using System.Diagnostics;
using FluentAssertions;
using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Tests.Lifecycle;

public class LogRegexReadinessTests
{
    private const string PalworldPattern = @"Running Palworld dedicated server on \[?[0-9A-Fa-f:.]+\]?:(?<port>\d+)";

    [Fact]
    public async Task WaitForReadyAsync_ReportsMatchingLineAsEvidence_WhenPatternAppears()
    {
        var source = new FakeLogLineSource();
        source.Emit("Starting up...");
        source.Emit("Running Palworld dedicated server on 0.0.0.0:8211");

        var detector = new LogRegexReadiness(source, PalworldPattern);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await detector.WaitForReadyAsync(context);

        signal.Ready.Should().BeTrue();
        signal.DetectorId.Should().Be("log-regex");
        signal.Detail.Should().Contain("Running Palworld dedicated server on 0.0.0.0:8211");
    }

    [Fact]
    public async Task WaitForReadyAsync_ExposesNamedCaptureGroups_IncludingPort()
    {
        var source = new FakeLogLineSource();
        source.Emit("Running Palworld dedicated server on 0.0.0.0:27015");

        var detector = new LogRegexReadiness(source, PalworldPattern);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await detector.WaitForReadyAsync(context);

        signal.CapturedGroups.Should().NotBeNull();
        signal.CapturedGroups!.Should().ContainKey("port").WhoseValue.Should().Be("27015");
    }

    [Fact]
    public async Task WaitForReadyAsync_TimesOutWithoutThrowing_AndAttachesLast50Lines()
    {
        var source = new FakeLogLineSource();
        for (var i = 0; i < 60; i++)
        {
            source.Emit($"log line {i}");
        }

        var detector = new LogRegexReadiness(source, PalworldPattern);
        var context = new ReadinessContext("srv-1", TimeSpan.FromMilliseconds(150));

        var signal = await detector.WaitForReadyAsync(context);

        signal.Ready.Should().BeFalse();
        signal.RecentLogLines.Should().NotBeNull();
        signal.RecentLogLines!.Should().HaveCount(LogRegexReadiness.RecentLinesCapacity);
        signal.RecentLogLines!.Should().Equal(Enumerable.Range(10, 50).Select(i => $"log line {i}"));
    }

    [Fact]
    public async Task WaitForReadyAsync_DoesNotHang_OnCatastrophicBacktrackingPattern()
    {
        // A backreference forces the fallback to RegexOptions.Compiled (NonBacktracking rejects it),
        // and "(a+)+" against a non-matching tail is the textbook catastrophic-backtracking shape.
        const string catastrophicPattern = "^(a+)+\\1$";
        var source = new FakeLogLineSource();
        source.Emit(new string('a', 40) + "!");

        var detector = new LogRegexReadiness(source, catastrophicPattern, matchTimeout: TimeSpan.FromMilliseconds(100));
        var context = new ReadinessContext("srv-1", TimeSpan.FromMilliseconds(400));

        var stopwatch = Stopwatch.StartNew();
        var signal = await detector.WaitForReadyAsync(context);
        stopwatch.Stop();

        signal.Ready.Should().BeFalse();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WaitForReadyAsync_PropagatesCancellation_Promptly()
    {
        var source = new FakeLogLineSource();
        var detector = new LogRegexReadiness(source, PalworldPattern);
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
