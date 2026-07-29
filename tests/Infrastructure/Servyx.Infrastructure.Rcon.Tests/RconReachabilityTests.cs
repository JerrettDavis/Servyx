using Servyx.Domain.Rcon;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The definition lists three reachability strategies in order. Exactly one of them is implemented, and the
/// other two say so rather than pretending.
/// </summary>
public class RconReachabilityTests
{
    private static DirectTcpRconReachability Direct(IRconSession? session = null) =>
        new(_ => session ?? new ScriptedRconSession(), TimeSpan.FromMilliseconds(750));

    [Fact]
    public async Task Direct_tcp_is_available_when_the_port_accepts()
    {
        await using var server = new FakeRconServer();

        (await Direct().IsAvailableAsync(server.Endpoint)).Should().BeTrue();
    }

    [Fact]
    public async Task Direct_tcp_is_unavailable_when_nothing_is_listening()
    {
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint;
        }

        // This is the real Palworld deployment's situation: the definition declares RCON 25575 with
        // published: false, so on that host direct-tcp genuinely cannot reach the container.
        (await Direct().IsAvailableAsync(endpoint)).Should().BeFalse();
    }

    [Fact]
    public async Task Direct_tcp_acquires_the_session_the_composition_root_supplied()
    {
        await using var server = new FakeRconServer();
        var expected = new ScriptedRconSession();

        (await Direct(expected).AcquireAsync(server.Endpoint)).Should().BeSameAs(expected);
    }

    [Theory]
    [InlineData("docker-exec-tool")]
    [InlineData("docker-exec-network")]
    [InlineData("ssh-tunnel")]
    public async Task An_unimplemented_strategy_reports_itself_unavailable_rather_than_pretending(string strategyId)
    {
        var strategy = Unimplemented(strategyId);

        strategy.StrategyId.Should().Be(strategyId);
        (await strategy.IsAvailableAsync(new RconEndpoint("127.0.0.1", 25575))).Should().BeFalse();
    }

    [Fact]
    public async Task An_unimplemented_strategy_refuses_to_be_acquired_and_says_why()
    {
        var act = async () => await UnavailableRconReachability.DockerExecTool.AcquireAsync(new RconEndpoint("127.0.0.1", 25575));

        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain("rcon-cli");
    }

    [Fact]
    public async Task The_ordered_chain_takes_the_first_strategy_that_is_available()
    {
        await using var server = new FakeRconServer();
        var expected = new ScriptedRconSession();

        // The definition's own order: direct-tcp, then docker-exec-tool, then docker-exec-network.
        var chain = new RconReachabilityChain(
        [
            Direct(expected),
            UnavailableRconReachability.DockerExecTool,
            UnavailableRconReachability.DockerExecNetwork,
        ]);

        (await chain.AcquireAsync(server.Endpoint)).Should().BeSameAs(expected);
    }

    [Fact]
    public async Task The_chain_names_every_strategy_it_tried_when_none_can_reach_the_endpoint()
    {
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint;
        }

        var chain = new RconReachabilityChain(
        [
            Direct(),
            UnavailableRconReachability.DockerExecTool,
            UnavailableRconReachability.DockerExecNetwork,
        ]);

        var act = async () => await chain.AcquireAsync(endpoint);

        var message = (await act.Should().ThrowAsync<RconUnreachableException>()).Which.Message;
        message.Should().Contain("direct-tcp");
        message.Should().Contain("docker-exec-tool");
        message.Should().Contain("docker-exec-network");
    }

    [Fact]
    public void An_empty_chain_is_a_composition_error()
    {
        var act = () => new RconReachabilityChain([]);

        act.Should().Throw<ArgumentException>();
    }

    private static UnavailableRconReachability Unimplemented(string strategyId) => strategyId switch
    {
        "docker-exec-tool" => UnavailableRconReachability.DockerExecTool,
        "docker-exec-network" => UnavailableRconReachability.DockerExecNetwork,
        _ => UnavailableRconReachability.SshTunnel,
    };
}
