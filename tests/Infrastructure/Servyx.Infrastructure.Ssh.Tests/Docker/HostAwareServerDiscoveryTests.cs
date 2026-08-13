using NSubstitute;
using Servyx.Domain.Discovery;
using Servyx.Infrastructure.Ssh.Docker;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="HostAwareServerDiscovery"/> — the seam that lets a database-registered host
/// become discoverable on a zero-<c>Servyx:Hosts</c> install without ever silencing local Docker discovery
/// for the (far more common) install that registers nothing at all, AND without silencing local Docker
/// discovery for an install that already has an adopted local server once a host IS registered — a
/// registered/configured host only ever adds a discovery source, never displaces the local one.
/// <see cref="IHostConnectionSource"/>, the "remote" <see cref="IServerDiscovery"/>, and the "local"
/// <see cref="IServerDiscovery"/> are all plain NSubstitute doubles — no real SSH connection or Docker
/// daemon anywhere in this file.
/// </summary>
public class HostAwareServerDiscoveryTests
{
    private const string ImageRepository = "thijsvanloef/palworld-server-docker";
    private const string RequiredMountPath = "/palworld";

    private static DiscoveredServer MakeServer(string id, string? hostKey = null) => new(
        ServerId: id,
        Name: id,
        Image: $"{ImageRepository}:latest",
        ImageDigest: "sha256:abc",
        State: "running",
        HealthStatus: "healthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: [],
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>(),
        HostKey: hostKey);

    private sealed class FakeHostConnectionSource(IReadOnlyList<HostConnection> connections) : IHostConnectionSource
    {
        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(connections);
    }

    private static IServerDiscovery StubDiscovery(params DiscoveredServer[] servers)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>(servers));
        return discovery;
    }

    private static IServerDiscovery FailingDiscovery(Exception failure)
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw failure);
        return discovery;
    }

    [Fact]
    public async Task Zero_hosts_defers_to_local_discovery_not_the_remote_one()
    {
        var connections = new FakeHostConnectionSource([]);
        var local = StubDiscovery(MakeServer("local-container"));
        var remote = StubDiscovery(MakeServer("remote-container"));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().ContainSingle().Which.ServerId.Should().Be("local-container");
        await remote.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task At_least_one_host_unions_remote_discovery_with_local_discovery()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("db-only-host", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ]);
        var local = StubDiscovery(MakeServer("local-container"));
        var remote = StubDiscovery(MakeServer("remote-container", hostKey: "db-only-host"));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().HaveCount(2,
            "a registered host must ADD a discovery source, not replace the local one — an already-adopted " +
            "local server must not vanish from the dashboard the instant a remote host is registered");
        results.Should().Contain(s => s.ServerId == "local-container" && s.HostKey == null);
        results.Should().Contain(s => s.ServerId == "remote-container" && s.HostKey == "db-only-host");
    }

    /// <summary>
    /// The exact regression the reviewer flagged: an operator with an already-adopted LOCAL server registers
    /// a remote SSH host through the <c>/hosts</c> UI. Both containers must remain simultaneously visible to
    /// every discovery-backed read (dashboard status, start/stop, server detail) — not just the one whichever
    /// branch of the old either/or happened to pick.
    /// </summary>
    [Fact]
    public async Task Local_container_and_remote_hosts_container_both_appear_together()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("newly-registered-host", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ]);
        var local = StubDiscovery(MakeServer("already-adopted-local-server"));
        var remote = StubDiscovery(MakeServer("remote-hosts-container", hostKey: "newly-registered-host"));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Select(s => s.ServerId).Should().BeEquivalentTo(
            ["already-adopted-local-server", "remote-hosts-container"]);
    }

    /// <summary>
    /// The exact "registered through the UI, no restart" scenario: the connection source's answer changes
    /// between two calls on the SAME <see cref="HostAwareServerDiscovery"/> instance (mirroring
    /// <see cref="Servyx.Infrastructure.Ssh.Docker.HostConnectionRegistry.Invalidate"/> making a
    /// newly-registered host visible on the very next call), and this type must react on the next call rather
    /// than pinning whatever it saw on the first one.
    /// </summary>
    [Fact]
    public async Task Adds_remote_discovery_alongside_local_across_calls_once_a_host_becomes_visible_without_reconstruction()
    {
        var mutableConnections = new MutableHostConnectionSource();
        var local = StubDiscovery(MakeServer("local-container"));
        var remote = StubDiscovery(MakeServer("remote-container", hostKey: "newly-registered"));

        var discovery = new HostAwareServerDiscovery(mutableConnections, remote, local);

        (await discovery.DiscoverAsync(ImageRepository, RequiredMountPath))
            .Should().ContainSingle().Which.ServerId.Should().Be("local-container",
                "nothing is registered yet, so the very first call must still see local containers");

        mutableConnections.Connections = [
            new HostConnection("newly-registered", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ];

        (await discovery.DiscoverAsync(ImageRepository, RequiredMountPath))
            .Select(s => s.ServerId).Should().BeEquivalentTo(["local-container", "remote-container"],
                "a host registered after construction must be discoverable on the very next call, matching " +
                "HostConnectionRegistry's own restart-free refresh contract — and it must ADD to local " +
                "discovery, not replace it");
    }

    /// <summary>
    /// The exact bug a real deployment hit: an operator's local Docker daemon is unreachable (Docker Desktop
    /// not running) while a remote SSH host they just registered is perfectly healthy. Before this fix,
    /// <c>Task.WhenAll(localTask, remoteTask)</c> propagated the local failure and lost the remote host's
    /// results entirely, rendering the adoption panel "could not be read" even though a container WAS
    /// discoverable. One source failing must not blank out the other, mirroring
    /// <see cref="CompositeServerDiscovery"/>'s own per-host isolation.
    /// </summary>
    [Fact]
    public async Task Local_source_failing_does_not_prevent_discovery_from_the_remote_source()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("registered-host", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ]);
        var local = FailingDiscovery(new InvalidOperationException("Docker engine unreachable: The operation has timed out."));
        var remote = StubDiscovery(MakeServer("remote-container", hostKey: "registered-host"));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().ContainSingle().Which.ServerId.Should().Be("remote-container");
    }

    /// <summary>The mirror image: a broken remote host must not blank out an already-adopted local server.</summary>
    [Fact]
    public async Task Remote_source_failing_does_not_prevent_discovery_from_the_local_source()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("registered-host", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ]);
        var local = StubDiscovery(MakeServer("local-container"));
        var remote = FailingDiscovery(new InvalidOperationException("Discovery failed on every host."));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var results = await discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        results.Should().ContainSingle().Which.ServerId.Should().Be("local-container");
    }

    /// <summary>
    /// The counterpart to the two isolation tests above: a partial failure degrades, but a total one is
    /// reported — an empty result would be indistinguishable from "nothing to adopt", exactly the reasoning
    /// <see cref="CompositeServerDiscovery"/>'s own every-host-failed case already uses.
    /// </summary>
    [Fact]
    public async Task Both_sources_failing_is_reported_rather_than_passed_off_as_an_empty_result()
    {
        var connections = new FakeHostConnectionSource([
            new HostConnection("registered-host", Substitute.For<Servyx.Domain.Transport.IExecutionTarget>()),
        ]);
        var local = FailingDiscovery(new InvalidOperationException("local-daemon-down"));
        var remote = FailingDiscovery(new InvalidOperationException("remote-host-unreachable"));

        var discovery = new HostAwareServerDiscovery(connections, remote, local);

        var act = () => discovery.DiscoverAsync(ImageRepository, RequiredMountPath);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.And.Message.Should().Contain("local-daemon-down").And.Contain("remote-host-unreachable");
    }

    private sealed class MutableHostConnectionSource : IHostConnectionSource
    {
        internal IReadOnlyList<HostConnection> Connections { get; set; } = [];

        public Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Connections);
    }
}
