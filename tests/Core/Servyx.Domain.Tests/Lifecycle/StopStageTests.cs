using Servyx.Domain.Lifecycle;

namespace Servyx.Domain.Tests.Lifecycle;

/// <summary>
/// Pins <see cref="StopStage.ContinueOnError"/>'s per-kind defaults. They are asymmetric on purpose, and a
/// definition that says nothing gets them — so flipping one silently changes how every shipped game
/// definition behaves when a stop stage fails.
/// </summary>
public class StopStageTests
{
    /// <summary>
    /// A control channel is routinely unavailable exactly when an operator most wants the server stopped —
    /// the process is wedged, the port never opened, the password is stale. Defaulting to
    /// <see langword="false"/> would let that wedge the stop permanently instead of escalating.
    /// </summary>
    [Fact]
    public void ControlChannelStages_DefaultToContinuingOnError()
    {
        new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(45)).ContinueOnError.Should().BeTrue();
        new StopStage.ConsoleWrite("save-all", TimeSpan.FromSeconds(30)).ContinueOnError.Should().BeTrue();
    }

    /// <summary>
    /// A signal stage fails only when the container runtime itself refused the call, which is a real fault
    /// worth surfacing rather than escalating past. <see cref="StopStage.Kill"/> is terminal: there is no
    /// later stage for it to escalate to at all.
    /// </summary>
    [Fact]
    public void SignalAndKillStages_DefaultToAbortingOnError()
    {
        new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(300)).ContinueOnError.Should().BeFalse();
        new StopStage.Kill().ContinueOnError.Should().BeFalse();
    }

    [Fact]
    public void ContinueOnError_IsSettableAtConstruction_AndParticipatesInValueEquality()
    {
        var permissive = new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(300)) { ContinueOnError = true };
        var strict = new StopStage.Signal("SIGTERM", TimeSpan.FromSeconds(300));

        permissive.ContinueOnError.Should().BeTrue();
        permissive.Should().NotBe(strict);
        (permissive with { ContinueOnError = false }).Should().Be(strict);
    }
}
