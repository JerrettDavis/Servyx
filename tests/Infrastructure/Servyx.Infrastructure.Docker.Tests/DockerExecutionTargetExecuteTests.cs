using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Tests;

/// <summary>
/// <see cref="DockerExecutionTarget.ExecuteAsync"/> — the general-purpose <c>docker exec</c> command
/// channel — and its still-unimplemented streaming sibling.
/// </summary>
public class DockerExecutionTargetExecuteTests
{
    private const string ContainerRef = "palworld-server";

    /// <summary>Builds a single Docker multiplexed-stream frame: an 8-byte header plus its payload.</summary>
    /// <param name="streamType">1 for stdout, 2 for stderr.</param>
    private static byte[] BuildFrame(byte streamType, string content)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        var header = new byte[8];
        header[0] = streamType;
        var length = (uint)payload.Length;
        header[4] = (byte)(length >> 24);
        header[5] = (byte)(length >> 16);
        header[6] = (byte)(length >> 8);
        header[7] = (byte)length;
        return [.. header, .. payload];
    }

    private sealed class ExecRecorder
    {
        public ExecRecorder(int exitCode = 0, string stdout = "", string stderr = "")
        {
            Client = Substitute.For<IDockerClient>();
            Exec = Substitute.For<IExecOperations>();
            Client.Exec.Returns(Exec);

            Exec.ExecCreateContainerAsync(Arg.Any<string>(), Arg.Any<ContainerExecCreateParameters>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    CreateParameters = ci.ArgAt<ContainerExecCreateParameters>(1);
                    return Task.FromResult(new ContainerExecCreateResponse { ID = "exec-1" });
                });

            var frames = BuildFrame(1, stdout).Concat(BuildFrame(2, stderr)).ToArray();
            Exec.StartAndAttachContainerExecAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new MultiplexedStream(new MemoryStream(frames), multiplexed: true)));

            Exec.InspectContainerExecAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(new ContainerExecInspectResponse { Running = false, ExitCode = exitCode }));

            Target = new DockerExecutionTarget(Client, ContainerRef, "/palworld");
        }

        public IDockerClient Client { get; }

        public IExecOperations Exec { get; }

        public DockerExecutionTarget Target { get; }

        public ContainerExecCreateParameters? CreateParameters { get; private set; }
    }

    [Fact]
    public async Task Execute_runs_the_command_and_returns_the_exit_code()
    {
        var daemon = new ExecRecorder(exitCode: 7);

        var result = await daemon.Target.ExecuteAsync(new CommandSpec("steamcmd", ["+quit"]));

        result.ExitCode.Should().Be(7);
        result.Succeeded.Should().BeFalse();
        daemon.CreateParameters.Should().NotBeNull();
        daemon.CreateParameters!.Cmd.Should().Equal("steamcmd", "+quit");
    }

    [Fact]
    public async Task Execute_passes_argv_without_shell_interpolation()
    {
        // Spaces, quotes, and a command-substitution-shaped string must all survive as one argv element
        // each: there is no shell on either side to interpret them.
        var daemon = new ExecRecorder();
        var dangerous = new[] { "arg with spaces", "double\"quote", "single'quote", "$(rm -rf /)", "`echo pwned`" };

        await daemon.Target.ExecuteAsync(new CommandSpec("echo", dangerous));

        daemon.CreateParameters!.Cmd.Should().Equal(["echo", .. dangerous]);
    }

    [Fact]
    public async Task Execute_captures_stdout_and_stderr()
    {
        var daemon = new ExecRecorder(stdout: "hello from stdout", stderr: "warning from stderr");

        var result = await daemon.Target.ExecuteAsync(new CommandSpec("echo", ["hi"]));

        result.StandardOutput.Should().Be("hello from stdout");
        result.StandardError.Should().Be("warning from stderr");
    }

    [Fact]
    public void Streaming_exec_still_reports_not_supported_with_an_accurate_message()
    {
        var daemon = new ExecRecorder();

        var act = () => daemon.Target.ExecuteStreamingAsync(new CommandSpec("tail", ["-f", "log.txt"]));

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*not implemented*")
            .Which.Message.Should().NotContain("M2");
    }
}
