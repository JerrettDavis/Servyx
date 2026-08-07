using System.Reflection;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="SshDockerLifecycleSession"/>: the decorator that adds an
/// <see cref="IContainerLifecycle"/> channel to the ssh+docker session while forwarding every other
/// <see cref="IExecutionTarget"/> member unchanged. Follows the house pattern: the SSH host is a substituted
/// <see cref="IExecutionTarget"/> (see <see cref="SshHostDouble"/>), so no live SSH server or docker daemon
/// is involved anywhere.
/// </summary>
public class SshDockerLifecycleSessionTests
{
    [Fact]
    public async Task Every_lifecycle_verb_maps_to_the_expected_docker_command()
    {
        await AssertMapsTo(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, "palworld"),
            DockerCli.Start("palworld"));

        await AssertMapsTo(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, "palworld", TimeSpan.FromSeconds(30)),
            DockerCli.Stop("palworld", 30));

        await AssertMapsTo(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Restart, "palworld"),
            DockerCli.Restart("palworld"));

        await AssertMapsTo(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, "palworld", Signal: "SIGTERM"),
            DockerCli.Kill("palworld", "SIGTERM"));
    }

    private static async Task AssertMapsTo(ContainerLifecycleRequest request, CommandSpec expected)
    {
        var host = new SshHostDouble();
        var session = new SshDockerLifecycleSession(host.Session);

        await session.InvokeAsync(request);

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Executable.Should().Be(expected.Executable);
        recorded.Arguments.Should().Equal(expected.Arguments);
        recorded.Intent.Should().Be(CommandIntent.Mutating);
    }

    [Fact]
    public async Task Stop_passes_the_grace_period_as_the_docker_timeout()
    {
        var host = new SshHostDouble();
        var session = new SshDockerLifecycleSession(host.Session);

        await session.InvokeAsync(new ContainerLifecycleRequest(
            ContainerLifecycleVerb.Stop, "palworld", GracePeriod: TimeSpan.FromSeconds(45)));

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Arguments.Should().Equal("stop", "--time", "45", "palworld");
    }

    [Fact]
    public async Task Stop_defaults_the_grace_period_when_none_is_given()
    {
        var host = new SshHostDouble();
        var session = new SshDockerLifecycleSession(host.Session);

        await session.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, "palworld"));

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Arguments.Should().Equal("stop", "--time", "10", "palworld");
    }

    [Fact]
    public async Task Lifecycle_succeeds_when_writes_are_enabled()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, "palworld", string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var inner = new WriteGuardedExecutionTarget(host.Session, WriteMode.Enabled, "palworld");
        var session = new SshDockerLifecycleSession(inner);

        var result = await session.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, "palworld"));

        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);

        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Executable.Should().Be("docker");
        recorded.Arguments.Should().Equal("start", "palworld");
    }

    [Fact]
    public async Task Non_zero_exit_is_reported_as_failure_with_truncated_stderr()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(1, string.Empty, new string('e', 5000), TimeSpan.Zero),
        };
        var inner = new WriteGuardedExecutionTarget(host.Session, WriteMode.Enabled, "palworld");
        var session = new SshDockerLifecycleSession(inner);

        var result = await session.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, "palworld"));

        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.Detail.Should().NotBeNull();
        result.Detail.Length.Should().BeLessThan(300);
    }

    [Fact]
    public async Task Lifecycle_is_refused_by_the_outer_write_guard()
    {
        // The outer WriteGuardedExecutionTarget gates IContainerLifecycle.InvokeAsync itself (via
        // ContainerLifecycleRequest.AsGuardedSpec) before ever reaching the inner session — the same
        // structural guard the exec/file paths get. Writes are disabled, so this must refuse before the
        // inner session records anything at all.
        var host = new SshHostDouble();
        var lifecycleSession = new SshDockerLifecycleSession(host.Session);
        var guarded = new WriteGuardedExecutionTarget(lifecycleSession, WriteMode.ReadOnly, "palworld");

        var act = async () => await guarded.InvokeAsync(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, "palworld"));

        await act.Should().ThrowAsync<WritesDisabledException>();
        host.Commands.Should().BeEmpty("the outer guard must refuse before the inner session is ever touched");
    }

    [Fact]
    public async Task Lifecycle_is_refused_by_the_inner_guard_even_if_the_outer_is_bypassed()
    {
        // Prove double gating: construct the decorator directly over a write-guarded inner session with
        // writes disabled, bypassing any outer WriteGuardedExecutionTarget entirely. The DockerCli spec the
        // decorator builds is Mutating by declaration (see DockerCliIntentArchitectureTests), so the INNER
        // guard refuses it independently of whether an outer guard exists at all.
        var host = new SshHostDouble();
        var innerGuard = new WriteGuardedExecutionTarget(host.Session, WriteMode.ReadOnly, "palworld");
        var session = new SshDockerLifecycleSession(innerGuard);

        var act = async () => await session.InvokeAsync(
            new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, "palworld"));

        await act.Should().ThrowAsync<WritesDisabledException>();
        host.Commands.Should().BeEmpty("the inner guard must refuse before the underlying session is touched");
    }

    [Fact]
    public void The_decorator_forwards_every_execution_target_member_unchanged()
    {
        // Reflection over IExecutionTarget's members, so a future member added to the interface and not
        // forwarded here is caught even though nobody remembered to add a bespoke test for it. Type.GetMethods()
        // on an interface does not surface members inherited from interfaces it extends, so IAsyncDisposable's
        // DisposeAsync has to be pulled in explicitly via GetInterfaces() or this scan would silently miss it.
        var members = typeof(IExecutionTarget).GetInterfaces()
            .Append(typeof(IExecutionTarget))
            .SelectMany(i => i.GetMethods())
            .ToList();
        members.Should().NotBeEmpty("the scan below must find real members for this assertion to mean anything");
        members.Should().Contain(m => m.Name == nameof(IAsyncDisposable.DisposeAsync),
            "the scan must include IAsyncDisposable's member, not just IExecutionTarget's own");

        var decoratorMethods = typeof(SshDockerLifecycleSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .ToList();

        foreach (var member in members)
        {
            var match = decoratorMethods.SingleOrDefault(m =>
                m.Name == member.Name &&
                m.GetParameters().Select(p => p.ParameterType).SequenceEqual(member.GetParameters().Select(p => p.ParameterType)));

            match.Should().NotBeNull(
                $"SshDockerLifecycleSession must implement IExecutionTarget.{member.Name} by forwarding it " +
                "to the inner session");
        }
    }

    [Fact]
    public async Task Every_execution_target_call_reaches_the_inner_session_verbatim()
    {
        // Belt-and-suspenders companion to the reflection test above: actually invoke each forwarding member
        // and prove the call landed on the inner session with the exact argument, not a rewritten one.
        var host = new SshHostDouble();
        var session = new SshDockerLifecycleSession(host.Session);

        var spec = new CommandSpec("echo", ["hi"], Intent: CommandIntent.ReadOnly);
        await session.ExecuteAsync(spec);
        host.Commands.Should().ContainSingle().Which.Should().Be(spec);

        var resolver = new SandboxedPathResolver(Path.GetTempPath());
        var path = resolver.Resolve("some/file");
        var directoryPath = resolver.Resolve("some");

        await session.ExistsAsync(path);
        await host.Session.Received(1).ExistsAsync(path, Arg.Any<CancellationToken>());

        await session.StatAsync(path);
        await host.Session.Received(1).StatAsync(path, Arg.Any<CancellationToken>());

        host.Directories.Add("/" + directoryPath.Value);
        await session.ListDirectoryAsync(directoryPath);
        await host.Session.Received(1).ListDirectoryAsync(directoryPath, Arg.Any<CancellationToken>());

        host.PutFile("/" + path.Value, [1, 2, 3]);
        await session.OpenReadAsync(path);
        await host.Session.Received(1).OpenReadAsync(path, Arg.Any<CancellationToken>());

        using var content = new MemoryStream([4, 5, 6]);
        var options = new FileWriteOptions(null);
        await session.WriteFileAsync(path, content, options);
        await host.Session.Received(1).WriteFileAsync(path, content, options, Arg.Any<CancellationToken>());

        await session.DeleteAsync(path);
        await host.Session.Received(1).DeleteAsync(path, Arg.Any<CancellationToken>());

        await session.DisposeAsync();
        await host.Session.Received(1).DisposeAsync();

        host.Session
            .ExecuteStreamingAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>())
            .Returns(EmptyChunks());

        _ = session.ExecuteStreamingAsync(spec);
        host.Session.Received(1).ExecuteStreamingAsync(spec, Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<OutputChunk> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }
}
