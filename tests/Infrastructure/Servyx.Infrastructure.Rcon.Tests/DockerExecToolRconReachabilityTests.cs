using NSubstitute;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The <c>docker-exec-tool</c> reachability strategy: the one that actually reaches RCON on the adopted
/// Palworld container, where port 25575 is exposed but not published and <c>DirectTcpRconReachability</c>
/// can never succeed.
/// </summary>
public class DockerExecToolRconReachabilityTests
{
    private static readonly RconEndpoint Endpoint = new("127.0.0.1", 25575);
    private static readonly IReadOnlyList<string> ArgvTemplate = ["rcon-cli", "{command}"];

    private static RconCommandCatalog Catalog() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
    ]);

    private static IExecutionTarget FakeTarget(int exitCode = 0, string stdout = "")
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(exitCode, stdout, string.Empty, TimeSpan.Zero)));
        return target;
    }

    private static DockerExecToolRconReachability Strategy(IExecutionTarget target, RconCommandCatalog? catalog = null) =>
        new(target, "palworld-server", ArgvTemplate, catalog ?? Catalog());

    [Fact]
    public void Strategy_id_is_docker_exec_tool()
    {
        Strategy(FakeTarget()).StrategyId.Should().Be("docker-exec-tool");
    }

    [Fact]
    public async Task Is_available_when_the_rcon_cli_probe_exits_zero()
    {
        var target = FakeTarget(exitCode: 0);

        (await Strategy(target).IsAvailableAsync(Endpoint)).Should().BeTrue();
    }

    [Fact]
    public async Task Is_unavailable_when_the_probe_exits_non_zero()
    {
        var target = FakeTarget(exitCode: 1);

        (await Strategy(target).IsAvailableAsync(Endpoint)).Should().BeFalse();
    }

    [Fact]
    public async Task Is_unavailable_when_the_transport_throws()
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandResult>>(_ => throw new InvalidOperationException("no exec channel in this test"));

        var act = async () => await Strategy(target).IsAvailableAsync(Endpoint);

        (await act.Should().NotThrowAsync()).Which.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_uses_a_read_only_command_intent()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        await Strategy(target).IsAvailableAsync(Endpoint);

        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.ReadOnly);
        captured.Executable.Should().Be("docker");
        captured.Arguments.Should().Contain("which");
        captured.Arguments.Should().Contain("rcon-cli");
    }

    [Fact]
    public async Task Info_command_is_issued_with_read_only_intent()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, "reply", string.Empty, TimeSpan.Zero)));

        var session = await Strategy(target).AcquireAsync(Endpoint);
        await session.InvokeAsync("info", null);

        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.ReadOnly);
        captured.Arguments.Should().ContainInOrder("exec", "palworld-server", "rcon-cli", "Info");
    }

    [Fact]
    public async Task Players_command_is_issued_with_read_only_intent()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, "name,playeruid,steamid", string.Empty, TimeSpan.Zero)));

        var session = await Strategy(target).AcquireAsync(Endpoint);
        await session.InvokeAsync("players", null);

        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.ReadOnly);
        captured.Arguments.Should().ContainInOrder("exec", "palworld-server", "rcon-cli", "ShowPlayers");
    }

    [Fact]
    public async Task Save_command_is_classified_as_mutating()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        var session = await Strategy(target).AcquireAsync(Endpoint);
        await session.InvokeAsync("save", null);

        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public async Task Mutating_rcon_command_is_refused_by_the_write_guard()
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        var guarded = new WriteGuardedExecutionTarget(target, WriteMode.ReadOnly);
        var session = await new DockerExecToolRconReachability(guarded, "palworld-server", ArgvTemplate, Catalog())
            .AcquireAsync(Endpoint);

        var act = async () => await session.InvokeAsync("save", null);

        await act.Should().ThrowAsync<WritesDisabledException>();
        await target.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Command_with_spaces_stays_a_single_argv_element()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)));

        var catalog = new RconCommandCatalog(
        [
            new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
        ]);

        var session = await Strategy(target, catalog).AcquireAsync(Endpoint);
        await session.InvokeAsync("broadcast", new Dictionary<string, string> { ["message"] = "hello there world" });

        captured.Should().NotBeNull();

        // "Broadcast hello there world" must arrive as ONE argv element, not split into four.
        captured!.Arguments.Should().Contain("Broadcast hello there world");
        captured.Arguments.Count(a => a.Contains("hello")).Should().Be(1);
        captured.Arguments.Should().NotContain("hello");
        captured.Arguments.Should().NotContain("there");
        captured.Arguments.Should().NotContain("world");
    }

    [Fact]
    public async Task Chain_falls_through_to_docker_exec_tool_when_direct_tcp_is_unavailable()
    {
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint; // nothing is listening here anymore; direct-tcp will refuse.
        }

        var direct = new DirectTcpRconReachability(
            _ => throw new InvalidOperationException("direct-tcp must not be acquired"),
            TimeSpan.FromMilliseconds(200));

        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, "Welcome to Pal Server", string.Empty, TimeSpan.Zero)));

        var chain = new RconReachabilityChain(
        [
            direct,
            Strategy(target),
            UnavailableRconReachability.DockerExecNetwork,
        ]);

        var session = await chain.AcquireAsync(endpoint);
        var response = await session.InvokeAsync("info", null);

        session.Should().BeOfType<DockerExecToolRconSession>();
        response.Text.Should().Be("Welcome to Pal Server");
        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.ReadOnly);
    }

    [Fact]
    public async Task Acquiring_docker_exec_tool_no_longer_throws_NotSupportedException()
    {
        var strategy = Strategy(FakeTarget());

        var session = await strategy.AcquireAsync(Endpoint);

        session.Should().NotBeNull();
        strategy.StrategyId.Should().Be("docker-exec-tool");
    }

    [Fact]
    public async Task Raw_commands_over_the_docker_exec_path_are_refused_without_an_audit_sink()
    {
        var target = FakeTarget();

        // No IRconAuditSink supplied to the strategy -- the default, and the state of the composition root
        // today (RconReachabilityChainFactory wires no sink for either strategy yet).
        var session = await Strategy(target).AcquireAsync(Endpoint);

        var act = async () => await session.SendRawAsync("Info");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("audit");

        // Refused before anything is ever sent -- an unrecorded raw command must never reach the exec channel.
        await target.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Raw_commands_over_the_docker_exec_path_are_recorded_and_sent_once_an_audit_sink_is_wired()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, "raw reply", string.Empty, TimeSpan.Zero)));

        var audit = new RecordingAuditSink();
        var strategy = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, Catalog(), audit);

        var session = await strategy.AcquireAsync(Endpoint);
        var response = await session.SendRawAsync("SomeUndocumentedCommand 1");

        audit.Recorded.Should().ContainSingle().Which.Should().Be("SomeUndocumentedCommand 1");
        response.Text.Should().Be("raw reply");
        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.Mutating);
        captured.Arguments.Should().ContainInOrder("exec", "palworld-server", "SomeUndocumentedCommand 1");
    }

    [Fact]
    public async Task The_catalogued_InvokeAsync_path_still_works_normally_with_no_audit_sink_configured()
    {
        CommandSpec? captured = null;
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Do<CommandSpec>(spec => captured = spec), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(0, "Welcome to Pal Server", string.Empty, TimeSpan.Zero)));

        // No audit sink -- only the raw escape hatch needs one; catalogued commands never did.
        var session = await Strategy(target).AcquireAsync(Endpoint);

        var response = await session.InvokeAsync("info", null);

        response.Text.Should().Be("Welcome to Pal Server");
        captured.Should().NotBeNull();
        captured!.Intent.Should().Be(CommandIntent.ReadOnly);
        captured.Arguments.Should().ContainInOrder("exec", "palworld-server", "rcon-cli", "Info");
    }
}
