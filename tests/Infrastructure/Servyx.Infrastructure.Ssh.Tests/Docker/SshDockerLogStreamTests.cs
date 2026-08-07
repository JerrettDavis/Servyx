using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="SshDockerLogStream"/>. Follows the house pattern: the SSH host is a
/// substituted <see cref="ITransport"/>/<see cref="IExecutionTarget"/> pair (see <see cref="SshHostDouble"/>),
/// so no live SSH server or docker daemon is involved anywhere.
/// </summary>
public class SshDockerLogStreamTests
{
    private const string ContainerId = "palworld-server";

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static async Task<List<ConsoleLine>> CollectAsync(IAsyncEnumerable<ConsoleLine> source)
    {
        var result = new List<ConsoleLine>();
        await foreach (var line in source)
        {
            result.Add(line);
        }

        return result;
    }

    [Fact]
    public async Task Log_stream_returns_the_tailed_lines()
    {
        var logsText = ReadFixture("palworld-logs.txt");
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, logsText, string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var logStream = new SshDockerLogStream(host.Session);

        var lines = await CollectAsync(logStream.FollowAsync(ContainerId, new ConsoleTailOptions(200)));

        var expectedLineCount = logsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        lines.Should().HaveCount(expectedLineCount);
        lines[0].Text.Should().Be("[2026-08-05 13:15:33] [LOG] REST accessed endpoint /v1/api/players OK (x6)");
        lines[0].Timestamp.Should().Be(DateTimeOffset.Parse("2026-08-05T18:16:02.921833118Z"));
        lines[0].Stream.Should().Be(OutputStream.StdOut);
        lines[0].Offset.Should().Be(0);
        lines[1].Offset.Should().Be(1);
    }

    [Fact]
    public async Task Log_stream_strips_the_timestamp_prefix_but_preserves_the_timestamp_value()
    {
        var logsText = ReadFixture("palworld-logs.txt");
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, logsText, string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var logStream = new SshDockerLogStream(host.Session);

        var lines = await CollectAsync(logStream.FollowAsync(ContainerId, new ConsoleTailOptions(200)));

        lines.Should().OnlyContain(l => !l.Text.StartsWith("2026-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Log_stream_issues_only_a_read_only_docker_logs_command()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, ReadFixture("palworld-logs.txt"), string.Empty, TimeSpan.Zero),
        };
        var logStream = new SshDockerLogStream(host.Session);

        await CollectAsync(logStream.FollowAsync(ContainerId, new ConsoleTailOptions(50)));

        host.Commands.Should().NotBeEmpty();
        host.Commands.Should().OnlyContain(c => c.Intent == CommandIntent.ReadOnly);
        var recorded = host.Commands.Should().ContainSingle().Subject;
        recorded.Executable.Should().Be("docker");
        recorded.Arguments.Should().Contain("logs");
        recorded.Arguments.Should().Contain("--timestamps");
    }

    [Fact]
    public void Write_async_is_always_refused()
    {
        var host = new SshHostDouble();
        var logStream = new SshDockerLogStream(host.Session);

        logStream.SupportsInput.Should().BeFalse();

        var act = async () => await logStream.WriteAsync(ContainerId, "say hello");

        act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Log_stream_throws_when_docker_logs_fails()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(1, string.Empty, "Error: No such container: palworld-server", TimeSpan.Zero),
        };
        var logStream = new SshDockerLogStream(host.Session);

        var act = async () => await CollectAsync(logStream.FollowAsync(ContainerId, new ConsoleTailOptions(50)));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*docker logs*");
    }
}
