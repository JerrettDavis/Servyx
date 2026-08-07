using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Tests;

/// <summary>
/// <see cref="DockerExecutionTarget"/>'s <see cref="IContainerLifecycle"/> implementation — start, stop,
/// restart, and kill against the Docker Engine container API (never <c>docker exec</c>, since you cannot
/// exec into a container that is not running).
/// </summary>
public class DockerExecutionTargetLifecycleTests
{
    private const string ContainerRef = "palworld-server";

    private sealed class LifecycleRecorder
    {
        public LifecycleRecorder()
        {
            Client = Substitute.For<IDockerClient>();
            Containers = Substitute.For<IContainerOperations>();
            Client.Containers.Returns(Containers);

            Containers.StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));

            Containers.StopContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    StopParameters = ci.ArgAt<ContainerStopParameters>(1);
                    return Task.FromResult(true);
                });

            Containers.RestartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerRestartParameters>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    RestartParameters = ci.ArgAt<ContainerRestartParameters>(1);
                    return Task.CompletedTask;
                });

            Containers.KillContainerAsync(Arg.Any<string>(), Arg.Any<ContainerKillParameters>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    KillParameters = ci.ArgAt<ContainerKillParameters>(1);
                    return Task.CompletedTask;
                });

            Containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ContainerInspectResponse
                {
                    State = new ContainerState { Status = "running", ExitCode = 0 },
                }));

            Target = new DockerExecutionTarget(Client, ContainerRef, "/palworld");
        }

        public IDockerClient Client { get; }

        public IContainerOperations Containers { get; }

        public DockerExecutionTarget Target { get; }

        public ContainerStopParameters? StopParameters { get; private set; }

        public ContainerRestartParameters? RestartParameters { get; private set; }

        public ContainerKillParameters? KillParameters { get; private set; }
    }

    [Theory]
    [InlineData(ContainerLifecycleVerb.Start)]
    [InlineData(ContainerLifecycleVerb.Stop)]
    [InlineData(ContainerLifecycleVerb.Restart)]
    [InlineData(ContainerLifecycleVerb.Kill)]
    public async Task Every_lifecycle_verb_calls_the_expected_docker_api(ContainerLifecycleVerb verb)
    {
        var daemon = new LifecycleRecorder();

        var result = await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(verb, ContainerRef));

        result.Success.Should().BeTrue();
        result.State.Should().Be("running");

        switch (verb)
        {
            case ContainerLifecycleVerb.Start:
                await daemon.Containers.Received(1)
                    .StartContainerAsync(ContainerRef, Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
                break;
            case ContainerLifecycleVerb.Stop:
                await daemon.Containers.Received(1)
                    .StopContainerAsync(ContainerRef, Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>());
                break;
            case ContainerLifecycleVerb.Restart:
                await daemon.Containers.Received(1)
                    .RestartContainerAsync(ContainerRef, Arg.Any<ContainerRestartParameters>(), Arg.Any<CancellationToken>());
                break;
            case ContainerLifecycleVerb.Kill:
                await daemon.Containers.Received(1)
                    .KillContainerAsync(ContainerRef, Arg.Any<ContainerKillParameters>(), Arg.Any<CancellationToken>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        }
    }

    [Fact]
    public async Task Stop_passes_the_grace_period_as_wait_before_kill_seconds()
    {
        var daemon = new LifecycleRecorder();
        var request = new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, ContainerRef, GracePeriod: TimeSpan.FromSeconds(30));

        await daemon.Target.InvokeAsync(request);

        daemon.StopParameters.Should().NotBeNull();
        daemon.StopParameters!.WaitBeforeKillSeconds.Should().Be(30u);
    }

    [Fact]
    public async Task Restart_passes_the_grace_period_as_wait_before_kill_seconds()
    {
        var daemon = new LifecycleRecorder();
        var request = new ContainerLifecycleRequest(ContainerLifecycleVerb.Restart, ContainerRef, GracePeriod: TimeSpan.FromSeconds(15));

        await daemon.Target.InvokeAsync(request);

        daemon.RestartParameters.Should().NotBeNull();
        daemon.RestartParameters!.WaitBeforeKillSeconds.Should().Be(15u);
    }

    [Fact]
    public async Task A_stop_with_no_grace_period_leaves_wait_before_kill_seconds_unset()
    {
        var daemon = new LifecycleRecorder();

        await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, ContainerRef));

        daemon.StopParameters.Should().NotBeNull();
        daemon.StopParameters!.WaitBeforeKillSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Kill_passes_the_signal()
    {
        var daemon = new LifecycleRecorder();
        var request = new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, ContainerRef, Signal: "SIGTERM");

        await daemon.Target.InvokeAsync(request);

        daemon.KillParameters.Should().NotBeNull();
        daemon.KillParameters!.Signal.Should().Be("SIGTERM");
    }

    [Fact]
    public async Task Stop_and_kill_report_the_exit_code_from_the_post_transition_inspection()
    {
        var daemon = new LifecycleRecorder();
        daemon.Containers.InspectContainerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ContainerInspectResponse
            {
                State = new ContainerState { Status = "exited", ExitCode = 137 },
            }));

        var result = await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Kill, ContainerRef));

        result.State.Should().Be("exited");
        result.ExitCode.Should().Be(137);
    }

    [Fact]
    public async Task Start_does_not_report_an_exit_code()
    {
        var daemon = new LifecycleRecorder();

        var result = await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, ContainerRef));

        result.ExitCode.Should().BeNull();
    }

    [Fact]
    public async Task Lifecycle_is_refused_when_wrapped_in_a_write_guard_with_writes_disabled()
    {
        var daemon = new LifecycleRecorder();
        var guarded = new WriteGuardedExecutionTarget(daemon.Target, WriteMode.ReadOnly);

        var act = async () => await guarded.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, ContainerRef));

        await act.Should().ThrowAsync<WritesDisabledException>();
        await daemon.Containers.DidNotReceive()
            .StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lifecycle_reaches_the_container_api_when_the_write_guard_has_writes_enabled()
    {
        var daemon = new LifecycleRecorder();
        var guarded = new WriteGuardedExecutionTarget(daemon.Target, WriteMode.Enabled);

        var result = await guarded.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, ContainerRef));

        result.Success.Should().BeTrue();
        await daemon.Containers.Received(1)
            .StartContainerAsync(ContainerRef, Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_container_not_found_error_is_surfaced_not_swallowed()
    {
        var daemon = new LifecycleRecorder();
        daemon.Containers.StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container: palworld-server")));

        var result = await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Start, ContainerRef));

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("palworld-server").And.Contain("no such container");
    }

    [Fact]
    public async Task A_daemon_api_failure_is_surfaced_as_an_unsuccessful_result_rather_than_thrown()
    {
        var daemon = new LifecycleRecorder();
        daemon.Containers.StopContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStopParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new DockerApiException(HttpStatusCode.InternalServerError, "daemon is unwell")));

        var act = async () => await daemon.Target.InvokeAsync(new ContainerLifecycleRequest(ContainerLifecycleVerb.Stop, ContainerRef));

        var result = await act.Should().NotThrowAsync();
        result.Which.Success.Should().BeFalse();
        result.Which.Detail.Should().Contain("daemon is unwell");
    }
}
