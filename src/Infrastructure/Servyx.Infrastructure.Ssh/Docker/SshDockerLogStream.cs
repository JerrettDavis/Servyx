using System.Globalization;
using System.Runtime.CompilerServices;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="ILogStream"/> implementation for the ssh+docker transport: reads a container's console
/// output via <c>docker logs --tail N --timestamps</c> run over an existing SSH exec channel, rather than
/// the Docker Engine API's logs endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Takes an already-connected <see cref="IExecutionTarget"/>, for the same reason as
/// <see cref="SshDockerServerDiscovery"/>: it mirrors <c>DockerLogStream</c> holding a persistent
/// <c>IDockerClient</c>, and a caller managing a remote server already holds one connected session to
/// reuse across every read surface rather than reconnecting per call.
/// </para>
/// <para>
/// <strong>This is a one-shot tail, not a live follow.</strong> <see cref="DockerCli.Logs"/> is the only
/// read-only log command this assembly exposes, and it has no <c>--follow</c> mode — it runs to
/// completion and returns the trailing <c>tailLines</c> lines as a single, non-streaming
/// <see cref="CommandResult"/> (SSH's <see cref="IExecutionTarget.ExecuteAsync"/> has no way to express an
/// unbounded, still-running remote process safely under the write-guard's read-only contract). So
/// <see cref="FollowAsync"/> replays the requested backlog once and then completes, rather than
/// continuing to yield new lines as <c>DockerLogStream.FollowAsync</c> does against the Engine API's
/// streaming logs endpoint. Callers that need to keep watching must call <see cref="FollowAsync"/> again.
/// </para>
/// <para>
/// <strong>Timestamp handling matches <c>DockerLogDemuxer.SplitTimestamp</c> exactly:</strong> each line
/// begins with docker's <c>--timestamps</c> RFC3339Nano prefix (e.g. <c>2024-01-01T00:00:00.123456789Z </c>),
/// which is parsed into <see cref="ConsoleLine.Timestamp"/> and stripped from <see cref="ConsoleLine.Text"/>
/// — never left in the returned text. A line with no valid timestamp prefix falls back to the current
/// time with the line left unmodified, same as the Docker-backed implementation.
/// </para>
/// <para>
/// Unlike the Engine API (which docker's own client library de-multiplexes into separate stdout/stderr
/// frames for a non-TTY container), running <c>docker logs</c> as a CLI process over SSH lets the
/// <c>docker</c> executable do that de-multiplexing itself: a container's stdout log lines land on the
/// CLI's own <see cref="CommandResult.StandardOutput"/>, and its stderr log lines land on
/// <see cref="CommandResult.StandardError"/>. So both are parsed here and attributed accordingly, with
/// stdout lines ordered before stderr lines (the two streams are captured as complete, separate strings by
/// the exec channel, so true cross-stream interleave order is not observable from a non-streaming result).
/// </para>
/// </remarks>
public sealed class SshDockerLogStream : ILogStream
{
    private const int DefaultTailLines = 200;

    private readonly IExecutionTarget _target;

    /// <summary>Creates a log stream operating against an already-connected SSH session.</summary>
    public SshDockerLogStream(IExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <inheritdoc />
    public bool SupportsInput => false;

    /// <inheritdoc />
    /// <remarks>See the type-level remarks: this replays the requested backlog once, then completes.</remarks>
    public async IAsyncEnumerable<ConsoleLine> FollowAsync(
        string serverId,
        ConsoleTailOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(options);

        var lines = await ReadTailAsync(serverId, options.MaxBacklogLines, ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            yield return line;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Backed by the same one-shot <c>docker logs --tail</c> read as <see cref="FollowAsync"/> — there is
    /// no separate offset-indexed store here, so this re-reads a large enough tail to cover
    /// <paramref name="fromOffset"/> + <paramref name="count"/> and slices it, rather than reading from an
    /// append-only file index as <c>docs/architecture.md</c>'s production design calls for.
    /// </remarks>
    public async Task<IReadOnlyList<ConsoleLine>> ReadAsync(
        string serverId, long fromOffset, int count, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (count <= 0)
        {
            return [];
        }

        var upperBound = fromOffset + count;
        var tailLines = upperBound > DefaultTailLines && upperBound <= int.MaxValue
            ? (int)upperBound
            : DefaultTailLines;

        var lines = await ReadTailAsync(serverId, tailLines, ct).ConfigureAwait(false);
        return lines.Where(l => l.Offset >= fromOffset).Take(count).ToList();
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">Always thrown: console input is not supported by this transport.</exception>
    public Task WriteAsync(string serverId, string text, CancellationToken ct = default) =>
        throw new WritesDisabledException(
            "Writing to a server's console (stdin) is not supported over the ssh+docker transport's read-only observation surface.");

    /// <exception cref="InvalidOperationException">
    /// <c>docker logs</c> exited non-zero. Never swallowed: a failed read surfaces loudly rather than
    /// silently returning an empty tail, so a broken SSH/docker path cannot masquerade as "no output yet".
    /// </exception>
    private async Task<IReadOnlyList<ConsoleLine>> ReadTailAsync(string serverId, int tailLines, CancellationToken ct)
    {
        var effectiveTail = tailLines > 0 ? tailLines : DefaultTailLines;

        var result = await _target.ExecuteAsync(DockerCli.Logs(serverId, effectiveTail), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"'docker logs' failed for container '{serverId}' (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        var lines = new List<ConsoleLine>();
        long offset = 0;
        offset = AppendLines(lines, result.StandardOutput, OutputStream.StdOut, offset);
        AppendLines(lines, result.StandardError, OutputStream.StdErr, offset);
        return lines;
    }

    private static long AppendLines(List<ConsoleLine> destination, string text, OutputStream stream, long startOffset)
    {
        var offset = startOffset;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var (timestamp, content) = SplitTimestamp(line);
            destination.Add(new ConsoleLine(offset, content, timestamp, stream));
            offset++;
        }

        return offset;
    }

    /// <summary>
    /// Splits docker's <c>--timestamps</c> RFC3339Nano prefix (e.g. <c>2024-01-01T00:00:00.123456789Z </c>)
    /// off the front of a line, matching <c>DockerLogDemuxer.SplitTimestamp</c>'s behavior exactly: falls
    /// back to the current time and the line unmodified if no valid timestamp prefix is present.
    /// </summary>
    private static (DateTimeOffset Timestamp, string Text) SplitTimestamp(string line)
    {
        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0)
        {
            var candidate = line[..spaceIndex];
            if (DateTimeOffset.TryParse(
                    candidate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp))
            {
                return (timestamp, line[(spaceIndex + 1)..]);
            }
        }

        return (DateTimeOffset.UtcNow, line);
    }
}
