using ModelContextProtocol;

namespace Servyx.Mcp;

/// <summary>
/// Emits a progress notification every <see cref="Interval"/> while a long, opaque call runs, so an MCP
/// client resets its request timeout instead of abandoning a stop ladder mid-escalation. The application
/// layer exposes no per-stage callback, so this reports elapsed-against-worst-case rather than claiming
/// to know which stage is running — an honest heartbeat, not a fabricated one.
/// </summary>
/// <remarks>
/// A future HTTP host <b>must</b> set <c>HttpServerTransportOptions.Stateless = false</c>. The 2.x line
/// of the MCP SDK defaults that flag to <see langword="true"/>, and a stateless server has nowhere to
/// hold the session a server→client notification rides on — every notification, this heartbeat included,
/// is silently dropped rather than delivered, which would look exactly like a healthy long call rather
/// than a broken one.
/// </remarks>
internal sealed class ProgressHeartbeat : IAsyncDisposable
{
    /// <summary>How often a heartbeat notification is emitted while the guarded call is in flight.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    private ProgressHeartbeat(CancellationTokenSource cts, Task loop)
    {
        _cts = cts;
        _loop = loop;
    }

    /// <summary>
    /// Starts emitting heartbeats. Returns immediately; the heartbeat runs on a background loop until
    /// <see cref="DisposeAsync"/> is called or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="progress">
    /// The caller's progress sink, or <see langword="null"/> when the client did not request progress
    /// notifications for this call — in which case this method returns a heartbeat whose loop never runs,
    /// rather than reporting to nothing.
    /// </param>
    /// <param name="message">A human-readable statement of what is in progress, repeated on every tick.</param>
    /// <param name="worstCase">
    /// The worst-case duration this call is documented to take, reported as <see cref="ProgressNotificationValue.Total"/>.
    /// Pass <see langword="null"/> when no such budget exists (e.g. a readiness wait bounded only by a
    /// per-definition timeout the caller hasn't computed) — <see cref="ProgressNotificationValue.Total"/> is
    /// itself nullable, and this heartbeat leaves it unset rather than inventing a number to report.
    /// </param>
    /// <param name="ct">Cancelled when the guarded call's own token is cancelled.</param>
    public static ProgressHeartbeat Start(
        IProgress<ProgressNotificationValue>? progress, string message, TimeSpan? worstCase, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = progress is null ? Task.CompletedTask : RunAsync(progress, message, worstCase, cts.Token);
        return new ProgressHeartbeat(cts, loop);
    }

    private static async Task RunAsync(
        IProgress<ProgressNotificationValue> progress, string message, TimeSpan? worstCase, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);

                var elapsedSeconds = (float)(DateTimeOffset.UtcNow - start).TotalSeconds;
                progress.Report(new ProgressNotificationValue
                {
                    Progress = elapsedSeconds,
                    Total = worstCase.HasValue ? (float)worstCase.Value.TotalSeconds : null,
                    Message = message,
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the guarded call finished (or was cancelled) and Dispose/DisposeAsync stopped the loop.
        }
    }

    /// <summary>Stops the heartbeat loop and waits for its final tick (if any) to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — see RunAsync.
        }

        _cts.Dispose();
    }
}
