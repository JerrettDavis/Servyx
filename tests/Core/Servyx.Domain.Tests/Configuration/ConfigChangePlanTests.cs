using Servyx.Domain.Configuration;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Tests.Configuration;

public class ConfigChangePlanTests
{
    private static PlannedAction ReversibleAction(string surfaceId = "env") =>
        new(PlannedActionKind.WriteSurface, surfaceId, "--- diff ---", Reversible: true, TransportCapabilities.FileWrite);

    private static PlannedAction IrreversibleAction(string surfaceId = "compose") =>
        new(PlannedActionKind.WriteSurface, surfaceId, "--- diff ---", Reversible: false, TransportCapabilities.FileWrite);

    [Fact]
    public void IsFullyReversible_TrueWhenAllActionsReversible()
    {
        var plan = new ConfigChangePlan(
            "plan-1",
            [ReversibleAction(), ReversibleAction("compose")],
            [],
            new Dictionary<string, string>());

        plan.IsFullyReversible.Should().BeTrue();
    }

    [Fact]
    public void IsFullyReversible_FalseWhenAnyActionIrreversible()
    {
        var plan = new ConfigChangePlan(
            "plan-1",
            [ReversibleAction(), IrreversibleAction()],
            [],
            new Dictionary<string, string>());

        plan.IsFullyReversible.Should().BeFalse();
    }

    [Fact]
    public void IsFullyReversible_FalseWhenNoActions()
    {
        var plan = new ConfigChangePlan("plan-1", [], [], new Dictionary<string, string>());

        plan.IsFullyReversible.Should().BeFalse();
    }

    [Fact]
    public void RequiresRestart_TrueWhenConsequenceIncludesRestartRequired()
    {
        var plan = new ConfigChangePlan(
            "plan-1",
            [ReversibleAction()],
            [new Consequence(ConsequenceKind.RestartRequired, "Server must restart to apply.")],
            new Dictionary<string, string>());

        plan.RequiresRestart.Should().BeTrue();
        plan.RequiresRecreate.Should().BeFalse();
    }

    [Fact]
    public void RequiresRecreate_TrueWhenConsequenceIncludesRecreateRequired()
    {
        var plan = new ConfigChangePlan(
            "plan-1",
            [ReversibleAction()],
            [new Consequence(ConsequenceKind.RecreateRequired, "Container must be recreated.")],
            new Dictionary<string, string>());

        plan.RequiresRecreate.Should().BeTrue();
        plan.RequiresRestart.Should().BeFalse();
    }

    [Fact]
    public void RequiresRestartAndRecreate_BothFalseWhenNoMatchingConsequence()
    {
        var plan = new ConfigChangePlan(
            "plan-1",
            [ReversibleAction()],
            [new Consequence(ConsequenceKind.ServiceInterruption, "Brief service blip.")],
            new Dictionary<string, string>());

        plan.RequiresRestart.Should().BeFalse();
        plan.RequiresRecreate.Should().BeFalse();
    }
}
