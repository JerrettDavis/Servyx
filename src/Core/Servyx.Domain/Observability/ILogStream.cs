using Servyx.Domain.Transport;

namespace Servyx.Domain.Observability;

/// <summary>
/// A single line of console output. <see cref="Offset"/> allows a client to resume streaming after a
/// socket drop without re-reading from the start. Every line passes through the global secret redactor
/// before being yielded by <see cref="ILogStream"/>.
/// </summary>
/// <param name="Offset">Monotonically increasing position of this line in the server's console index.</param>
/// <param name="Text">The line's text.</param>
/// <param name="Timestamp">When the line was produced.</param>
/// <param name="Stream">Which stream (stdout/stderr) the line came from.</param>
public sealed record ConsoleLine(long Offset, string Text, DateTimeOffset Timestamp, OutputStream Stream);

/// <summary>Options controlling how much backscroll to replay when following console output.</summary>
/// <param name="MaxBacklogLines">Maximum number of historical lines to replay before following new output.</param>
public sealed record ConsoleTailOptions(int MaxBacklogLines);

/// <summary>
/// Provides access to a server's console output, backed by append-only, rotated files with an offset
/// index — not the relational database.
/// </summary>
public interface ILogStream
{
    /// <summary>Replays tail backscroll per <paramref name="options"/>, then follows new output.</summary>
    IAsyncEnumerable<ConsoleLine> FollowAsync(string serverId, ConsoleTailOptions options, CancellationToken ct = default);

    /// <summary>Reads a range from the on-disk index directly, without touching the live workload.</summary>
    Task<IReadOnlyList<ConsoleLine>> ReadAsync(string serverId, long fromOffset, int count, CancellationToken ct = default);

    /// <summary>Writes a line to the server's stdin. Requires the <c>server.console.write</c> scope.</summary>
    Task WriteAsync(string serverId, string text, CancellationToken ct = default);

    /// <summary>Whether this server's transport supports interactive input.</summary>
    bool SupportsInput { get; }
}
