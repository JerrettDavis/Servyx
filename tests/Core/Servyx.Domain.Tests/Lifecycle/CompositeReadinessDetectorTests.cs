using FluentAssertions;
using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Tests.Lifecycle;

public class CompositeReadinessDetectorTests
{
    [Fact]
    public async Task WaitForReadyAsync_FirstSuccessWins_AndIsIdentifiedOnTheSignal()
    {
        var fast = FakeDetector.ReadyAfter(TimeSpan.FromMilliseconds(10), "fast-winner", "fast evidence");
        var slow = FakeDetector.FailsAfter(TimeSpan.FromMilliseconds(500), "slow-loser", "would have failed anyway");

        var composite = new CompositeReadinessDetector([fast, slow]);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await composite.WaitForReadyAsync(context);

        signal.Ready.Should().BeTrue();
        signal.DetectorId.Should().Be("fast-winner");
        signal.Detail.Should().Be("fast evidence");
    }

    [Fact]
    public async Task WaitForReadyAsync_CancelsLosingDetectors_Promptly()
    {
        var loserCancelled = new TaskCompletionSource<bool>();
        var winner = FakeDetector.ReadyAfter(TimeSpan.FromMilliseconds(10), "winner");
        var loser = FakeDetector.WaitsUntilCancelled(loserCancelled);

        var composite = new CompositeReadinessDetector([winner, loser]);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await composite.WaitForReadyAsync(context);
        signal.Ready.Should().BeTrue();

        var completed = await Task.WhenAny(loserCancelled.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        completed.Should().Be(loserCancelled.Task, "the losing detector should have been cancelled promptly");
        (await loserCancelled.Task).Should().BeTrue();
    }

    [Fact]
    public async Task WaitForReadyAsync_ThrowingDetector_DoesNotAbortTheRace()
    {
        var throwing = FakeDetector.Throws(new InvalidOperationException("boom"));
        var winner = FakeDetector.ReadyAfter(TimeSpan.FromMilliseconds(30), "winner", "evidence");

        var composite = new CompositeReadinessDetector([throwing, winner]);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await composite.WaitForReadyAsync(context);

        signal.Ready.Should().BeTrue();
        signal.DetectorId.Should().Be("winner");
    }

    [Fact]
    public async Task WaitForReadyAsync_AllFail_AggregatesEveryReason()
    {
        var failA = FakeDetector.FailsAfter(TimeSpan.Zero, "detector-a", "detector-a says no");
        var failB = FakeDetector.Throws(new InvalidOperationException("detector-b exploded"));

        var composite = new CompositeReadinessDetector([failA, failB]);
        var context = new ReadinessContext("srv-1", TimeSpan.FromSeconds(2));

        var signal = await composite.WaitForReadyAsync(context);

        signal.Ready.Should().BeFalse();
        signal.DetectorId.Should().Be("composite");
        signal.Detail.Should().Contain("detector-a says no");
        signal.Detail.Should().Contain("detector-b exploded");
    }

    [Fact]
    public void Constructor_Throws_WhenNoDetectorsSupplied()
    {
        var act = () => new CompositeReadinessDetector([]);

        act.Should().Throw<ArgumentException>();
    }
}
