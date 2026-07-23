namespace Servyx.Domain.Lifecycle;

/// <summary>
/// A source of log lines for a running or starting server. Concrete implementations (tailing a
/// container's stdout, reading an SSH-tailed file, etc.) live outside <c>Servyx.Domain</c> — this
/// interface is the only I/O-shaped dependency <see cref="LogRegexReadiness"/> has, which keeps
/// <c>Servyx.Domain</c> free of any actual I/O.
/// </summary>
public interface ILogLineSource
{
    /// <summary>
    /// Streams log lines for <paramref name="serverId"/> as they are produced. The stream ends when the
    /// underlying source is exhausted, and MUST stop promptly when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<string> TailAsync(string serverId, CancellationToken ct);
}
