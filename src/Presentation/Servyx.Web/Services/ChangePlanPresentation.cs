using Servyx.Composition;
using Servyx.Domain.Configuration;
using Servyx.Domain.Transport;

namespace Servyx.Web.Services;

/// <summary>
/// Pure, static presentation logic for a previewed <see cref="ConfigChangePlan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately static, with no dependency injection.</strong> Nothing here holds state, opens a
/// session, or has a lifetime to manage — it turns a <see cref="ConfigChangePlan"/> plus a server's write
/// posture into the strings and booleans <see cref="Servyx.Web.Components.Pages.Servers.ChangePlanPanel"/>
/// renders. A DI-registered service would be one more constructor parameter and one more thing a host has to
/// remember to compose for logic that needs neither; it would also make this a citizen of
/// <c>CompositionRootSingleSourceTests</c>' scan for no benefit, since that test exists to catch a SECOND
/// source of a stateful, safety-relevant registration — not a pure function.
/// </para>
/// <para>
/// <strong><see cref="Applicability"/> is the riskiest member here.</strong> It is the single place that
/// decides whether an operator would be shown an "Apply" affordance for a plan that
/// <c>IPlanExecutor.ApplyAsync</c> would refuse outright — see that method's own handling of
/// <see cref="PlanFeasibility.Blocked"/> plans (nothing to apply) and of any
/// <see cref="PlannedActionKind.WriteControlChannel"/> action (the whole plan refused, unconditionally, no
/// matter the write posture). Getting either wrong would let an operator "approve" a plan that can never do
/// what the UI implied.
/// </para>
/// </remarks>
public static class ChangePlanPresentation
{
    /// <summary>
    /// Whether <paramref name="plan"/> could ever be turned into an <c>IPlanExecutor.ApplyAsync</c> call that
    /// writes something, and — when it could not — why not, phrased for an operator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked in a fixed order, because more than one reason can apply at once and only the first one
    /// checked is the one an operator needs to hear:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="PlanFeasibility.Blocked"/> — every requested change was blocked, so
    /// <see cref="ConfigChangePlan.Actions"/> is empty and there is nothing left for a write posture or a
    /// control-channel action to matter to.
    /// </description></item>
    /// <item><description>
    /// Any <see cref="PlannedActionKind.WriteControlChannel"/> action — <c>PlanExecutor.ApplyAsync</c> refuses
    /// the WHOLE plan for this, unconditionally, regardless of write posture: applying only the file half
    /// would leave the server in a state no operator approved.
    /// </description></item>
    /// <item><description>
    /// <paramref name="writeMode"/> not <see cref="WriteMode.Enabled"/> — <c>ApplyAsync</c> refuses via
    /// <c>WritesDisabledException</c> regardless of what the plan contains. The reason names whether
    /// <paramref name="gate"/> even permits raising the tier from the Overview tab, because a reason an
    /// operator cannot act on is a dead end.
    /// </description></item>
    /// </list>
    /// <para>
    /// <paramref name="gate"/> matters only when <paramref name="writeMode"/> is not already
    /// <see cref="WriteMode.Enabled"/>: <c>WriteModeControl</c> itself will not let an operator raise a
    /// server's write posture while <see cref="ProvisioningGate.Enabled"/> is false, so a server stuck below
    /// <see cref="WriteMode.Enabled"/> in a closed-gate process cannot reach applicability from this UI at
    /// all — the reason says so rather than pointing at a tab that cannot help. A server already
    /// <see cref="WriteMode.Enabled"/> is unaffected by the gate: closing it after a grant exists does not
    /// revoke that grant (see <c>ProvisioningGate</c>'s own remarks — it gates NEW infrastructure/grants, not
    /// existing ones).
    /// </para>
    /// </remarks>
    public static (bool CanApply, string Reason) Applicability(
        ConfigChangePlan plan, WriteMode writeMode, ProvisioningGate gate)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gate);

        if (plan.Feasibility == PlanFeasibility.Blocked)
        {
            return (false, "Nothing in this plan can be written, so approving it would do nothing.");
        }

        if (plan.Actions.Any(a => a.Kind == PlannedActionKind.WriteControlChannel))
        {
            return (false,
                "This plan contains a control-channel action, which Servyx cannot yet apply. Applying is "
                + "refused for the whole plan and nothing would be written — applying only the file actions "
                + "would leave the server half-changed in a way the approved diff never described.");
        }

        return writeMode switch
        {
            WriteMode.Enabled => (true, "This plan can be applied."),

            WriteMode.PreviewOnly => (false, gate.Enabled
                ? "Preview only: this plan cannot be applied. Raise write access on the Overview tab."
                : "Preview only: this plan cannot be applied, and write access cannot be raised in this "
                    + $"process at all: {ProvisioningGate.ConfigurationKey} is not enabled. Set it on the "
                    + "host (appsettings.json or the equivalent environment variable) and restart Servyx."),

            _ => (false, gate.Enabled
                ? "This server is read-only, so nothing here can be applied. Raise write access to Enabled "
                    + "on the Overview tab."
                : "This server is read-only, and write access cannot be raised in this process at all: "
                    + $"{ProvisioningGate.ConfigurationKey} is not enabled. Set it on the host "
                    + "(appsettings.json or the equivalent environment variable) and restart Servyx."),
        };
    }

    /// <summary>A short, human label for a <see cref="ConsequenceKind"/>, for use next to its own <see cref="Consequence.Description"/>.</summary>
    public static string Describe(ConsequenceKind kind) => kind switch
    {
        ConsequenceKind.RestartRequired => "Restart required",

        // Deliberately spelled out here too, not just in the ServerSettingsTab lock note: a consequence badge
        // that only said "Recreate required" would still let an operator read "recreate" as something Servyx
        // will do on their behalf, when nothing in this codebase does — see ServerLifecycleService.RecreateAsync.
        ConsequenceKind.RecreateRequired =>
            "Container recreate required — applying writes the bytes, but nothing in Servyx recreates a "
            + "container, so the new value sits on disk until an operator recreates it by hand",

        ConsequenceKind.ServiceInterruption => "Service interruption",

        _ => kind.ToString(),
    };

    /// <summary>
    /// The qualitative half of the feasibility banner's copy for <paramref name="feasibility"/>. Counts (how
    /// many of how many) are the caller's job — this returns only the phrase that does not depend on them, so
    /// a plan with zero requested changes reads the same as one with a hundred.
    /// </summary>
    public static string Summarize(PlanFeasibility feasibility) => feasibility switch
    {
        PlanFeasibility.FullyAchievable => "can be written",
        PlanFeasibility.PartiallyAchievable => "the rest are blocked and applying will not perform them",
        PlanFeasibility.Blocked => "nothing can be written; approving would do nothing",
        _ => feasibility.ToString(),
    };
}
