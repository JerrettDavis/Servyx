using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Infrastructure.Ssh.Tests.Provisioning;

namespace Servyx.Infrastructure.Ssh.Tests.Docker;

/// <summary>
/// Unit tests for <see cref="SshDockerMetricsSource"/>. Follows the house pattern: the SSH host is a
/// substituted <see cref="ITransport"/>/<see cref="IExecutionTarget"/> pair (see <see cref="SshHostDouble"/>),
/// so no live SSH server or docker daemon is involved anywhere.
/// </summary>
public class SshDockerMetricsSourceTests
{
    private const string ContainerId = "palworld-server";

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    private static async Task<ResourceSample> FirstAsync(IAsyncEnumerable<ResourceSample> source)
    {
        await foreach (var sample in source)
        {
            return sample;
        }

        throw new InvalidOperationException("Sequence contained no elements.");
    }

    private static async Task<List<ResourceSample>> TakeAsync(IAsyncEnumerable<ResourceSample> source, int count)
    {
        var results = new List<ResourceSample>();
        await foreach (var sample in source)
        {
            results.Add(sample);
            if (results.Count >= count)
            {
                break;
            }
        }

        return results;
    }

    [Fact]
    public async Task Metrics_reports_cpu_and_memory()
    {
        var statsJson = ReadFixture("palworld-stats.json");
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, statsJson, string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var metrics = new SshDockerMetricsSource(host.Session, pollInterval: TimeSpan.FromMilliseconds(10));

        var sample = await FirstAsync(metrics.StreamAsync(ContainerId));

        sample.CpuPercent.Should().BeApproximately(138.51, 0.001);
        // "2.141GiB" as reported in the fixture's MemUsage field.
        sample.MemoryBytes.Should().Be((long)(2.141 * 1024 * 1024 * 1024));
    }

    [Fact]
    public async Task Metrics_issues_only_read_only_commands()
    {
        var statsJson = ReadFixture("palworld-stats.json");
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, statsJson, string.Empty, TimeSpan.FromMilliseconds(5)),
        };
        var metrics = new SshDockerMetricsSource(host.Session, pollInterval: TimeSpan.FromMilliseconds(10));

        await FirstAsync(metrics.StreamAsync(ContainerId));

        host.Commands.Should().NotBeEmpty();
        host.Commands.Should().OnlyContain(c => c.Intent == CommandIntent.ReadOnly);
        var recorded = host.Commands[0];
        recorded.Executable.Should().Be("docker");
        recorded.Arguments.Should().Contain("stats");
        recorded.Arguments.Should().Contain("--no-stream");
    }

    [Fact]
    public async Task Metrics_polls_repeatedly_while_the_caller_keeps_enumerating()
    {
        var statsJson = ReadFixture("palworld-stats.json");
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(0, statsJson, string.Empty, TimeSpan.Zero),
        };
        var metrics = new SshDockerMetricsSource(host.Session, pollInterval: TimeSpan.FromMilliseconds(5));

        var samples = await TakeAsync(metrics.StreamAsync(ContainerId), 3);

        samples.Should().HaveCount(3);
        host.Commands.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Metrics_throws_when_docker_stats_fails()
    {
        var host = new SshHostDouble
        {
            ExecHandler = _ => new CommandResult(1, string.Empty, "Error: No such container: palworld-server", TimeSpan.Zero),
        };
        var metrics = new SshDockerMetricsSource(host.Session, pollInterval: TimeSpan.FromMilliseconds(10));

        var act = async () => await FirstAsync(metrics.StreamAsync(ContainerId));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*docker stats*");
    }
}
