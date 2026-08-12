using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="HostConnectionRegistry"/>: the combined configured + database-registered host set
/// <see cref="CompositeServerDiscovery"/> fans discovery out across. <see cref="IHostRepository"/> is a
/// hand-written in-memory fake (mutable, so <c>Invalidate</c> can be exercised across a change), and
/// <see cref="ITransport"/> is a trivial NSubstitute stub that hands back a distinguishable marker
/// <see cref="IExecutionTarget"/> per endpoint. No real SSH connection, database, or docker daemon is involved.
/// </summary>
public class HostConnectionRegistryTests
{
    private sealed class FakeHostRepository : IHostRepository
    {
        public List<Host> Rows { get; } = [];

        public Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Host>>(Rows);

        public Task<Host?> TryGetAsync(HostId id, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by these tests.");

        public Task<Host?> TryGetByNameAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by these tests.");

        public Task AddAsync(Host host, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by these tests.");

        public Task<bool> RemoveAsync(HostId id, CancellationToken ct = default) =>
            throw new NotSupportedException("Not needed by these tests.");
    }

    private static Host NewHost(string name, string endpoint, bool enabled = true) => new()
    {
        Id = HostId.New(),
        Name = name,
        ConnectorId = $"ssh:{name}",
        Endpoint = endpoint,
        TrustPolicy = "trustOnFirstUse",
        Enabled = enabled,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static SshDockerHost NewConfiguredHost(string name, string endpoint) => new(
        name,
        new TargetDescriptor(
            SshDockerWiringOptions.TransportIdValue,
            endpoint,
            null,
            null,
            new Dictionary<string, string> { ["declaredChannels"] = SshDockerWiringOptions.DeclaredChannels }),
        ContainerName: "palworld-server");

    /// <summary>A stub transport that hands back a fresh substituted <see cref="IExecutionTarget"/> per call.</summary>
    private static ITransport StubTransport()
    {
        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));
        return transport;
    }

    private static HostConnectionRegistry BuildRegistry(
        IEnumerable<SshDockerHost> configuredHosts, FakeHostRepository repository, ITransport? transport = null) =>
        new(
            new SshDockerWiringOptions(configuredHosts),
            repository,
            transport ?? StubTransport(),
            NullLogger<HostConnectionRegistry>.Instance);

    [Fact]
    public async Task With_one_configured_host_and_zero_database_rows_exactly_that_host_is_returned()
    {
        var repository = new FakeHostRepository();
        var registry = BuildRegistry([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")], repository);

        var connections = await registry.GetConnectionsAsync();

        var entry = connections.Should().ContainSingle().Subject;
        entry.HostKey.Should().Be("configured-host");
    }

    [Fact]
    public async Task A_database_registered_host_is_included_alongside_the_configured_host()
    {
        var repository = new FakeHostRepository();
        repository.Rows.Add(NewHost("db-host", "db-host.example.com:22"));

        var registry = BuildRegistry([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")], repository);

        var connections = await registry.GetConnectionsAsync();

        connections.Should().HaveCount(2);
        connections.Should().Contain(c => c.HostKey == "configured-host");
        connections.Should().Contain(c => c.HostKey == "db-host");
    }

    [Fact]
    public async Task A_disabled_database_host_is_not_included()
    {
        var repository = new FakeHostRepository();
        repository.Rows.Add(NewHost("disabled-host", "disabled.example.com:22", enabled: false));

        var registry = BuildRegistry([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")], repository);

        var connections = await registry.GetConnectionsAsync();

        connections.Should().ContainSingle().Which.HostKey.Should().Be("configured-host");
    }

    /// <summary>
    /// The precedence rule this type's remarks document as deliberate: a configured host is authoritative over
    /// a database row registered under the same name, never the other way round.
    /// </summary>
    [Fact]
    public async Task A_configured_host_wins_over_a_database_row_with_the_same_name()
    {
        var repository = new FakeHostRepository();
        repository.Rows.Add(NewHost("shared-name", "db-endpoint.example.com:22"));

        var executionTarget = Substitute.For<IExecutionTarget>();
        executionTarget.ExistsAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(executionTarget));

        var registry = BuildRegistry(
            [NewConfiguredHost("shared-name", "ssh:user@10.0.0.9:22")], repository, transport);

        var connections = await registry.GetConnectionsAsync();

        var entry = connections.Should().ContainSingle().Subject;
        entry.HostKey.Should().Be("shared-name");

        // Prove it is really the CONFIGURED descriptor that gets used to connect, not the database row's —
        // the lazy target only actually calls ITransport.ConnectAsync on first real use.
        await entry.ExecutionTarget.ExistsAsync(default);

        await transport.Received(1).ConnectAsync(
            Arg.Is<TargetDescriptor>(d => d != null && d.Endpoint == "ssh:user@10.0.0.9:22"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalidate_causes_the_next_call_to_see_a_newly_registered_database_host()
    {
        var repository = new FakeHostRepository();
        var registry = BuildRegistry([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")], repository);

        (await registry.GetConnectionsAsync()).Should().ContainSingle();

        repository.Rows.Add(NewHost("newly-registered", "new.example.com:22"));
        (await registry.GetConnectionsAsync()).Should().ContainSingle(
            "the cache has not been invalidated yet, so the newly-added row must not be visible");

        registry.Invalidate();

        (await registry.GetConnectionsAsync()).Should().HaveCount(2,
            "Invalidate() must force the next call to re-read the database");
    }

    /// <summary>
    /// The same refresh, reached the way the host-registration use case actually reaches it: through
    /// <see cref="IHostConnectionRefresher"/>, the one-method Domain view this type implements so
    /// <c>Servyx.Application</c> can say "your cache is stale" without depending on this project (and without
    /// being handed the ability to enumerate or connect to hosts as a side effect).
    /// </summary>
    [Fact]
    public async Task Invalidating_through_the_domain_refresher_interface_has_the_same_effect_as_calling_Invalidate()
    {
        var repository = new FakeHostRepository();
        var registry = BuildRegistry([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")], repository);

        (await registry.GetConnectionsAsync()).Should().ContainSingle();

        repository.Rows.Add(NewHost("newly-registered", "new.example.com:22"));

        IHostConnectionRefresher refresher = registry;
        refresher.Invalidate();

        (await registry.GetConnectionsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_database_read_failure_degrades_to_the_configured_set_rather_than_failing_the_whole_registry()
    {
        var repository = Substitute.For<IHostRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<Host>>>(_ => throw new InvalidOperationException("db unreachable"));

        var registry = new HostConnectionRegistry(
            new SshDockerWiringOptions([NewConfiguredHost("configured-host", "ssh:user@10.0.0.9:22")]),
            repository,
            StubTransport(),
            NullLogger<HostConnectionRegistry>.Instance);

        var connections = await registry.GetConnectionsAsync();

        connections.Should().ContainSingle().Which.HostKey.Should().Be("configured-host");
    }
}
