using System.Net;
using System.Net.Sockets;
using NSubstitute;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The reachability chain <see cref="ServyxRconChannels"/> acquires a session through: composed by
/// <see cref="RconReachabilityChainFactory"/> in the definition's declared order, falling back correctly when
/// the preferred strategy cannot reach the endpoint, and — the reason any of this exists — still wrapped in
/// <see cref="WriteGuardedRconSession"/> no matter which strategy in the chain actually answered.
/// </summary>
public class RconReachabilityChainWiringTests
{
    private const string Container = "palworld-server";

    private static readonly SecretUrn PasswordUrn = SecretUrn.Create("server", Container, "rcon", "password");

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    private static RconChannel Channel(RconEndpoint endpoint) => new(Container, endpoint, PasswordUrn);

    /// <summary>An endpoint nothing is listening on: bound to grab a free ephemeral port, then released.</summary>
    private static RconEndpoint UnusedEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new RconEndpoint("127.0.0.1", port);
    }

    private static IExecutionTarget FakeExecutionTarget(int exitCode = 0, string stdout = "reply")
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(exitCode, stdout, string.Empty, TimeSpan.Zero)));
        return target;
    }

    [Fact]
    public void The_chain_is_composed_in_the_definitions_declared_order()
    {
        var chain = RconReachabilityChainFactory.Build(
            Channel(new RconEndpoint("127.0.0.1", 25575)),
            new SourceRconClient(),
            Palworld(),
            new Fakes.RecordingSecretStore(),
            Container,
            FakeExecutionTarget());

        chain.Strategies.Select(s => s.StrategyId).Should().Equal("direct-tcp", "docker-exec-tool", "docker-exec-network");
    }

    [Fact]
    public void When_no_remote_host_is_configured_the_exec_strategy_is_absent_and_startup_succeeds()
    {
        // No container name, no IExecutionTarget — exactly what Program.cs supplies when
        // SshDockerWiringOptions.Any is false. Composition itself must not throw.
        var chain = RconReachabilityChainFactory.Build(
            Channel(new RconEndpoint("127.0.0.1", 25575)),
            new SourceRconClient(),
            Palworld(),
            new Fakes.RecordingSecretStore(),
            containerName: null,
            executionTarget: null);

        chain.Strategies.Select(s => s.StrategyId).Should().Equal("direct-tcp", "docker-exec-network");
    }

    [Fact]
    public async Task Direct_tcp_is_tried_before_docker_exec_tool()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new RconEndpoint("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);

        var target = FakeExecutionTarget();
        var chain = RconReachabilityChainFactory.Build(
            Channel(endpoint), new SourceRconClient(), Palworld(), new Fakes.RecordingSecretStore(), Container, target);

        var session = await chain.AcquireAsync(endpoint);

        // direct-tcp answered first, so docker-exec-tool's probe never ran against the exec target at all.
        session.Should().NotBeOfType<DockerExecToolRconSession>();
        await target.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_direct_tcp_is_unavailable_the_exec_strategy_is_used()
    {
        var endpoint = UnusedEndpoint();
        var target = FakeExecutionTarget();

        var chain = RconReachabilityChainFactory.Build(
            Channel(endpoint), new SourceRconClient(), Palworld(), new Fakes.RecordingSecretStore(), Container, target);

        var session = await chain.AcquireAsync(endpoint);

        session.Should().BeOfType<DockerExecToolRconSession>();
    }

    [Fact]
    public async Task Unreachable_chain_reports_why_each_strategy_failed()
    {
        var endpoint = UnusedEndpoint();
        var target = FakeExecutionTarget(exitCode: 1, stdout: string.Empty);

        var chain = RconReachabilityChainFactory.Build(
            Channel(endpoint), new SourceRconClient(), Palworld(), new Fakes.RecordingSecretStore(), Container, target);

        var act = async () => await chain.AcquireAsync(endpoint);

        var message = (await act.Should().ThrowAsync<RconUnreachableException>()).Which.Message;
        message.Should().Contain("direct-tcp");
        message.Should().Contain("docker-exec-tool");
        message.Should().Contain("docker-exec-network");

        // Not just the strategy ids — each strategy's own LastUnavailableReason is folded in too.
        message.Should().Contain("exited 1");
    }

    [Fact]
    public async Task A_chain_acquired_session_still_refuses_mutating_commands_under_read_only()
    {
        var channels = ChannelsOver(GuardTestChainFactory(), WritableServers.None);

        var session = await channels.GetSessionAsync(Container);

        session.Should().BeOfType<WriteGuardedRconSession>();

        var act = async () => await session!.InvokeAsync("save", null);
        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task A_chain_acquired_session_still_refuses_send_raw()
    {
        var channels = ChannelsOver(GuardTestChainFactory(), WritableServers.None);

        var session = await channels.GetSessionAsync(Container);

        var act = async () => await session!.SendRawAsync("Shutdown");
        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Sessions_are_memoized_per_channel()
    {
        var calls = 0;
        Func<RconChannel, RconReachabilityChain> chainFactory = channel =>
        {
            calls++;
            return new RconReachabilityChain(
            [
                new Fakes.AlwaysAvailableRconReachability(endpoint =>
                    new RconSession(new SourceRconClient(), endpoint, Palworld(), new Fakes.RecordingSecretStore(), channel.PasswordUrn)),
            ]);
        };

        var channels = ChannelsOver(chainFactory, new WritableServers([Container]));

        var first = await channels.GetSessionAsync(Container);
        var second = await channels.GetSessionAsync(Container);

        second.Should().BeSameAs(first);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_acquisition_is_not_cached_permanently()
    {
        var attempt = 0;
        var successSession = new RconSession(
            new SourceRconClient(), new RconEndpoint("127.0.0.1", 25575), Palworld(), new Fakes.RecordingSecretStore(), PasswordUrn);

        Func<RconChannel, RconReachabilityChain> chainFactory = _ => new RconReachabilityChain(
        [
            new Fakes.AlwaysAvailableRconReachability(endpoint =>
            {
                attempt++;
                return attempt == 1
                    ? throw new InvalidOperationException("transient failure reaching the endpoint")
                    : successSession;
            }),
        ]);

        var channels = ChannelsOver(chainFactory, new WritableServers([Container]));

        var firstCall = async () => await channels.GetSessionAsync(Container);
        await firstCall.Should().ThrowAsync<InvalidOperationException>();

        // Improves on ServyxBackupContextSource.SessionAsync's Lazy<Task<T>> memoization, which would cache
        // the faulted task forever and replay the same stale failure to every future caller. Here, the next
        // call retries and succeeds.
        var second = await channels.GetSessionAsync(Container);

        second.Should().BeOfType<WriteGuardedRconSession>();
        attempt.Should().Be(2);
    }

    private static Func<RconChannel, RconReachabilityChain> GuardTestChainFactory() => channel =>
        new RconReachabilityChain(
        [
            new Fakes.AlwaysAvailableRconReachability(endpoint =>
                new RconSession(new SourceRconClient(), endpoint, Palworld(), new Fakes.RecordingSecretStore(), channel.PasswordUrn)),
        ]);

    private static ServyxRconChannels ChannelsOver(Func<RconChannel, RconReachabilityChain> chainFactory, WritableServers writable) =>
        new(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            Palworld(),
            new SourceRconClient(),
            new Fakes.RecordingSecretStore(),
            writable,
            chainFactory: chainFactory);
}
