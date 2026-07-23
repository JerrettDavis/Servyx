namespace Servyx.Domain.Lifecycle;

/// <summary>Context supplied to a readiness detector.</summary>
/// <param name="ServerId">The server being waited on.</param>
/// <param name="Timeout">Maximum time to wait before giving up.</param>
public sealed record ReadinessContext(string ServerId, TimeSpan Timeout);

/// <summary>Result of a readiness check.</summary>
/// <param name="Ready">Whether the server was observed to become ready within the timeout.</param>
/// <param name="DetectorId">Which detector produced this signal. For <see cref="CompositeReadinessDetector"/>,
/// a successful signal carries the id of the sub-detector that actually won the race; a failing signal
/// carries the composite's own id, since it speaks for all of them.</param>
/// <param name="Detail">Human-readable detail, especially useful on failure — e.g. the matched log line,
/// or an aggregation of every sub-detector's failure reason.</param>
/// <param name="CapturedGroups">Named regex capture groups extracted from the evidence that proved
/// readiness (e.g. a game's listening port), if the winning detector produces any. Null when not
/// applicable.</param>
/// <param name="RecentLogLines">The most recent log lines observed before giving up, newest-last, capped
/// at a small fixed count. Populated on a <see cref="LogRegexReadiness"/> timeout so the UI can show the
/// user what the server actually said instead of a bare "timed out". Null when not applicable.</param>
public sealed record ReadinessSignal(
    bool Ready,
    string DetectorId,
    string? Detail,
    IReadOnlyDictionary<string, string>? CapturedGroups = null,
    IReadOnlyList<string>? RecentLogLines = null);

/// <summary>Result of a start attempt.</summary>
/// <param name="Ready">Whether the server became ready.</param>
/// <param name="TimeToReady">How long it took to become ready.</param>
/// <param name="Signal">The readiness signal that confirmed readiness.</param>
public sealed record StartOutcome(bool Ready, TimeSpan TimeToReady, ReadinessSignal Signal);

/// <summary>Detects when a starting server has become ready to serve.</summary>
public interface IReadinessDetector
{
    /// <summary>Waits for the server described by <paramref name="context"/> to become ready, or for the timeout to elapse.</summary>
    Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default);
}
