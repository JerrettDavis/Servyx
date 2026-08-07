using System.Runtime.CompilerServices;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="IMetricsSource"/> implementation for the ssh+docker transport: samples a container's
/// resource usage via <c>docker stats --no-stream</c> run over an existing SSH exec channel, rather than
/// the Docker Engine API's streaming stats endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Takes an already-connected <see cref="IExecutionTarget"/>, for the same reason as
/// <see cref="SshDockerServerDiscovery"/> and <see cref="SshDockerLogStream"/>: it mirrors
/// <c>DockerMetricsSource</c> holding a persistent <c>IDockerClient</c>, and a caller managing a remote
/// server already holds one connected session to reuse across every read surface.
/// </para>
/// <para>
/// <see cref="DockerCli.Stats"/> is a single non-streaming snapshot (<c>--no-stream</c>) — the Engine
/// API's push-based streaming stats endpoint has no CLI-over-exec equivalent this assembly exposes. So
/// <see cref="StreamAsync"/> polls: it executes a fresh <c>docker stats --no-stream</c> read-only command
/// on a fixed interval, parses each snapshot, and yields a <see cref="ResourceSample"/> per poll, for as
/// long as the caller keeps enumerating. This is a deliberate behavioral difference from
/// <c>DockerMetricsSource.StreamAsync</c> (which yields a sample per push from the Engine API, often
/// sub-second) — the interval here is configurable but not tied to daemon-side timing.
/// </para>
/// <para>
/// Unlike <c>docker stats</c>'s <c>CPUPerc</c>/<c>MemPerc</c> fields (already a ready-to-use percentage
/// computed daemon-side across the whole sampling window docker itself chose), no delta calculation
/// against a previous reading is needed here, unlike <c>DockerMetricsSource.SampleAsync</c>'s two-reading
/// CPU delta against the Engine API's raw counters.
/// </para>
/// <para>
/// <c>docker stats</c>'s JSON output has no machine-parsable network I/O field (only the human-formatted
/// <c>NetIO</c> string, e.g. <c>"14.1MB / 49.5MB"</c>, which <see cref="DockerInspectJson.ParseStats"/>
/// deliberately does not decode), so <see cref="ResourceSample.NetworkRxBytes"/> and
/// <see cref="ResourceSample.NetworkTxBytes"/> are always reported as zero here.
/// </para>
/// </remarks>
public sealed class SshDockerMetricsSource : IMetricsSource
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private readonly IExecutionTarget _target;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Creates a metrics source operating against an already-connected SSH session, polling
    /// <paramref name="pollInterval"/> apart (defaults to 2 seconds).
    /// </summary>
    public SshDockerMetricsSource(IExecutionTarget target, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (pollInterval is { } interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        }

        _target = target;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <c>docker stats</c> exited non-zero. Never swallowed: a failed poll surfaces loudly rather than
    /// silently stopping or skipping a sample, so a broken SSH/docker path on a remote host cannot
    /// masquerade as "the server has no metrics yet".
    /// </exception>
    public async IAsyncEnumerable<ResourceSample> StreamAsync(
        string serverId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _target.ExecuteAsync(DockerCli.Stats(serverId), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"'docker stats' failed for container '{serverId}' (exit {result.ExitCode}): {result.StandardError.Trim()}");
            }

            var stats = DockerInspectJson.ParseStats(result.StandardOutput);
            yield return MapSample(stats);

            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    private static ResourceSample MapSample(DockerContainerStats stats) => new(
        Timestamp: DateTimeOffset.UtcNow,
        CpuPercent: stats.CpuPercent ?? 0.0,
        MemoryBytes: stats.MemoryUsageBytes ?? 0,
        NetworkRxBytes: 0,
        NetworkTxBytes: 0);
}
