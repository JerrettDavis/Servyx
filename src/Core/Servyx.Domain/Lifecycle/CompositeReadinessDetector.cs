namespace Servyx.Domain.Lifecycle;

/// <summary>
/// Races several <see cref="IReadinessDetector"/>s against the same server and reports the first success.
/// </summary>
/// <remarks>
/// This is how <see cref="LogRegexReadiness"/> and <see cref="ControlProbeReadiness"/> are normally
/// combined: log matching is fast and cheap when it works, and the authenticated control probe is the
/// fallback for when an upstream game update changes the log format. Whichever detector wins is recorded
/// on the returned <see cref="ReadinessSignal"/> — its <see cref="ReadinessSignal.DetectorId"/> and
/// <see cref="ReadinessSignal.Detail"/> are returned unmodified from the winning sub-detector, so "how did
/// we know it was ready" is preserved as diagnostic output rather than collapsed into a generic
/// "composite: ready".
/// </remarks>
public sealed class CompositeReadinessDetector : IReadinessDetector
{
    private readonly IReadOnlyList<IReadinessDetector> _detectors;
    private readonly string _detectorId;

    /// <summary>Creates a detector that races <paramref name="detectors"/>.</summary>
    /// <param name="detectors">The sub-detectors to race. Must contain at least one entry.</param>
    /// <param name="detectorId">Identifier recorded on the aggregated all-failed signal. Defaults to <c>"composite"</c>.</param>
    public CompositeReadinessDetector(IReadOnlyList<IReadinessDetector> detectors, string detectorId = "composite")
    {
        ArgumentNullException.ThrowIfNull(detectors);
        if (detectors.Count == 0)
        {
            throw new ArgumentException("At least one detector is required.", nameof(detectors));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detectorId);

        _detectors = detectors;
        _detectorId = detectorId;
    }

    /// <inheritdoc />
    public async Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var remaining = _detectors
            .Select(detector => RunOneAsync(detector, context, raceCts.Token))
            .ToList();

        var reasons = new List<string>();

        try
        {
            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(completed);

                // Propagates if this is a genuine failure of the awaited task (including caller
                // cancellation surfacing through it) rather than a recorded Attempt.
                var attempt = await completed.ConfigureAwait(false);

                if (attempt.Signal is { Ready: true } winningSignal)
                {
                    // First success wins: cancel every other detector promptly.
                    await raceCts.CancelAsync().ConfigureAwait(false);
                    return winningSignal;
                }

                reasons.Add(attempt.Reason!);
            }
        }
        finally
        {
            if (!raceCts.IsCancellationRequested)
            {
                await raceCts.CancelAsync().ConfigureAwait(false);
            }
        }

        return new ReadinessSignal(
            Ready: false,
            DetectorId: _detectorId,
            Detail: $"all {_detectors.Count} readiness detector(s) failed: {string.Join(" | ", reasons)}");
    }

    /// <summary>
    /// Runs a single detector and converts its outcome into an <see cref="Attempt"/> that never throws for
    /// a detector-local failure — an exception from <paramref name="detector"/> is recorded as that
    /// detector's failure reason rather than aborting the race, so its siblings keep running. Cancellation
    /// originating from <paramref name="ct"/> (whether the caller cancelled, or this detector lost the
    /// race) still propagates as an exception out of the returned task.
    /// </summary>
    private static async Task<Attempt> RunOneAsync(IReadinessDetector detector, ReadinessContext context, CancellationToken ct)
    {
        ReadinessSignal signal;
        try
        {
            signal = await detector.WaitForReadyAsync(context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation or race-loss cancellation: propagate, don't record as a reason.
            throw;
        }
        catch (Exception ex)
        {
            return new Attempt(null, $"{detector.GetType().Name}: threw {ex.GetType().Name}: {ex.Message}");
        }

        return signal.Ready
            ? new Attempt(signal, null)
            : new Attempt(null, $"{signal.DetectorId}: {signal.Detail}");
    }

    private readonly record struct Attempt(ReadinessSignal? Signal, string? Reason);
}
