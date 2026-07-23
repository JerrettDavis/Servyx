using System.Threading.Channels;
using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Tests.Lifecycle;

/// <summary>A controllable <see cref="ILogLineSource"/> fed by the test rather than real I/O.</summary>
internal sealed class FakeLogLineSource : ILogLineSource
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();

    public void Emit(string line) => _channel.Writer.TryWrite(line);

    public void Complete() => _channel.Writer.TryComplete();

    public IAsyncEnumerable<string> TailAsync(string serverId, CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>A controllable <see cref="IReadinessProbeChannel"/> that replays a scripted sequence of attempts.</summary>
internal sealed class FakeProbeChannel : IReadinessProbeChannel
{
    private readonly Queue<ProbeAttempt> _scripted;
    private readonly ProbeAttempt _afterScriptEnds;

    public FakeProbeChannel(IEnumerable<ProbeAttempt> scripted, ProbeAttempt? afterScriptEnds = null)
    {
        _scripted = new Queue<ProbeAttempt>(scripted);
        _afterScriptEnds = afterScriptEnds ?? new ProbeAttempt.ConnectionFailed("script exhausted");
    }

    public int CallCount { get; private set; }

    public Task<ProbeAttempt> ProbeAsync(string serverId, CancellationToken ct)
    {
        CallCount++;
        var attempt = _scripted.Count > 0 ? _scripted.Dequeue() : _afterScriptEnds;
        return Task.FromResult(attempt);
    }
}

/// <summary>A minimal <see cref="IReadinessDetector"/> whose behaviour is supplied by a delegate, for composing race scenarios.</summary>
internal sealed class FakeDetector : IReadinessDetector
{
    private readonly Func<ReadinessContext, CancellationToken, Task<ReadinessSignal>> _impl;

    public FakeDetector(Func<ReadinessContext, CancellationToken, Task<ReadinessSignal>> impl) => _impl = impl;

    public Task<ReadinessSignal> WaitForReadyAsync(ReadinessContext context, CancellationToken ct = default)
        => _impl(context, ct);

    /// <summary>A detector that becomes ready after <paramref name="delay"/>, honouring cancellation.</summary>
    public static FakeDetector ReadyAfter(TimeSpan delay, string detectorId, string detail = "ready")
        => new(async (_, ct) =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return new ReadinessSignal(true, detectorId, detail);
        });

    /// <summary>A detector that reports not-ready after <paramref name="delay"/>, honouring cancellation.</summary>
    public static FakeDetector FailsAfter(TimeSpan delay, string detectorId, string detail = "not ready")
        => new(async (_, ct) =>
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return new ReadinessSignal(false, detectorId, detail);
        });

    /// <summary>A detector that throws immediately instead of returning a signal.</summary>
    public static FakeDetector Throws(Exception exception)
        => new((_, _) => throw exception);

    /// <summary>
    /// A detector that waits until cancelled and then sets <paramref name="wasCancelled"/>, used to assert
    /// that composite race losers are cancelled promptly instead of left running.
    /// </summary>
    public static FakeDetector WaitsUntilCancelled(TaskCompletionSource<bool> wasCancelled)
        => new(async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled.TrySetResult(true);
                throw;
            }

            return new ReadinessSignal(true, "unreachable", null);
        });
}
