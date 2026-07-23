namespace Servyx.Domain.Lifecycle;

/// <summary>
/// Outcome of a single <see cref="IReadinessProbeChannel"/> attempt.
/// </summary>
public abstract record ProbeAttempt
{
    private ProbeAttempt()
    {
    }

    /// <summary>The channel answered. <see cref="Response"/> is matched against the detector's expected pattern.</summary>
    /// <param name="Response">The raw response payload/text returned by the channel.</param>
    public sealed record Responded(string Response) : ProbeAttempt;

    /// <summary>
    /// The channel could not be reached at all — connection refused, timed out, network unreachable.
    /// This is treated as "not ready yet"; polling continues.
    /// </summary>
    /// <param name="Reason">Human-readable description of the connection failure.</param>
    public sealed record ConnectionFailed(string Reason) : ProbeAttempt;

    /// <summary>
    /// The channel was reached but rejected the supplied credentials. This is a terminal outcome:
    /// retrying a bad password forever is wrong, and on some servers will trigger an account/IP lockout.
    /// </summary>
    /// <param name="Reason">Human-readable description of the rejection.</param>
    public sealed record AuthenticationRejected(string Reason) : ProbeAttempt;
}

/// <summary>
/// A control channel (RCON, REST, etc.) that can be probed to determine whether a server is ready.
/// </summary>
/// <remarks>
/// Implementations live outside <c>Servyx.Domain</c> (protocol/transport code), but the contract is
/// binding: <b>every probe attempt MUST be authenticated</b> before it can report
/// <see cref="ProbeAttempt.Responded"/> or <see cref="ProbeAttempt.AuthenticationRejected"/>. A probe
/// that authenticates no better than a failing healthcheck is worthless — this is precisely the bug that
/// makes the live Palworld container report Docker health <c>unhealthy</c> while the game runs perfectly:
/// its healthcheck queries an authenticated REST endpoint without credentials and always gets a 401. See
/// "Readiness vs. Container Health" in <c>docs/architecture.md</c>. An unauthenticated implementation of
/// this interface would silently reproduce that exact bug for every downstream user of
/// <see cref="ControlProbeReadiness"/>.
/// </remarks>
public interface IReadinessProbeChannel
{
    /// <summary>Performs one authenticated probe attempt against the control channel for <paramref name="serverId"/>.</summary>
    Task<ProbeAttempt> ProbeAsync(string serverId, CancellationToken ct);
}
