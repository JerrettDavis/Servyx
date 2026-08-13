using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Common;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="CompositeServerDiscovery"/>. Each host is a substituted
/// <see cref="SshHostDouble"/> (same double <see cref="SshDockerServerDiscoveryTests"/> uses), wrapped in a
/// trivial fake <see cref="IHostConnectionSource"/> — no <see cref="HostConnectionRegistry"/>, no database, no
/// live SSH server or docker daemon anywhere in this file.
/// </summary>
public class CompositeServerDiscoveryTests
{
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";
    private const string RequiredMountPath = "/palworld";
    private const string PalworldContainerId = "1cae202fb5341a59ec34c72e63fbad9c33e054ce88f5b05e37bd756b729fa81e";

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static SshHostDouble CreatePalworldHost()
    {
        var lsOutput = ReadFixture("palworld-container-ls.jsonl");
        var inspectJson = ReadFixture("palworld-inspect.json");

        return new SshHostDouble
        {
            ExecHandler = command =>
            {
                if (command.Arguments.Contains("ls"))
                {
                    return new CommandResult(0, lsOutput, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                if (command.Arguments.Contains("inspect"))
                {
                    return new CommandResult(0, inspectJson, string.Empty, TimeSpan.FromMilliseconds(5));
                }

                throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
            },
        };
    }

    private static SshHostDouble CreateUnreachableHost() => new()
    {
        ExecHandler = _ => new CommandResult(1, string.Empty, "Cannot connect to the Docker daemon", TimeSpan.Zero),
    };

    private sealed class FakeHostConnectionSource(IReadOnlyList<HostConnection> connections) : IHostConnectionSource
    {
        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(connections);
    }

    [Fact]
    public async Task Fans_out_across_every_host_and_tags_each_result_with_its_host_key()
    {
        var alpha = CreatePalworldHost();
        var beta = CreatePalworldHost();
        var source = new FakeHostConnectionSource([
            new HostConnection("alpha-host", alpha.Session),
            new HostConnection("beta-host", beta.Session),
        ]);
        var discovery = new CompositeServerDiscovery(source);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().HaveCount(2);
        results.Should().Contain(s => s.HostKey == "alpha-host" && s.ServerId == PalworldContainerId);
        results.Should().Contain(s => s.HostKey == "beta-host" && s.ServerId == PalworldContainerId);
    }

    [Fact]
    public async Task One_unreachable_host_does_not_prevent_discovery_from_the_others()
    {
        var reachable = CreatePalworldHost();
        var unreachable = CreateUnreachableHost();
        var source = new FakeHostConnectionSource([
            new HostConnection("reachable-host", reachable.Session),
            new HostConnection("unreachable-host", unreachable.Session),
        ]);
        var discovery = new CompositeServerDiscovery(source);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var server = results.Should().ContainSingle().Subject;
        server.HostKey.Should().Be("reachable-host");
        server.ServerId.Should().Be(PalworldContainerId);
    }

    /// <summary>
    /// The counterpart to <see cref="One_unreachable_host_does_not_prevent_discovery_from_the_others"/>: a
    /// partial failure degrades, but a total one is reported. Returning an empty list when nothing answered is
    /// what let a mis-registered host render as "no containers available to adopt" — the caller cannot tell
    /// that apart from every host genuinely running nothing, so it shows the empty state instead of the
    /// degraded one and the operator never learns their host is broken.
    /// </summary>
    [Fact]
    public async Task Every_host_failing_is_reported_rather_than_passed_off_as_an_empty_result()
    {
        var first = CreateUnreachableHost();
        var second = CreateUnreachableHost();
        var source = new FakeHostConnectionSource([
            new HostConnection("first-host", first.Session),
            new HostConnection("second-host", second.Session),
        ]);
        var discovery = new CompositeServerDiscovery(source);

        var act = () => discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        // The message carries each host's own reason, since that is what the operator needs to act on.
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("first-host").And.Contain("second-host");
    }

    /// <summary>
    /// The zero-regression pin: with exactly one host, <see cref="CompositeServerDiscovery"/> must discover
    /// the same workload(s) <see cref="SshDockerServerDiscovery"/> would directly, on every field except the
    /// newly-added <see cref="DiscoveredServer.HostKey"/> tag — which the direct, single-host implementation
    /// never sets (it has no notion of "which host"), and the composite implementation always does.
    /// </summary>
    [Fact]
    public async Task With_a_single_host_the_discovered_workload_matches_the_direct_single_host_call_exactly()
    {
        var direct = CreatePalworldHost();
        var directDiscovery = new SshDockerServerDiscovery(direct.Session);
        var directResults = await directDiscovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var composedHost = CreatePalworldHost();
        var composite = new CompositeServerDiscovery(
            new FakeHostConnectionSource([new HostConnection("only-host", composedHost.Session)]));
        var compositeResults = await composite.DiscoverAsync(ImageRepository, RequiredMountPath);

        directResults.Should().ContainSingle();
        compositeResults.Should().ContainSingle();

        var compositeAsIfUntagged = compositeResults[0] with { HostKey = null };
        compositeAsIfUntagged.Should().BeEquivalentTo(directResults[0]);
        compositeResults[0].HostKey.Should().Be("only-host");
    }

    /// <summary>
    /// The exact scenario Increment 4b's fix targets: a fresh install with zero <c>Servyx:Hosts</c> config
    /// entries but one database-registered <see cref="Host"/> row. Before that fix,
    /// <c>AddServyxSshDocker</c> no-op'd entirely whenever <see cref="SshDockerWiringOptions.Any"/> was
    /// <see langword="false"/>, so <see cref="HostConnectionRegistry"/> (and therefore this
    /// <see cref="CompositeServerDiscovery"/> over it) never existed in the composition root at all — there was
    /// nothing for a database-registered host to be discovered through. This test goes over the real
    /// <see cref="HostConnectionRegistry"/> (not the trivial <see cref="FakeHostConnectionSource"/> the other
    /// tests in this file use) specifically to exercise that registry/discovery pairing with a genuinely empty
    /// configured half — see <see cref="Servyx.Infrastructure.Ssh.Tests.Docker.HostConnectionRegistryTests"/>
    /// for the registry's own unit coverage of the same combination.
    /// </summary>
    [Fact]
    public async Task Zero_configured_hosts_plus_one_database_registered_host_is_still_discovered()
    {
        var dbHost = new Host
        {
            Id = HostId.New(),
            Name = "db-only-host",
            ConnectorId = "ssh:db-only-host",
            Endpoint = "ssh:user@10.0.0.50:22",
            TrustPolicy = "trustOnFirstUse",
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var repository = Substitute.For<IHostRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([dbHost]));

        var host = CreatePalworldHost();
        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(host.Session));

        var registry = new HostConnectionRegistry(
            SshDockerWiringOptions.None, repository, transport, NullLogger<HostConnectionRegistry>.Instance);
        var discovery = new CompositeServerDiscovery(registry);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var server = results.Should().ContainSingle().Subject;
        server.HostKey.Should().Be("db-only-host");
        server.ServerId.Should().Be(PalworldContainerId);
    }

    /// <summary>
    /// The companion "nothing configured yet" case: zero config hosts AND zero database rows. Increment 4b's
    /// behavioral call — see <see cref="HostConnectionRegistry"/>'s remarks — is that this is a normal, empty
    /// deployment state, not an error: a fresh install has nothing to discover until an operator registers a
    /// host, and discovery should report that honestly rather than throw.
    /// </summary>
    [Fact]
    public async Task Zero_configured_hosts_and_zero_database_hosts_yields_an_empty_result_rather_than_throwing()
    {
        var repository = Substitute.For<IHostRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([]));

        var registry = new HostConnectionRegistry(
            SshDockerWiringOptions.None, repository, Substitute.For<ITransport>(), NullLogger<HostConnectionRegistry>.Instance);
        var discovery = new CompositeServerDiscovery(registry);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().BeEmpty();
    }
}
