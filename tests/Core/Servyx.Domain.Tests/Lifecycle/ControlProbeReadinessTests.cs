using System.Diagnostics;
using FluentAssertions;
using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Tests.Lifecycle;

public class ControlProbeReadinessTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task WaitForReadyAsync_Succeeds_WhenChannelReturnsMatchingResponse()
    {
        var channel = new FakeProbeChannel([new ProbeAttempt.Responded("OK players=0")]);
        var detector = new ControlProbeReadiness(channel, "^OK", PollInterval);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await detector.WaitForReadyAsync(context);

        signal.Ready.Should().BeTrue();
        signal.DetectorId.Should().Be("control-probe");
        signal.Detail.Should().Contain("OK players=0");
    }

    [Fact]
    public async Task WaitForReadyAsync_KeepsPolling_ThroughConnectionFailures()
    {
        var channel = new FakeProbeChannel(
        [
            new ProbeAttempt.ConnectionFailed("refused"),
            new ProbeAttempt.ConnectionFailed("refused"),
            new ProbeAttempt.Responded("READY"),
        ]);
        var detector = new ControlProbeReadiness(channel, "^READY$", PollInterval);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await detector.WaitForReadyAsync(context);

        signal.Ready.Should().BeTrue();
        channel.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task WaitForReadyAsync_StopsImmediately_OnAuthenticationRejection_AndReportsItAsTerminal()
    {
        var channel = new FakeProbeChannel(
        [
            new ProbeAttempt.ConnectionFailed("refused"),
            new ProbeAttempt.AuthenticationRejected("bad password"),
        ]);
        var detector = new ControlProbeReadiness(channel, "^READY$", PollInterval);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var signal = await detector.WaitForReadyAsync(context);
        stopwatch.Stop();

        signal.Ready.Should().BeFalse();
        signal.Detail.Should().Contain("bad password");
        channel.CallCount.Should().Be(2);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForReadyAsync_PropagatesCancellation_Promptly()
    {
        var channel = new FakeProbeChannel([], afterScriptEnds: new ProbeAttempt.ConnectionFailed("still down"));
        var detector = new ControlProbeReadiness(channel, "^READY$", PollInterval);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        var stopwatch = Stopwatch.StartNew();
        var act = () => detector.WaitForReadyAsync(context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
