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

    /// <summary>
    /// Turns a failure thrown out of <c>IPlanExecutor.ApplyAsync</c> into operator-facing copy, and names
    /// which of the engine's distinct failure modes it is so the panel can offer the one follow-up action
    /// that actually helps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every arm is written against what <c>PlanExecutor.ApplyAsync</c> actually does, not against
    /// what the exception's name suggests.</strong> Two of those facts are easy to get wrong in the operator's
    /// favour and are called out here because getting them wrong would print a comforting falsehood on the one
    /// screen in the product that writes to a live game server:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="PlanStaleException"/> is <em>usually</em> raised before any byte is written — expiry, a
    /// changed governing definition, or the pre-flight drift sweep, all of which precede the first write. It is
    /// also raised by the TOCTOU backstop <em>mid-plan</em>, when a transport's own pre-image check catches
    /// drift after earlier actions have already landed; that message states how many did. So this copy says
    /// where the refusal normally happens and points at the detail, rather than promising "nothing was
    /// written" outright.
    /// </description></item>
    /// <item><description>
    /// <see cref="PlanApplyFidelityException"/> is raised from three checks, and they do not agree about
    /// whether anything reached the server. The two pre-flight checks (a stored row whose post-image content
    /// and digest disagree, or content stored with no digest) refuse before writing anything; the receipt and
    /// read-back checks fire after the write already landed. The exception carries both digests, the surface
    /// and the ordinal, but <strong>no flag saying which check failed</strong> — so this copy states the
    /// no-repair, human-decides part unconditionally (true on every arm) and is precise about the one fact it
    /// cannot know, instead of asserting a partial application that may not have happened.
    /// </description></item>
    /// </list>
    /// <para>
    /// An unrecognised exception gets its own arm that names the type and repeats the message verbatim.
    /// Deliberately not folded into one of the arms above and deliberately not smoothed into "something went
    /// wrong": <c>ApplyAsync</c> also throws plain <see cref="InvalidOperationException"/> for real, reachable
    /// situations (a plan the retention sweep already purged, a plan that is no longer <c>Previewed</c>, a
    /// control-channel action, a server no longer tracked), each with its own carefully-worded message. Hiding
    /// those behind a generic sentence would throw away the only accurate account of what happened.
    /// </para>
    /// </remarks>
    /// <param name="failure">The exception <c>ApplyAsync</c> threw.</param>
    /// <returns>The failure's kind and the copy to show for it.</returns>
    public static ApplyFailure Explain(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure switch
        {
            PlanStaleException =>
                new ApplyFailure(ApplyFailureKind.Stale,
                    "The server changed since this plan was built, so Servyx refused it rather than writing "
                    + "bytes against a picture of the server that no longer holds. That refusal normally "
                    + "happens before a single byte is written — the exception to that is drift caught partway "
                    + "through, and the detail below says which happened and how many actions had already "
                    + "landed. Nothing was rolled back either way; there is no revert. Preview again to build "
                    + $"a fresh plan against the server as it is now. Detail: {failure.Message}"),

            WritesDisabledException =>
                new ApplyFailure(ApplyFailureKind.WritesDisabled,
                    "This server's write access is not Enabled, so the write was refused. Servyx checks the "
                    + "posture for the whole plan before it starts and the transport guard re-checks it on "
                    + "every call, so a grant lowered — here or in another Servyx process — between previewing "
                    + "this plan and approving it lands exactly here. Raise write access to Enabled on the "
                    + "Overview tab and preview again; if "
                    + $"{ProvisioningGate.ConfigurationKey} is not enabled on the host, it cannot be raised in "
                    + $"this process at all. Detail: {failure.Message}"),

            ChangePlanConcurrencyException =>
                new ApplyFailure(ApplyFailureKind.Concurrency,
                    "Another session got to this plan first. A plan is applicable exactly once, and this "
                    + "attempt lost that race, so the plan's record now reflects the other session's attempt "
                    + "rather than this one. Find out what that session did before approving anything else "
                    + $"for this server, then preview again to see the server as it is now. Detail: {failure.Message}"),

            PlanApplyFidelityException fidelity => new ApplyFailure(ApplyFailureKind.Fidelity, Fidelity(fidelity)),

            _ => new ApplyFailure(ApplyFailureKind.Unrecognized,
                "Applying failed in a way Servyx has no specific handling for, so it is reported exactly as it "
                + "came back rather than smoothed into a generic message: "
                + $"{failure.GetType().Name}: {failure.Message} — whether anything reached the server is not "
                + "something this panel can state for an unrecognised failure. Check the plan's own record and "
                + "the server itself before trying again."),
        };
    }

    /// <summary>
    /// The partial-application account for a <see cref="PlanApplyFidelityException"/>, carrying both digests
    /// and the surface/ordinal that identify exactly which write is in question.
    /// </summary>
    /// <remarks>
    /// The "not undone, not rewritten, not retried" statement is unconditional because it is true on every arm
    /// of this failure — <c>PlanExecutor</c> has no repair path at all, by design: a second write chasing a bad
    /// first one risks turning one damaged file into two, and <c>RevertAsync</c> throws
    /// <see cref="NotImplementedException"/>. The hedge is only about <em>whether the write already happened</em>,
    /// which this exception genuinely does not carry — see <see cref="Explain"/>'s remarks.
    /// </remarks>
    private static string Fidelity(PlanApplyFidelityException failure)
    {
        var ordinal = failure.Ordinal is { } o ? $"#{o}" : "(ordinal not reported)";
        var surface = failure.SurfaceId ?? "(surface not reported)";
        var approved = failure.ApprovedHash ?? "(approved digest not reported)";
        var observed = failure.ObservedHash ?? "(nothing was read back)";

        return $"Action {ordinal} of this plan, on surface '{surface}', failed its content-fidelity check. "
            + $"Approved digest: {approved}. Observed: {observed}. "
            + "Servyx did not undo it, rewrite it, or retry it, and that is deliberate rather than an "
            + "oversight: a second write chasing a bad first one risks turning one damaged file into two, and "
            + "there is no revert. If the check that failed was one of the post-write checks — the transport's "
            + "own receipt, or reading the surface back off the server — then the write already happened, "
            + "those bytes are on the server now, the plan's remaining actions were skipped, and the plan is "
            + "left partially applied. The detail below says which check failed and states plainly when "
            + "nothing was written. Servyx will not resolve this on its own: a human has to look at the "
            + $"surface on the server and decide what to do. Detail: {failure.Message}";
    }
}

/// <summary>
/// Which of <c>IPlanExecutor.ApplyAsync</c>'s distinct failure modes a caught exception is. Named after what
/// the operator is being told, and kept separate from the message so the panel can offer the one follow-up
/// action that helps for each — a re-preview for staleness, and deliberately nothing automatic for a fidelity
/// failure, where the next step is a human decision.
/// </summary>
public enum ApplyFailureKind
{
    /// <summary>The plan no longer matches the server it was planned against. <see cref="PlanStaleException"/>.</summary>
    Stale,

    /// <summary>The server's write posture refused the write. <c>WritesDisabledException</c>.</summary>
    WritesDisabled,

    /// <summary>Another session claimed this plan first. <c>ChangePlanConcurrencyException</c>.</summary>
    Concurrency,

    /// <summary>A write's content could not be verified against what was approved. <see cref="PlanApplyFidelityException"/>.</summary>
    Fidelity,

    /// <summary>
    /// Anything else, reported verbatim rather than folded into one of the arms above. Reachable in normal
    /// operation — see <see cref="ChangePlanPresentation.Explain"/>'s remarks on <c>ApplyAsync</c>'s plain
    /// <see cref="InvalidOperationException"/> refusals.
    /// </summary>
    Unrecognized,
}

/// <summary>An <c>ApplyAsync</c> failure, classified and phrased for an operator.</summary>
/// <param name="Kind">Which failure mode this is.</param>
/// <param name="Message">The copy to show, including the engine's own message as detail.</param>
public sealed record ApplyFailure(ApplyFailureKind Kind, string Message);
