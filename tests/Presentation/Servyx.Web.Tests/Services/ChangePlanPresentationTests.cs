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
/// present, absent} × {feasibility}</c> matrix, because getting even one combination wrong would mean showing
/// an operator an apply story the engine cannot deliver.
/// <para>
/// The table asserts the <em>category</em> of refusal, not only the <c>CanApply</c> boolean. More than one
/// refusal can be true at once and only the first-checked one is shown; a boolean-only matrix cannot tell a
/// correct precedence from a reordered one, because reordering changes only which sentence an operator reads.
/// </para>
/// <para>
/// <see cref="Cases"/> is written out literally, one row per input, and is deliberately NOT computed from any
/// expression resembling <see cref="ChangePlanPresentation.Applicability"/>'s own control flow — an expected
/// value derived the same way the production code derives its answer would agree with any implementation,
/// including a wrong one.
/// </para>
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

    /// <summary>
    /// The distinct operator-facing outcomes <see cref="ChangePlanPresentation.Applicability"/> can produce.
    /// Named after what an operator is told, not after the branch that produced it.
    /// </summary>
    public enum ReasonCategory
    {
        /// <summary>Nothing refuses this plan; it could be applied.</summary>
        Applicable,

        /// <summary>Every requested change was blocked, so approving would write nothing.</summary>
        NothingToWrite,

        /// <summary><c>PlanExecutor.ApplyAsync</c> refuses the whole plan over a control-channel action.</summary>
        ControlChannelRefusal,

        /// <summary>Preview-only, and the write tier can still be raised from the Overview tab.</summary>
        PreviewOnlyRaiseOnOverview,

        /// <summary>Preview-only, and the provisioning gate makes raising the tier impossible in this process.</summary>
        PreviewOnlyGateClosed,

        /// <summary>Read-only, and the write tier can still be raised from the Overview tab.</summary>
        ReadOnlyRaiseOnOverview,

        /// <summary>Read-only, and the provisioning gate makes raising the tier impossible in this process.</summary>
        ReadOnlyGateClosed,
    }

    /// <summary>
    /// Marker phrases that identify which category a returned <c>Reason</c> belongs to. These read the OUTPUT
    /// copy; they say nothing about which input produces which category — that is <see cref="Cases"/>' job.
    /// </summary>
    private static readonly (ReasonCategory Category, string[] Markers)[] ReasonMarkers =
    [
        (ReasonCategory.Applicable, ["This plan can be applied."]),
        (ReasonCategory.NothingToWrite, ["Nothing in this plan can be written"]),
        (ReasonCategory.ControlChannelRefusal, ["control-channel action"]),
        (ReasonCategory.PreviewOnlyRaiseOnOverview, ["Preview only", "Raise write access on the Overview tab"]),
        (ReasonCategory.PreviewOnlyGateClosed, ["Preview only", ProvisioningGate.ConfigurationKey]),
        (ReasonCategory.ReadOnlyRaiseOnOverview, ["read-only", "Overview tab"]),
        (ReasonCategory.ReadOnlyGateClosed, ["read-only", ProvisioningGate.ConfigurationKey]),
    ];

    private static ReasonCategory Categorize(string reason)
    {
        var matched = ReasonMarkers
            .Where(m => m.Markers.All(marker => reason.Contains(marker, StringComparison.Ordinal)))
            .Select(m => m.Category)
            .ToList();

        matched.Should().ContainSingle(because:
            $"every reason must be unambiguously one operator-facing outcome, but \"{reason}\" matched "
            + $"{matched.Count}");

        return matched[0];
    }

    /// <summary>
    /// One row per distinct input, each with the outcome an operator should be shown — read off
    /// <c>PlanExecutor.ApplyAsync</c>'s own refusal conditions and the stated precedence (Blocked, then
    /// control-channel, then write posture), written down by hand rather than recomputed here.
    /// </summary>
    /// <remarks>
    /// <see cref="PlanFeasibility.Blocked"/> forces <c>Actions.Count == 0</c>, so a control-channel action
    /// cannot coexist with it — <c>controlChannel: true</c> rows are omitted for that feasibility rather than
    /// listed as extra cases that feed <see cref="Applicability"/> a byte-identical plan twice.
    /// </remarks>
    private static readonly (WriteMode WriteMode, bool GateOpen, bool ControlChannel, PlanFeasibility Feasibility,
        ReasonCategory Expected)[] Cases =
    [
        // ── Read-only ────────────────────────────────────────────────────────────────────────────────
        (WriteMode.ReadOnly, true, false, PlanFeasibility.FullyAchievable, ReasonCategory.ReadOnlyRaiseOnOverview),
        (WriteMode.ReadOnly, true, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.ReadOnlyRaiseOnOverview),
        (WriteMode.ReadOnly, false, false, PlanFeasibility.FullyAchievable, ReasonCategory.ReadOnlyGateClosed),
        (WriteMode.ReadOnly, false, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.ReadOnlyGateClosed),

        // A control-channel action outranks the read-only refusal: ApplyAsync would refuse the whole plan for
        // it no matter what the write posture were raised to, so pointing at the Overview tab would be a lie.
        (WriteMode.ReadOnly, true, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.ReadOnly, true, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.ReadOnly, false, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.ReadOnly, false, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),

        // ── Preview-only ─────────────────────────────────────────────────────────────────────────────
        (WriteMode.PreviewOnly, true, false, PlanFeasibility.FullyAchievable, ReasonCategory.PreviewOnlyRaiseOnOverview),
        (WriteMode.PreviewOnly, true, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.PreviewOnlyRaiseOnOverview),
        (WriteMode.PreviewOnly, false, false, PlanFeasibility.FullyAchievable, ReasonCategory.PreviewOnlyGateClosed),
        (WriteMode.PreviewOnly, false, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.PreviewOnlyGateClosed),

        // Same precedence, one tier up: still the control-channel refusal, gate open or closed.
        (WriteMode.PreviewOnly, true, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.PreviewOnly, true, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.PreviewOnly, false, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.PreviewOnly, false, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),

        // ── Enabled ──────────────────────────────────────────────────────────────────────────────────
        // The gate does not revoke an existing grant, so a closed gate changes nothing here.
        (WriteMode.Enabled, true, false, PlanFeasibility.FullyAchievable, ReasonCategory.Applicable),
        (WriteMode.Enabled, true, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.Applicable),
        (WriteMode.Enabled, false, false, PlanFeasibility.FullyAchievable, ReasonCategory.Applicable),
        (WriteMode.Enabled, false, false, PlanFeasibility.PartiallyAchievable, ReasonCategory.Applicable),

        (WriteMode.Enabled, true, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.Enabled, true, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.Enabled, false, true, PlanFeasibility.FullyAchievable, ReasonCategory.ControlChannelRefusal),
        (WriteMode.Enabled, false, true, PlanFeasibility.PartiallyAchievable, ReasonCategory.ControlChannelRefusal),

        // ── Blocked ──────────────────────────────────────────────────────────────────────────────────
        // Outranks every write posture and every gate state: there is nothing left to write.
        (WriteMode.ReadOnly, true, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
        (WriteMode.ReadOnly, false, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
        (WriteMode.PreviewOnly, true, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
        (WriteMode.PreviewOnly, false, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
        (WriteMode.Enabled, true, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
        (WriteMode.Enabled, false, false, PlanFeasibility.Blocked, ReasonCategory.NothingToWrite),
    ];

    public static TheoryData<WriteMode, bool, bool, PlanFeasibility, ReasonCategory> Matrix()
    {
        var data = new TheoryData<WriteMode, bool, bool, PlanFeasibility, ReasonCategory>();

        foreach (var (writeMode, gateOpen, controlChannel, feasibility, expected) in Cases)
        {
            data.Add(writeMode, gateOpen, controlChannel, feasibility, expected);
        }

        return data;
    }

    /// <summary>
    /// The matrix is every distinct input exactly once — no combination missing, and no row that hands
    /// <see cref="Applicability"/> a plan it has already been handed.
    /// </summary>
    [Fact]
    public void The_gating_matrix_is_every_distinct_input_exactly_once()
    {
        var everyDistinctInput =
            from writeMode in new[] { WriteMode.ReadOnly, WriteMode.PreviewOnly, WriteMode.Enabled }
            from gateOpen in new[] { true, false }
            from feasibility in new[]
            {
                PlanFeasibility.FullyAchievable, PlanFeasibility.PartiallyAchievable, PlanFeasibility.Blocked,
            }
            // A Blocked plan has no actions at all, so the control-channel flag produces the very same plan.
            from controlChannel in feasibility == PlanFeasibility.Blocked
                ? new[] { false }
                : new[] { true, false }
            select (writeMode, gateOpen, controlChannel, feasibility);

        Cases.Select(c => (c.WriteMode, c.GateOpen, c.ControlChannel, c.Feasibility))
            .Should().BeEquivalentTo(everyDistinctInput);

        Cases.Should().HaveCount(30, because: "24 non-Blocked combinations plus 6 Blocked ones");
        Matrix().Count.Should().Be(Cases.Length);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Applicability_is_exhaustively_correct(
        WriteMode writeMode, bool gateOpen, bool controlChannel, PlanFeasibility feasibility,
        ReasonCategory expected)
    {
        var plan = BuildPlan(feasibility, controlChannel);
        var gate = new ProvisioningGate(gateOpen);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, writeMode, gate);

        var because =
            $"writeMode={writeMode}, gateOpen={gateOpen}, controlChannel={controlChannel}, feasibility={feasibility}";

        reason.Should().NotBeNullOrWhiteSpace();
        Categorize(reason).Should().Be(expected, because);
        canApply.Should().Be(expected == ReasonCategory.Applicable, because);
    }

    /// <summary>
    /// The overlap the single-scenario tests below never reach: a control-channel action AND a write posture
    /// that would refuse on its own. <c>PlanExecutor.ApplyAsync</c> refuses the whole plan for a
    /// control-channel action unconditionally, so telling an operator to raise write access — a thing they
    /// can actually go and do — would send them off to change something that would not help.
    /// </summary>
    [Theory]
    [InlineData(WriteMode.PreviewOnly, true)]
    [InlineData(WriteMode.PreviewOnly, false)]
    [InlineData(WriteMode.ReadOnly, true)]
    [InlineData(WriteMode.ReadOnly, false)]
    public void The_control_channel_refusal_outranks_the_write_mode_refusal(WriteMode writeMode, bool gateOpen)
    {
        var plan = BuildPlan(PlanFeasibility.FullyAchievable, controlChannelAction: true);

        var (canApply, reason) = ChangePlanPresentation.Applicability(plan, writeMode, new ProvisioningGate(gateOpen));

        canApply.Should().BeFalse();
        reason.Should().Contain("control-channel action");
        reason.Should().NotContain("Overview tab", because:
            "raising write access would not make this plan applicable, so naming the control that raises it "
            + "would send an operator to do something that cannot help");
        reason.Should().NotContain(ProvisioningGate.ConfigurationKey);
        reason.Should().NotContain("Preview only");
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
