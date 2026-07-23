namespace Servyx.Domain.Lifecycle;

/// <summary>Context supplied to a readiness detector.</summary>
/// <param name="ServerId">The server being waited on.</param>
/// <param name="Timeout">Maximum time to wait before giving up.</param>
public sealed record ReadinessContext(string ServerId, TimeSpan Timeout);

/// <summary>Result of a readiness check.</summary>
/// <param name="Ready">Whether the server was observed to become ready within the timeout.</param>
/// <param name="DetectorId">Which detector produced this signal.</param>
/// <param name="Detail">Human-readable detail, especially useful on failure.</param>
public sealed record ReadinessSignal(bool Ready, string DetectorId, string? Detail);

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

/// <summary>
/// Readiness detector based on matching a regex against console output. Definition-supplied regexes are
/// untrusted input: implementations MUST compile the pattern with <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>
/// and evaluate it with a per-match timeout, so a malicious or accidental catastrophic-backtracking
/// pattern cannot become a ReDoS vector against the panel host. The concrete matching logic is
/// implemented outside <c>Servyx.Domain</c>; this type is a placeholder for the detector's identity.
/// </summary>
public sealed class LogRegexReadiness : IReadinessDetector
{
    /// <inheritdoc />
    public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>
/// Readiness detector based on an authenticated control-channel probe. Used as a fallback behind
/// <see cref="LogRegexReadiness"/>, and must never be weaker than the container's own health signal — see
/// "Readiness vs. Container Health" in <c>docs/architecture.md</c>. The concrete probing logic is
/// implemented outside <c>Servyx.Domain</c>; this type is a placeholder for the detector's identity.
/// </summary>
public sealed class ControlProbeReadiness : IReadinessDetector
{
    /// <inheritdoc />
    public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
        => throw new NotImplementedException();
}
