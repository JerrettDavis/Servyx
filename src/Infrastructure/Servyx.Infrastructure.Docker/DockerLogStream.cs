using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// <see cref="ILogStream"/> implementation backed by the Docker Engine logs endpoint.
/// </summary>
/// <remarks>
/// In this milestone there is no Server → container mapping component in scope, so <c>serverId</c> is
/// treated as the Docker container id or name directly (see <see cref="DockerMetricsSource"/> for the
/// same convention). Offsets are allocated from a per-container, monotonically increasing counter held
/// for the lifetime of this instance, so a second <see cref="FollowAsync"/> call after a dropped
/// connection continues the sequence rather than restarting at zero — this is what lets a client resume
/// without re-reading from the start, per <see cref="ConsoleLine.Offset"/>'s contract.
/// </remarks>
public sealed class DockerLogStream : ILogStream
{
    private readonly IDockerClient _client;
    private readonly ILogger<DockerLogStream> _logger;
    private readonly ConcurrentDictionary<string, long> _nextOffsets = new(StringComparer.Ordinal);

    /// <summary>Creates a log stream operating against the given Docker client.</summary>
    public DockerLogStream(IDockerClient client, ILogger<DockerLogStream>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _logger = logger ?? NullLogger<DockerLogStream>.Instance;
    }

    /// <inheritdoc />
    public bool SupportsInput => false;

    /// <inheritdoc />
    public async IAsyncEnumerable<ConsoleLine> FollowAsync(
        string serverId,
        ConsoleTailOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(options);

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = true,
            Tail = options.MaxBacklogLines > 0 ? options.MaxBacklogLines.ToString(CultureInfo.InvariantCulture) : "all",
        };

        var isTty = await IsTtyAsync(serverId, ct).ConfigureAwait(false);
        var rawStream = await OpenRawLogStreamAsync(serverId, parameters, ct).ConfigureAwait(false);

        var demuxer = new DockerLogDemuxer(demultiplex: !isTty);
        var buffer = new byte[8192];

        await using (rawStream.ConfigureAwait(false))
        {
            while (true)
            {
                var (read, stopReason) = await TryReadAsync(rawStream, buffer, ct).ConfigureAwait(false);
                if (stopReason is not null)
                {
                    _logger.LogWarning(
                        stopReason,
                        "Docker log stream for container '{ContainerId}' ended: {ExceptionType}: {Message}",
                        serverId,
                        stopReason.GetType().Name,
                        stopReason.Message);
                    yield break;
                }

                if (read == 0)
                {
                    _logger.LogDebug("Docker log stream for container '{ContainerId}' reached end of stream.", serverId);
                    yield break;
                }

                foreach (var line in demuxer.Feed(buffer.AsSpan(0, read)))
                {
                    yield return new ConsoleLine(TakeOffset(serverId), line.Text, line.Timestamp, line.Stream);
                }
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Offsets here are assigned by position within the currently-retained log history (line 0 is
    /// always the oldest retained line), independently of <see cref="FollowAsync"/>'s per-instance
    /// counter — this class talks to the Docker log driver directly rather than the append-only,
    /// offset-indexed file store the production design in <c>docs/architecture.md</c> calls for, so a
    /// concurrently running <see cref="FollowAsync"/> call's offsets for the same server are not
    /// guaranteed to align with this method's. Repeated calls with the same arguments against an
    /// unchanged log return the same result.
    /// </remarks>
    public async Task<IReadOnlyList<ConsoleLine>> ReadAsync(string serverId, long fromOffset, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (count <= 0)
        {
            return [];
        }

        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Tail = "all",
        };

        var isTty = await IsTtyAsync(serverId, ct).ConfigureAwait(false);
        var rawStream = await OpenRawLogStreamAsync(serverId, parameters, ct).ConfigureAwait(false);

        var demuxer = new DockerLogDemuxer(demultiplex: !isTty);
        var buffer = new byte[8192];
        var results = new List<ConsoleLine>();
        long offset = 0;

        await using (rawStream.ConfigureAwait(false))
        {
            int read;
            while ((read = await rawStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                foreach (var line in demuxer.Feed(buffer.AsSpan(0, read)))
                {
                    if (offset >= fromOffset && results.Count < count)
                    {
                        results.Add(new ConsoleLine(offset, line.Text, line.Timestamp, line.Stream));
                    }

                    offset++;
                }

                if (results.Count >= count)
                {
                    break;
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">Always thrown: console input is disabled in this milestone.</exception>
    public Task WriteAsync(string serverId, string text, CancellationToken ct = default) =>
        throw new WritesDisabledException(
            "Writing to a server's console (stdin) is disabled in this milestone (M1); interactive input is a later milestone.");

    /// <summary>
    /// Whether the container was created with a TTY, which determines whether its log stream carries
    /// Docker's 8-byte frame headers (see <see cref="DockerLogDemuxer"/>'s constructor).
    /// </summary>
    private async Task<bool> IsTtyAsync(string serverId, CancellationToken ct)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(serverId, ct).ConfigureAwait(false);
            return inspect?.Config?.Tty ?? false;
        }
        catch (DockerContainerNotFoundException ex)
        {
            throw new InvalidOperationException($"Container '{serverId}' was not found.", ex);
        }
    }

    private async Task<Stream> OpenRawLogStreamAsync(string serverId, ContainerLogsParameters parameters, CancellationToken ct)
    {
        try
        {
            // Intentionally using the non-demultiplexed overload: this class implements its own frame
            // de-multiplexer (see DockerLogDemuxer) rather than relying on Docker.DotNet's MultiplexedStream.
#pragma warning disable CS0618
            return await _client.Containers.GetContainerLogsAsync(serverId, parameters, ct).ConfigureAwait(false);
#pragma warning restore CS0618
        }
        catch (DockerContainerNotFoundException ex)
        {
            throw new InvalidOperationException($"Container '{serverId}' was not found.", ex);
        }
    }

    /// <summary>
    /// Reads from the raw log stream, treating a dropped connection as a clean end of enumeration
    /// (rather than an unhandled exception) so callers can reconnect via a fresh <see cref="FollowAsync"/>
    /// call and resume from the last <see cref="ConsoleLine.Offset"/> they observed. The causing
    /// exception, when there is one, is returned rather than swallowed, so the caller can log/inspect why
    /// the stream ended instead of treating a genuine transport failure the same as a graceful close.
    /// </summary>
    private static async Task<(int Read, Exception? StopReason)> TryReadAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            return (read, null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or HttpRequestException or SocketException)
        {
            return (0, ex);
        }
    }

    private long TakeOffset(string serverId) => _nextOffsets.AddOrUpdate(serverId, 1, (_, next) => next + 1) - 1;
}
