using Servyx.Composition;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
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
    /// What a <see cref="PostWriteVerification"/> value means, spelled out for an operator reading a history
    /// row rather than abbreviated to the enum member's own name.
    /// </summary>
    /// <remarks>
    /// <see cref="PostWriteVerification.NotAttempted"/> and <see cref="PostWriteVerification.Unverifiable"/>
    /// are given visibly different sentences on purpose: the first means the read-back never ran, the second
    /// means it ran or was wanted and could not be performed. Both amount to "nobody has confirmed this", and
    /// neither may be phrased as if someone had.
    /// </remarks>
    public static string Describe(PostWriteVerification verification) => verification switch
    {
        PostWriteVerification.NotAttempted =>
            "not read back — nothing looked at the surface after the write",

        PostWriteVerification.Verified =>
            "read back, and it held exactly the approved bytes",

        PostWriteVerification.Unverifiable =>
            "could not be read back — the write is believed to have landed, and nobody has looked",

        PostWriteVerification.Mismatched =>
            "read back, and it did NOT hold the approved bytes",

        _ => verification.ToString(),
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
                    + "landed. Nothing was rolled back automatically: reverting an applied plan is a separate "
                    + "step an operator has to take deliberately, and it is refused outright — as a whole, "
                    + "never partially — if any action was recorded as not reversible, if the recorded "
                    + "pre-images have aged out of the retention window, if the plan has already been "
                    + "reverted, or if no write ever reached the server. Preview again to build a fresh plan "
                    + $"against the server as it is now. Detail: {failure.Message}"),

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
    /// first one risks turning one damaged file into two. <c>IPlanExecutor.RevertAsync</c> does exist, but it
    /// is a separate call an operator has to make deliberately, never something the apply path reaches for on
    /// its own, and it refuses a plan whose recorded pre-images the retention sweep has already discarded, one
    /// with a non-reversible action, one already reverted, and one where nothing ever reached the server. The
    /// hedge is only about <em>whether the write already happened</em>, which this exception genuinely does not
    /// carry — see <see cref="Explain"/>'s remarks.
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
            + "oversight: a second write chasing a bad first one risks turning one damaged file into two. "
            + "Reverting this plan is a separate step you would have to take deliberately — it restores the "
            + "exact bytes each surface held before the write, all of them or none, and it is refused if any "
            + "action was recorded as not reversible, if the recorded pre-images have aged out of the "
            + "retention window, if this plan has already been reverted, or if no write ever reached the "
            + "server. "
            + "If the check that failed was one of the post-write checks — the transport's "
            + "own receipt, or reading the surface back off the server — then the write already happened, "
            + "those bytes are on the server now, the plan's remaining actions were skipped, and the plan is "
            + "left partially applied. The detail below says which check failed and states plainly when "
            + "nothing was written. Servyx will not resolve this on its own: a human has to look at the "
            + $"surface on the server and decide what to do. Detail: {failure.Message}";
    }

    // ── History ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What actually happened to a stored plan, phrased for an operator reading a recent-plans list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The apply outcome is derived from the ACTIONS, never from
    /// <see cref="ChangePlanSummary.Status"/> alone,</strong> for the same reason
    /// <c>IChangePlanStore.PurgeImagesAsync</c> makes its retention decision from action state: a plan's
    /// summary status is a rollup, and the situations that matter most here are exactly the ones a rollup
    /// smooths over. A plan whose every write landed but whose read-back found bytes nobody approved is the
    /// case the whole read-back-verification design exists to catch, and it must never render as a clean
    /// "Applied" — so a digest disagreement between <see cref="ChangePlanActionSummary.PostImageHash"/> (what
    /// was approved) and <see cref="ChangePlanActionSummary.ObservedPostImageHash"/> (what was seen) demotes
    /// the whole plan on its own, regardless of how many writes reached the server or what the plan row says.
    /// </para>
    /// <para>
    /// <strong>Status still decides the non-apply outcomes, and is checked first.</strong> Reverted,
    /// partially reverted, revert-failed, previewed, stale and superseded are facts about the plan's lifecycle
    /// its actions cannot express — a reverted plan's actions still record the apply writes that reached the
    /// server, so deriving from them alone would report a plan that has been fully put back as one that
    /// changed the server.
    /// </para>
    /// <para>
    /// <strong><see cref="ChangePlanOutcome.AppliedUnverified"/> is a distinct outcome on purpose.</strong>
    /// <see cref="PostWriteVerification.Unverifiable"/> means the write is believed to have landed and nobody
    /// looked — folding that into <see cref="ChangePlanOutcome.Applied"/> would claim a confirmation nobody
    /// obtained, and folding it into <see cref="ChangePlanOutcome.PartiallyApplied"/> would claim an
    /// incompleteness nobody observed. Neither is true, so it gets its own badge.
    /// </para>
    /// </remarks>
    /// <param name="plan">The stored plan summary, as <c>IChangePlanStore.ListRecentAsync</c> returns it.</param>
    /// <returns>The outcome, its badge label, and the sentence explaining it.</returns>
    public static PlanOutcome Outcome(ChangePlanSummary plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Status switch
        {
            ChangePlanStatus.Reverting => new PlanOutcome(ChangePlanOutcome.Reverting, "Reverting",
                "A revert for this plan is in flight: it was claimed for reverting and at least one restoring "
                + "write has started."),

            ChangePlanStatus.Reverted => new PlanOutcome(ChangePlanOutcome.Reverted, "Reverted",
                "Every surface this plan wrote was put back to the exact bytes recorded before that write. "
                + "That restored the files; it did not restart or recreate the workload."),

            ChangePlanStatus.PartiallyReverted => new PlanOutcome(
                ChangePlanOutcome.PartiallyReverted, "Partially reverted",
                "The revert stopped partway through, or a restored surface did not read back as the recorded "
                + "pre-image. Some surfaces hold the pre-apply bytes and some still hold what this plan "
                + "wrote — resolving that is a human decision."),

            ChangePlanStatus.RevertFailed => new PlanOutcome(ChangePlanOutcome.RevertFailed, "Revert failed",
                "A revert was attempted and nothing was put back, so every change this plan made is still in "
                + "force on the server."),

            ChangePlanStatus.Previewed => new PlanOutcome(ChangePlanOutcome.Previewed, "Previewed",
                "Previewed and not applied. Nothing was written to the server."),

            ChangePlanStatus.Applying => new PlanOutcome(ChangePlanOutcome.Applying, "Applying",
                "An apply for this plan is in flight: it was claimed for applying and at least one write has "
                + "started."),

            ChangePlanStatus.Stale => new PlanOutcome(ChangePlanOutcome.Stale, "Stale",
                "Previewed, never applied, and no longer safe to apply — its preview window elapsed or a "
                + "bound surface drifted. Nothing was written to the server."),

            ChangePlanStatus.Superseded => new PlanOutcome(ChangePlanOutcome.Superseded, "Superseded",
                "Never applied: a later plan for this server was applied instead. Nothing was written to the "
                + "server for this one."),

            _ => ApplyOutcome(plan),
        };
    }

    /// <summary>
    /// The apply outcome for a plan whose status says an apply was attempted
    /// (<see cref="ChangePlanStatus.Applied"/>, <see cref="ChangePlanStatus.PartiallyApplied"/> or
    /// <see cref="ChangePlanStatus.Failed"/>), read off the actions in a fixed order.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing: more than one of these can hold at once and the operator needs the most
    /// alarming true one. A digest disagreement outranks a partial reach because bytes nobody approved sitting
    /// on a live server is worse news than a write that never went out, and both outrank the reached-and-
    /// verified arm that is the only path to a clean <see cref="ChangePlanOutcome.Applied"/>.
    /// </remarks>
    private static PlanOutcome ApplyOutcome(ChangePlanSummary plan)
    {
        var actions = plan.Actions;
        var reached = actions.Count(a => a.WriteReachedServer);

        if (actions.Count == 0 || reached == 0)
        {
            return new PlanOutcome(ChangePlanOutcome.NotApplied, "Not applied",
                "No action of this plan recorded a write that reached the server, so nothing here changed the "
                + "server.");
        }

        var mismatched = actions.Count(DigestMismatch);
        if (mismatched > 0)
        {
            return new PlanOutcome(ChangePlanOutcome.PartiallyApplied, "Partially applied",
                $"{mismatched} of {actions.Count} action{(actions.Count == 1 ? "" : "s")} wrote content whose "
                + "digest does not match the one that was approved, so a live server is holding bytes nobody "
                + "approved — the content was changed in transit or afterwards. Servyx did not rewrite, retry "
                + "or undo it: resolving this is a human decision.");
        }

        if (reached < actions.Count)
        {
            return new PlanOutcome(ChangePlanOutcome.PartiallyApplied, "Partially applied",
                $"{reached} of {actions.Count} actions recorded a write that reached the server; the rest did "
                + "not. This plan changed the server incompletely, and nothing rolled back what did land.");
        }

        if (actions.All(a => a.PostWriteVerification == PostWriteVerification.Verified))
        {
            return new PlanOutcome(ChangePlanOutcome.Applied, "Applied",
                "Every action's write reached the server and read back as exactly the approved bytes. That "
                + "means those bytes are on disk — not that the workload has re-read them.");
        }

        return new PlanOutcome(ChangePlanOutcome.AppliedUnverified, "Applied, not verified",
            "Every action's write reached the server, but at least one of them was never read back "
            + "afterwards — the surface could not be read, or the read-back never ran. The change is believed "
            + "to have landed and nothing has looked.");
    }

    /// <summary>
    /// Whether this action's approved post-image digest and the digest actually observed for it disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing <see cref="ChangePlanActionSummary.ObservedPostImageHash"/> is deliberately NOT a mismatch.
    /// Apply records that column only when something produced a digest — a transport receipt, or a read-back —
    /// and leaves it null when nothing did, which is the <see cref="PostWriteVerification.Unverifiable"/>
    /// story ("nobody looked"), not the <see cref="PostWriteVerification.Mismatched"/> one ("someone looked
    /// and found the wrong bytes"). Treating null as a disagreement would report every unverifiable write as
    /// a mangled one.
    /// </para>
    /// <para>
    /// Compared case-insensitively because both sides are a bare hex SHA-256 and <c>PlanExecutor</c> compares
    /// them the same way — a badge that disagreed with the engine over letter case would flag a correct write
    /// as mangled.
    /// </para>
    /// </remarks>
    /// <param name="action">The action summary to inspect.</param>
    public static bool DigestMismatch(ChangePlanActionSummary action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return !string.IsNullOrEmpty(action.PostImageHash)
            && !string.IsNullOrEmpty(action.ObservedPostImageHash)
            && !string.Equals(
                action.PostImageHash, action.ObservedPostImageHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A digest shortened for display, with the absence of one said in words rather than rendered as a blank.
    /// </summary>
    /// <remarks>
    /// Callers are expected to carry the full value in a <c>title</c> attribute: a truncated digest is enough
    /// to see that two of them differ, never enough to check one against a file.
    /// </remarks>
    /// <param name="digest">The digest, or <see langword="null"/> when none was recorded.</param>
    public static string ShortDigest(string? digest) =>
        string.IsNullOrWhiteSpace(digest)
            ? "(none recorded)"
            : digest.Length <= 12 ? digest : digest[..12] + "…";
}

/// <summary>
/// What happened to a stored change plan, as a recent-plans list has to state it. Named after what an
/// operator is being told, not after the <see cref="ChangePlanStatus"/> or action state that produced it.
/// </summary>
public enum ChangePlanOutcome
{
    /// <summary>Previewed and never applied. Nothing was written.</summary>
    Previewed,

    /// <summary>An apply is in flight.</summary>
    Applying,

    /// <summary>Every action reached the server and read back as exactly the approved bytes.</summary>
    Applied,

    /// <summary>
    /// Every action reached the server, but at least one was never read back — believed landed, unconfirmed.
    /// See <see cref="PostWriteVerification.Unverifiable"/>.
    /// </summary>
    AppliedUnverified,

    /// <summary>
    /// Some actions reached the server and some did not, or an action's observed digest disagrees with the
    /// approved one. Either way the server does not hold, in whole, what was approved.
    /// </summary>
    PartiallyApplied,

    /// <summary>No action's write reached the server, so this plan changed nothing.</summary>
    NotApplied,

    /// <summary>A revert is in flight.</summary>
    Reverting,

    /// <summary>Every surface this plan wrote was put back to its recorded pre-image.</summary>
    Reverted,

    /// <summary>The revert stopped partway, or a restored surface did not read back as the pre-image.</summary>
    PartiallyReverted,

    /// <summary>A revert was attempted and nothing was put back.</summary>
    RevertFailed,

    /// <summary>Previewed, never applied, and no longer safe to apply.</summary>
    Stale,

    /// <summary>Never applied because a later plan for the same server was applied instead.</summary>
    Superseded,
}

/// <summary>One stored plan's outcome, its badge label, and the sentence that explains it.</summary>
/// <param name="Kind">Which outcome this is. Reaches the DOM as a class, so two outcomes can never style alike.</param>
/// <param name="Label">The short badge text.</param>
/// <param name="Detail">The operator-facing account of what that badge means for this plan.</param>
public sealed record PlanOutcome(ChangePlanOutcome Kind, string Label, string Detail);

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
