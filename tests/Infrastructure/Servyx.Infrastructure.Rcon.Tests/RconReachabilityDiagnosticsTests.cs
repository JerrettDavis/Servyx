using NSubstitute;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// When Servyx cannot reach a game server's RCON endpoint, <see cref="RconUnreachableException"/> used to
/// name only the strategy ids that were tried, never why each one declined. These tests cover the fix: every
/// <see cref="IRconReachability"/> implementation now records a short, non-secret
/// <see cref="IRconReachability.LastUnavailableReason"/>, and <see cref="RconReachabilityChain"/> folds it
/// into the exception message.
/// </summary>
public class RconReachabilityDiagnosticsTests
{
    private static readonly IReadOnlyList<string> ArgvTemplate = ["rcon-cli", "{command}"];

    [Fact]
    public async Task Unreachable_exception_names_why_each_strategy_failed()
    {
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint; // nothing is listening here anymore; direct-tcp will refuse.
        }

        var direct = new DirectTcpRconReachability(
            _ => throw new InvalidOperationException("direct-tcp must not be acquired"),
            TimeSpan.FromMilliseconds(200));

        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(127, string.Empty, "exec: \"rcon-cli\": not found", TimeSpan.Zero)));
        var dockerExecTool = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, RconCommandCatalog.Empty);

        var chain = new RconReachabilityChain([direct, dockerExecTool, UnavailableRconReachability.DockerExecNetwork]);

        var act = async () => await chain.AcquireAsync(endpoint);
        var message = (await act.Should().ThrowAsync<RconUnreachableException>()).Which.Message;

        // Strategy ids are still named, exactly as before...
        message.Should().Contain("direct-tcp");
        message.Should().Contain("docker-exec-tool");
        message.Should().Contain("docker-exec-network");

        // ...but now each carries WHY it declined. direct-tcp's own reason depends on how the OS answers a
        // connect to a port nothing is listening on anymore - either an immediate refusal or the probe
        // window elapsing - so accept either rather than pin down OS-specific socket behaviour.
        message.Should().MatchRegex("direct-tcp \\((TCP connect failed|timed out waiting)");
        message.Should().Contain("exited 127");
        message.Should().Contain("sibling container");
    }

    [Fact]
    public async Task Direct_tcp_reports_a_connection_failure_reason()
    {
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint; // nothing is listening here anymore.
        }

        var direct = new DirectTcpRconReachability(
            _ => throw new InvalidOperationException("must not be acquired"),
            TimeSpan.FromMilliseconds(200));

        direct.LastUnavailableReason.Should().BeNull("no probe has run yet");

        (await direct.IsAvailableAsync(endpoint)).Should().BeFalse();

        // Depending on how the OS answers a connect to a now-closed loopback port, this is either an
        // immediate refusal or the probe window elapsing - either is a valid, actionable reason.
        direct.LastUnavailableReason.Should().NotBeNullOrWhiteSpace();
        direct.LastUnavailableReason.Should().MatchRegex("^(TCP connect failed|timed out waiting)");
    }

    [Fact]
    public async Task Direct_tcp_clears_the_reason_once_available_again()
    {
        await using var server = new FakeRconServer();
        var direct = new DirectTcpRconReachability(_ => new ScriptedRconSession(), TimeSpan.FromMilliseconds(750));

        (await direct.IsAvailableAsync(server.Endpoint)).Should().BeTrue();

        direct.LastUnavailableReason.Should().BeNull();
    }

    [Fact]
    public async Task Docker_exec_tool_reports_the_probe_exit_code()
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(127, string.Empty, "exec: \"rcon-cli\": not found", TimeSpan.Zero)));

        var strategy = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, RconCommandCatalog.Empty);

        (await strategy.IsAvailableAsync(new RconEndpoint("127.0.0.1", 25575))).Should().BeFalse();

        strategy.LastUnavailableReason.Should().NotBeNullOrWhiteSpace();
        strategy.LastUnavailableReason.Should().Contain("exited 127");
        strategy.LastUnavailableReason.Should().Contain("not found");
    }

    [Fact]
    public async Task Docker_exec_tool_reports_the_thrown_exception_type_without_its_message()
    {
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandResult>>(_ => throw new InvalidOperationException("no exec channel in this test"));

        var strategy = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, RconCommandCatalog.Empty);

        (await strategy.IsAvailableAsync(new RconEndpoint("127.0.0.1", 25575))).Should().BeFalse();

        strategy.LastUnavailableReason.Should().Contain(nameof(InvalidOperationException));
        strategy.LastUnavailableReason.Should().NotContain("no exec channel in this test");
    }

    [Fact]
    public void Unavailable_strategies_report_their_declared_reason()
    {
        UnavailableRconReachability.DockerExecNetwork.LastUnavailableReason
            .Should().Be(UnavailableRconReachability.DockerExecNetwork.Reason);

        UnavailableRconReachability.SshTunnel.LastUnavailableReason
            .Should().Be(UnavailableRconReachability.SshTunnel.Reason);

        UnavailableRconReachability.DockerExecNetwork.LastUnavailableReason.Should().Contain("sibling container");
        UnavailableRconReachability.SshTunnel.LastUnavailableReason.Should().Contain("SSH port-forward");
    }

    [Fact]
    public async Task Failure_reasons_never_contain_the_rcon_password()
    {
        const string password = "sup3r-secret-rcon-P@ssw0rd";

        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer(password))
        {
            endpoint = scratch.Endpoint; // nothing is listening here anymore.
        }

        // The password is "in scope" exactly the way a real composition root would put it there: captured in
        // the closure a session factory uses. IsAvailableAsync must never touch it - direct-tcp's probe takes
        // no credential at all, so the factory is never even invoked while probing.
        var direct = new DirectTcpRconReachability(
            _ => new ScriptedRconSession(respond: _ => new RconResponse($"authenticated with {password}", true)),
            TimeSpan.FromMilliseconds(200));

        // A second strategy whose exec channel throws an exception whose message happens to mention the
        // password, as a misbehaving or overly-verbose transport might. Proves docker-exec-tool's fallback
        // never echoes an exception's raw message into the reason it records.
        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommandResult>>(_ => throw new InvalidOperationException($"exec failed; password was '{password}'"));
        var dockerExecTool = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, RconCommandCatalog.Empty);

        var chain = new RconReachabilityChain([direct, dockerExecTool, UnavailableRconReachability.DockerExecNetwork]);

        var act = async () => await chain.AcquireAsync(endpoint);
        var exception = (await act.Should().ThrowAsync<RconUnreachableException>()).Which;

        exception.Message.Should().NotContain(password);
        direct.LastUnavailableReason.Should().NotContain(password);
        dockerExecTool.LastUnavailableReason.Should().NotContain(password);
    }

    [Fact]
    public async Task Captured_failure_text_is_truncated()
    {
        var longStderr = new string('x', 5_000);

        var target = Substitute.For<IExecutionTarget>();
        target.ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CommandResult(1, string.Empty, longStderr, TimeSpan.Zero)));

        var strategy = new DockerExecToolRconReachability(target, "palworld-server", ArgvTemplate, RconCommandCatalog.Empty);

        (await strategy.IsAvailableAsync(new RconEndpoint("127.0.0.1", 25575))).Should().BeFalse();

        var reason = strategy.LastUnavailableReason;
        reason.Should().NotBeNullOrWhiteSpace();
        reason!.Length.Should().BeLessThan(longStderr.Length);
        reason.Length.Should().BeLessThan(300);
        reason.Should().NotContain(longStderr);
        reason.Should().EndWith("...");
    }
}
