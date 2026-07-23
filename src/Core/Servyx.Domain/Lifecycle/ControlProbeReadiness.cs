using System.Text.RegularExpressions;

namespace Servyx.Domain.Lifecycle;

/// <summary>
/// Readiness detector based on an authenticated control-channel probe (RCON/REST). Used as the fallback
/// behind <see cref="LogRegexReadiness"/> for when an upstream game update changes the log format.
/// </summary>
/// <remarks>
/// Authentication is delegated to, and mandatory on, the injected <see cref="IReadinessProbeChannel"/> —
/// see the contract documented there. This type's own responsibility is purely the polling and
/// terminal/non-terminal-failure policy: a connection failure means "not ready yet, keep polling", while
/// an authentication rejection is terminal and must not be retried (see
/// "Readiness vs. Container Health" in <c>docs/architecture.md</c>).
/// </remarks>
public sealed class ControlProbeReadiness : IReadinessDetector
{
    private static readonly TimeSpan ExpectedPatternMatchTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadinessProbeChannel _channel;
    private readonly Regex _expectedResponse;
    private readonly TimeSpan _pollInterval;
    private readonly string _detectorId;

    /// <summary>Creates a detector that polls <paramref name="channel"/> until its response matches <paramref name="expectedResponsePattern"/>.</summary>
    /// <param name="channel">The authenticated control channel to probe.</param>
    /// <param name="expectedResponsePattern">Regex the probe response must match for the server to be considered ready.</param>
    /// <param name="pollInterval">Delay between successive probe attempts.</param>
    /// <param name="detectorId">Identifier recorded on produced signals. Defaults to <c>"control-probe"</c>.</param>
    public ControlProbeReadiness(
        IReadinessProbeChannel channel,
        string expectedResponsePattern,
        TimeSpan pollInterval,
        string detectorId = "control-probe")
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(expectedResponsePattern);
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "Poll interval must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detectorId);

        _channel = channel;
        _expectedResponse = new Regex(expectedResponsePattern, RegexOptions.None, ExpectedPatternMatchTimeout);
        _pollInterval = pollInterval;
        _detectorId = detectorId;
    }

    /// <inheritdoc />
    public async Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var timeoutCts = new CancellationTokenSource(context.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            while (true)
            {
                var attempt = await _channel.ProbeAsync(context.ServerId, linkedCts.Token).ConfigureAwait(false);

                switch (attempt)
                {
                    case ProbeAttempt.Responded responded when IsMatch(responded.Response):
                        return new ReadinessSignal(
                            Ready: true,
                            DetectorId: _detectorId,
                            Detail: $"control probe responded: \"{responded.Response}\"");

                    case ProbeAttempt.AuthenticationRejected rejected:
                        // Terminal: do not retry a bad credential — some servers lock out after repeated
                        // failed attempts.
                        return new ReadinessSignal(
                            Ready: false,
                            DetectorId: _detectorId,
                            Detail: $"control probe authentication rejected: {rejected.Reason}");

                    default:
                        // Responded-but-not-matching, or ConnectionFailed: not ready yet, keep polling.
                        break;
                }

                await Task.Delay(_pollInterval, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Only the per-attempt timeout elapsed; the caller's own token was not cancelled.
            return new ReadinessSignal(
                Ready: false,
                DetectorId: _detectorId,
                Detail: $"no matching control probe response within {context.Timeout}");
        }
    }

    private bool IsMatch(string response)
    {
        try
        {
            return _expectedResponse.IsMatch(response);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
