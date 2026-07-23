using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Observability;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// <see cref="IMetricsSource"/> implementation backed by the Docker Engine stats endpoint.
/// </summary>
/// <remarks>
/// In this milestone there is no Server → container mapping component in scope, so <c>serverId</c> is
/// treated as the Docker container id or name directly. A later milestone (in <c>Servyx.Application</c>)
/// is expected to resolve a domain <c>ServerId</c> to a container reference before reaching this class.
/// Player counts are deliberately not populated here — that requires a control-channel session (RCON/REST),
/// not the Docker API, and lands in M2.
/// </remarks>
public sealed class DockerMetricsSource : IMetricsSource
{
    private readonly IDockerClient _client;

    /// <summary>Creates a metrics source operating against the given Docker client.</summary>
    public DockerMetricsSource(IDockerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Takes a single, one-shot resource-usage sample for the given server (container). A single Docker
    /// stats reading cannot honestly yield a CPU percentage — the calculation requires a delta between
    /// two readings — so this takes two readings separated by <paramref name="sampleInterval"/> rather
    /// than fabricating a number from one.
    /// </summary>
    public async Task<ResourceSample> SampleAsync(string serverId, TimeSpan? sampleInterval = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var interval = sampleInterval ?? TimeSpan.FromMilliseconds(500);

        var first = await GetStatsOnceAsync(serverId, ct).ConfigureAwait(false);
        await Task.Delay(interval, ct).ConfigureAwait(false);
        var second = await GetStatsOnceAsync(serverId, ct).ConfigureAwait(false);

        var cpuPercent = DockerCpuPercentCalculator.Compute(ExtractCpuSnapshot(second), ExtractCpuSnapshot(first)) ?? 0.0;
        return MapSample(second, cpuPercent);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ResourceSample> StreamAsync(string serverId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var channel = Channel.CreateUnbounded<ResourceSample>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        CpuUsageSnapshot? previous = null;
        var progress = new SynchronousProgress<ContainerStatsResponse>(stats =>
        {
            var currentCpu = ExtractCpuSnapshot(stats);
            var cpuPercent = previous is null ? 0.0 : DockerCpuPercentCalculator.Compute(currentCpu, previous.Value) ?? 0.0;
            previous = currentCpu;
            channel.Writer.TryWrite(MapSample(stats, cpuPercent));
        });

        _ = RunStatsLoopAsync(serverId, progress, channel, ct);

        await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    private async Task RunStatsLoopAsync(
        string serverId,
        IProgress<ContainerStatsResponse> progress,
        Channel<ResourceSample> channel,
        CancellationToken ct)
    {
        try
        {
            await _client.Containers.GetContainerStatsAsync(serverId, new ContainerStatsParameters { Stream = true }, progress, ct)
                .ConfigureAwait(false);
            channel.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
        }
    }

    private async Task<ContainerStatsResponse> GetStatsOnceAsync(string serverId, CancellationToken ct)
    {
        ContainerStatsResponse? result = null;
        var progress = new SynchronousProgress<ContainerStatsResponse>(stats => result = stats);

        await _client.Containers.GetContainerStatsAsync(serverId, new ContainerStatsParameters { Stream = false }, progress, ct)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException($"Docker did not return a stats reading for container '{serverId}'.");
    }

    /// <summary>
    /// Extracts the CPU-accounting fields Docker reports for the <em>current</em> window
    /// (<c>cpu_stats</c>), decoupled from the Docker.DotNet response shape.
    /// </summary>
    internal static CpuUsageSnapshot ExtractCpuSnapshot(ContainerStatsResponse stats) => new(
        stats.CPUStats?.CPUUsage?.TotalUsage ?? 0,
        stats.CPUStats?.SystemUsage ?? 0,
        stats.CPUStats?.OnlineCPUs ?? 0);

    /// <summary>Maps a raw Docker stats reading plus a precomputed CPU percentage into a domain <see cref="ResourceSample"/>.</summary>
    internal static ResourceSample MapSample(ContainerStatsResponse stats, double cpuPercent)
    {
        long rxBytes = 0;
        long txBytes = 0;
        if (stats.Networks is not null)
        {
            foreach (var network in stats.Networks.Values)
            {
                rxBytes += (long)network.RxBytes;
                txBytes += (long)network.TxBytes;
            }
        }

        var timestamp = stats.Read == default
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(stats.Read, DateTimeKind.Utc));

        return new ResourceSample(timestamp, cpuPercent, (long)(stats.MemoryStats?.Usage ?? 0), rxBytes, txBytes);
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that invokes its callback synchronously on the reporting thread,
    /// unlike <see cref="Progress{T}"/>, which marshals to a captured <see cref="SynchronizationContext"/>
    /// asynchronously. Synchronous invocation is required here: <see cref="GetStatsOnceAsync"/> reads its
    /// result immediately after the reporting call completes, which would otherwise race the
    /// context-switch <see cref="Progress{T}"/> performs internally.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
