using Servyx.Composition;
using Servyx.Domain.Configuration;
using Servyx.Domain.Transport;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Coverage for the pure presentation logic <c>ChangePlanPanel</c> renders through.
/// </summary>
/// <remarks>
/// <see cref="ChangePlanPresentation.Applicability"/> is the single pure function preventing an operator from
/// being shown an "this can be applied" story for a plan <c>IPlanExecutor.ApplyAsync</c> would actually
/// refuse — see <c>PlanExecutor.ApplyAsync</c>'s own handling of a <see cref="PlanFeasibility.Blocked"/> plan
/// and of any <see cref="PlannedActionKind.WriteControlChannel"/> action. <see cref="Applicability_is_exhaustively_correct"/>
/// is table-driven across the full <c>{ReadOnly, PreviewOnly, Enabled} × {gate open, closed} × {control-channel
/// present, absent} × {feasibility}</c> matrix — 36 cases — because getting even one combination wrong would
/// mean showing an operator an apply story the engine cannot deliver.
/// </remarks>
public class ChangePlanPresentationTests
{
    private static readonly PlannedAction WriteAction = new(
        PlannedActionKind.WriteSurface, "env", "--- a/.env\n+++ b/.env\n@@ -1,1 +1,1 @@\n-A=1\n+A=2\n", true,
        TransportCapabilities.FileWrite);

    private static readonly PlannedAction ControlChannelAction = new(
        PlannedActionKind.WriteControlChannel, "rcon", "--- a/rcon\n+++ b/rcon\n@@ -1,1 +1,1 @@\n-x\n+y\n", false,
        TransportCapabilities.ExecuteCommand);

    private static readonly BlockedChange SomeBlockedChange = new(
        "SOME_KEY", "surface", "it could not be written", "fix the definition");

    /// <summary>
    /// Builds a plan with the requested <see cref="PlanFeasibility"/> and, when structurally possible, a
    /// <see cref="PlannedActionKind.WriteControlChannel"/> action among its actions.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanFeasibility.Blocked"/> requires <c>Actions.Count == 0</c> by
    /// <see cref="ConfigChangePlan.Feasibility"/>'s own derivation, so a control-channel action cannot coexist
    /// with it — <paramref name="controlChannelAction"/> is simply ignored for that feasibility, which is also
    /// why <see cref="Applicability"/> checks <see cref="PlanFeasibility.Blocked"/> before it ever looks at
    /// <see cref="ConfigChangePlan.Actions"/> for a control-channel entry.
    /// </remarks>
    private static ConfigChangePlan BuildPlan(PlanFeasibility feasibility, bool controlChannelAction)
    {
        var action = controlChannelAction ? ControlChannelAction : WriteAction;

        return feasibility switch
        {
            PlanFeasibility.FullyAchievable => new ConfigChangePlan(
                "plan-1", [action], [], new Dictionary<string, string>(StringComparer.Ordinal)),

            PlanFeasibility.PartiallyAchievable => new ConfigChangePlan(
                "plan-1", [action], [], new Dictionary<string, string>(StringComparer.Ordinal))
            {
                Blocked = [SomeBlockedChange],
            },

            _ => new ConfigChangePlan(
                "plan-1", [], [], new Dictionary<string, string>(StringComparer.Ordinal))
            {
                Blocked = [SomeBlockedChange],
            },
        };
    }

    public static TheoryData<WriteMode, bool, bool, PlanFeasibility> Matrix()
    {
        var data = new TheoryData<WriteMode, bool, bool, PlanFeasibility>();

        foreach (var writeMode in new[] { WriteMode.ReadOnly, WriteMode.PreviewOnly, WriteMode.Enabled })
        foreach (var gateOpen in new[] { true, false })
        foreach (var controlChannel in new[] { true, false })
        foreach (var feasibility in new[]
                 {
                     PlanFeasibility.FullyAchievable, PlanFeasibility.PartiallyAchievable, PlanFeasibility.Blocked,
                 })
        {
            data.Add(writeMode, gateOpen, controlChannel, feasibility);
        }

        return data;
    }

    [Fact]
    public void The_gating_matrix_is_genuinely_36_cases()
    {
        Matrix().Count.Should().Be(3 * 2 * 2 * 3);
        Matrix().Count.Should().Be(36);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Applicability_is_exhaustively_correct(
        WriteMode writeMode, bool gateOpen, bool controlChannel, PlanFeasibility feasibility)
    {
        var plan = BuildPlan(feasibility, controlChannel);
        var gate = new ProvisioningGate(gateOpen);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, writeMode, gate);

        // The only way to CanApply==true: write access is fully Enabled, the plan is not Blocked, and it
        // carries no control-channel action — matching PlanExecutor.ApplyAsync's own refusal conditions.
        var expected = writeMode == WriteMode.Enabled
            && feasibility != PlanFeasibility.Blocked
            && !controlChannel;

        canApply.Should().Be(expected, because:
            $"writeMode={writeMode}, gateOpen={gateOpen}, controlChannel={controlChannel}, feasibility={feasibility}");
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Blocked_feasibility_refuses_regardless_of_write_mode_or_gate()
    {
        var plan = BuildPlan(PlanFeasibility.Blocked, controlChannelAction: false);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.Enabled, new ProvisioningGate(true));

        canApply.Should().BeFalse();
        reason.Should().Contain("Nothing in this plan can be written");
    }

    [Fact]
    public void A_control_channel_action_refuses_the_whole_plan_even_when_write_access_is_enabled()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: true);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.Enabled, new ProvisioningGate(true));

        canApply.Should().BeFalse();
        reason.Should().Contain("control-channel action");
        reason.Should().Contain("nothing would be written");
    }

    [Fact]
    public void PreviewOnly_with_an_open_gate_points_at_the_overview_tab()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: false);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.PreviewOnly, new ProvisioningGate(true));

        canApply.Should().BeFalse();
        reason.Should().Contain("Raise write access on the Overview tab");
    }

    [Fact]
    public void PreviewOnly_with_a_closed_gate_says_the_tier_cannot_be_raised_from_the_UI_at_all()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: false);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.PreviewOnly, new ProvisioningGate(false));

        canApply.Should().BeFalse();
        reason.Should().Contain(ProvisioningGate.ConfigurationKey);
        reason.Should().Contain("cannot be raised in this process at all");
    }

    [Fact]
    public void ReadOnly_with_a_closed_gate_also_names_the_configuration_key()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: false);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.ReadOnly, new ProvisioningGate(false));

        canApply.Should().BeFalse();
        reason.Should().Contain(ProvisioningGate.ConfigurationKey);
    }

    [Fact]
    public void A_fully_achievable_plan_under_enabled_write_access_can_be_applied()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: false);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, WriteMode.Enabled, new ProvisioningGate(true));

        canApply.Should().BeTrue();
        reason.Should().Contain("can be applied");
    }

    [Fact]
    public void A_partially_achievable_plan_under_enabled_write_access_can_still_be_applied()
    {
        // Applying a PartiallyAchievable plan does exactly what its Actions describe — the blocked changes
        // were never Actions to begin with, so PlanExecutor.ApplyAsync has nothing to refuse them over.
        var plan = BuildPlan(PlanFeasibility.PartiallyAchievable, controlChannelAction: false);

        var (canApply, _) = ChangePlanPresentation.Applicability(plan, WriteMode.Enabled, new ProvisioningGate(true));

        canApply.Should().BeTrue();
    }

    [Fact]
    public void Applicability_throws_on_a_null_plan_or_gate()
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: false);

        var act1 = () => ChangePlanPresentation.Applicability(null!, WriteMode.Enabled, ProvisioningGate.Closed);
        var act2 = () => ChangePlanPresentation.Applicability(plan, WriteMode.Enabled, null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(ConsequenceKind.RestartRequired, "Restart required")]
    [InlineData(ConsequenceKind.ServiceInterruption, "Service interruption")]
    public void Describe_labels_restart_and_interruption_plainly(ConsequenceKind kind, string expected)
    {
        ChangePlanPresentation.Describe(kind).Should().Be(expected);
    }

    [Fact]
    public void Describe_names_that_nothing_recreates_a_container()
    {
        var description = ChangePlanPresentation.Describe(ConsequenceKind.RecreateRequired);

        description.Should().Contain("nothing in Servyx recreates a container",
            because: "a badge that only said \"recreate required\" could be misread as Servyx doing it");
    }

    [Theory]
    [InlineData(PlanFeasibility.FullyAchievable, "can be written")]
    [InlineData(PlanFeasibility.PartiallyAchievable, "the rest are blocked and applying will not perform them")]
    [InlineData(PlanFeasibility.Blocked, "nothing can be written; approving would do nothing")]
    public void Summarize_matches_the_specified_copy(PlanFeasibility feasibility, string expected)
    {
        ChangePlanPresentation.Summarize(feasibility).Should().Be(expected);
    }
}
