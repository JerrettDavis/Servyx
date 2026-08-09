using Microsoft.EntityFrameworkCore;
using Servyx.Composition;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The resolver the whole write guard hangs off. Its invariants are the non-negotiable ones from the plan:
/// a closed master switch is read-only WITHOUT the database being touched at all; a missing row is
/// read-only; a container name is never an identity; and a grant written a moment ago is visible.
/// </summary>
public class DbBackedWriteModeResolverTests : IDisposable
{
    private const string ContainerId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherContainerId = "2222222222222222222222222222222222222222222222222222222222222222";

    private readonly WriteGrantTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> that records how many contexts were ever asked for, so a
    /// test can assert the database was not merely "not written to" but not even OPENED.
    /// </summary>
    private sealed class CountingContextFactory(WriteGrantTestDatabase owner) : IDbContextFactory<ServyxDbContext>
    {
        public int Created { get; private set; }

        public ServyxDbContext CreateDbContext()
        {
            Created++;
            return owner.CreateContext();
        }
    }

    private static TargetDescriptor Docker(params (string Key, string Value)[] options) => new(
        "docker",
        "npipe://./pipe/docker_engine",
        null,
        null,
        options.ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal));

    private DbBackedWriteModeResolver Resolver(ProvisioningGate gate, IDbContextFactory<ServyxDbContext>? factory = null) =>
        new(gate,
            new WriteGrantCache(gate, gate.Enabled ? factory ?? _database.Factory : null),
            new GrantedWriteModeResolver([]));

    [Fact]
    public void A_closed_master_switch_resolves_read_only_and_never_opens_the_database()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);
        var factory = new CountingContextFactory(_database);

        // Constructed exactly as the composition root does with the gate closed: no factory is even handed
        // to the cache. The counting factory is passed to the resolver's fallback path instead, so if any
        // code path ever reached for a context this assertion would catch it.
        var resolver = new DbBackedWriteModeResolver(
            ProvisioningGate.Closed,
            new WriteGrantCache(ProvisioningGate.Closed, factory),
            new GrantedWriteModeResolver(
            [
                new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containerId"] = ContainerId,
                }),
            ]));

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.ReadOnly,
            because: "with the master switch closed there is no grant in this process at all, whatever any " +
                "row or any configured grant says");

        factory.Created.Should().Be(0,
            because: "a read-only host's behaviour must not depend on whether its database is even reachable — " +
                "the gate short-circuits before the store, not after it");
    }

    [Fact]
    public void A_container_with_no_row_resolves_read_only()
    {
        // The single most important line in this change: an unknown target is a read-only one, always.
        Resolver(new ProvisioningGate(enabled: true))
            .Resolve(Docker(("containerId", ContainerId)))
            .Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_descriptor_naming_no_container_at_all_resolves_read_only()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        Resolver(new ProvisioningGate(enabled: true)).Resolve(Docker()).Should().Be(WriteMode.ReadOnly);
    }

    [Theory]
    [InlineData(ServerWriteMode.ReadOnly, WriteMode.ReadOnly)]
    [InlineData(ServerWriteMode.PreviewOnly, WriteMode.PreviewOnly)]
    [InlineData(ServerWriteMode.Enabled, WriteMode.Enabled)]
    public void Each_recorded_tier_resolves_as_itself(ServerWriteMode recorded, WriteMode expected)
    {
        _database.AddServer(ContainerId, "palworld-server", recorded);

        Resolver(new ProvisioningGate(enabled: true))
            .Resolve(Docker(("containerId", ContainerId)))
            .Should().Be(expected);
    }

    [Fact]
    public void A_grant_for_one_container_never_reaches_another()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);
        _database.AddServer(OtherContainerId, "minecraft-server", ServerWriteMode.ReadOnly);

        var resolver = Resolver(new ProvisioningGate(enabled: true));

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.Enabled);
        resolver.Resolve(Docker(("containerId", OtherContainerId))).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_container_name_alone_never_satisfies_a_grant()
    {
        // The "recreated container inherits the old grant" hole, closed. The row names a container id; a
        // descriptor that only knows a name cannot prove it is the same workload, so it is refused.
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        Resolver(new ProvisioningGate(enabled: true))
            .Resolve(Docker(("containerName", "palworld-server")))
            .Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_recreated_container_with_the_same_name_but_a_new_id_is_refused()
    {
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var resolver = Resolver(new ProvisioningGate(enabled: true));

        resolver.Resolve(Docker(("containerId", OtherContainerId), ("containerName", "palworld-server")))
            .Should().Be(WriteMode.ReadOnly,
                because: "destroying and recreating a container produces a workload the operator never " +
                    "granted anything to, even though it answers to the same name");
    }

    [Fact]
    public void A_renamed_container_keeps_its_grant_because_the_identity_did_not_change()
    {
        // Asserted deliberately, so a future change cannot silently reverse it: a rename is a cosmetic
        // change to the same workload, and revoking on one would be a surprise with no safety payoff.
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        Resolver(new ProvisioningGate(enabled: true))
            .Resolve(Docker(("containerId", ContainerId), ("containerName", "renamed-in-docker")))
            .Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void A_target_on_another_transport_falls_through_to_the_configured_grants()
    {
        // ssh+docker containers and SSH backup endpoints name a host the operator declared explicitly in
        // configuration; no adoption path mints a row for them, so their grants are still config-driven.
        var gate = new ProvisioningGate(enabled: true);
        var resolver = new DbBackedWriteModeResolver(
            gate,
            new WriteGrantCache(gate, _database.Factory),
            new GrantedWriteModeResolver(
            [
                new WriteModeGrant(WriteMode.Enabled, "ssh+docker", endpoint: "ssh://host:22",
                    requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "remote" }),
            ]));

        var overSshDocker = new TargetDescriptor(
            "ssh+docker",
            "ssh://host:22",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "remote" });

        resolver.Resolve(overSshDocker).Should().Be(WriteMode.Enabled);
    }

    [Fact]
    public void A_configured_grant_can_never_re_grant_an_adopted_docker_container()
    {
        // Two sources of truth for one decision is the ambiguity this change exists to remove. A leftover
        // WriteModeGrant naming a docker container must not override the row.
        var gate = new ProvisioningGate(enabled: true);
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var resolver = new DbBackedWriteModeResolver(
            gate,
            new WriteGrantCache(gate, _database.Factory),
            new GrantedWriteModeResolver(
            [
                new WriteModeGrant(WriteMode.Enabled, "docker", requiredOptions: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containerId"] = ContainerId,
                }),
            ]));

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public async Task A_grant_written_a_moment_ago_is_visible_to_the_very_next_resolve()
    {
        var gate = new ProvisioningGate(enabled: true);
        var id = _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.ReadOnly);

        var cache = new WriteGrantCache(gate, _database.Factory);
        var resolver = new DbBackedWriteModeResolver(gate, cache, new GrantedWriteModeResolver([]));

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.ReadOnly);

        await new WriteGrantService(gate, _database.Repository, cache, new RecordingLogger())
            .SetWriteModeAsync(id, ServerWriteMode.Enabled, "operator");

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.Enabled,
            because: "the grant service invalidates the cache before returning, so a UI flip is visible to " +
                "the next command issued anywhere in this process");
    }

    [Fact]
    public void The_cache_reads_the_database_once_and_then_serves_from_memory()
    {
        // Re-resolution now happens per guarded command, so this is a correctness property, not a
        // micro-optimisation: a database round-trip per docker exec would be unacceptable.
        var gate = new ProvisioningGate(enabled: true);
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var factory = new CountingContextFactory(_database);
        var resolver = new DbBackedWriteModeResolver(
            gate, new WriteGrantCache(gate, factory), new GrantedWriteModeResolver([]));

        for (var i = 0; i < 25; i++)
        {
            resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.Enabled);
        }

        factory.Created.Should().Be(1);
    }

    [Fact]
    public void An_unreadable_grant_store_fails_closed_rather_than_open()
    {
        var gate = new ProvisioningGate(enabled: true);
        _database.AddServer(ContainerId, "palworld-server", ServerWriteMode.Enabled);

        var logger = new RecordingLogger();
        var resolver = new DbBackedWriteModeResolver(
            gate,
            new WriteGrantCache(gate, new ThrowingContextFactory(), logger),
            new GrantedWriteModeResolver([]));

        resolver.Resolve(Docker(("containerId", ContainerId))).Should().Be(WriteMode.ReadOnly);
        logger.Entries.Should().NotBeEmpty(
            because: "silently treating every server as read-only because the store broke is safe, but it " +
                "still has to be diagnosable");
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<ServyxDbContext>
    {
        public ServyxDbContext CreateDbContext() => throw new InvalidOperationException("the store is unavailable");
    }
}
