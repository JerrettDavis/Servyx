using Servyx.Domain.Entities;

namespace Servyx.Domain.Configuration;

/// <summary>
/// The single funnel through which every mutation in the product passes. No other interface applies a
/// configuration write directly; a code path that mutates without a receipt is a bug.
/// </summary>
public interface IPlanExecutor
{
    /// <summary>
    /// Read-only. Produces a unified diff with secrets masked, a reversibility flag per action, the
    /// capabilities required, and any restart/recreate consequences.
    /// </summary>
    Task<ConfigChangePlan> PreviewAsync(string serverId, IReadOnlyDictionary<string, string> desiredValues, CancellationToken ct = default);

    /// <summary>
    /// Applies a previously previewed and approved plan by id. Throws <see cref="PlanStaleException"/> if
    /// any bound surface has drifted since preview, and <c>WritesDisabledException</c> if the server's
    /// write mode does not permit it.
    /// </summary>
    Task<ChangeReceipt> ApplyAsync(string planId, CancellationToken ct = default);

    /// <summary>
    /// Reverts a previously applied plan using its recorded pre-images: every surface whose apply write
    /// reached the server is put back to the literal bytes recorded before that write, or deleted when the
    /// row says the file did not exist beforehand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>All-or-nothing, bought by preflighting rather than by a transaction.</strong> Live-server
    /// writes cannot be rolled back, so every failure-prone check — pre-image availability, the pre-image
    /// agreeing with its own recorded digest, reversibility, surface reachability — runs across the WHOLE
    /// revert set before the first byte is written. Any failure raises
    /// <see cref="PlanRevertException"/> naming the offending action(s) with nothing written at all. A plan
    /// whose images the retention sweep has already discarded is refused here, never partially reverted.
    /// </para>
    /// <para>
    /// <strong>The revert set is keyed on <c>ChangePlanActionRecord.WriteReachedServer</c>, not on
    /// <c>Applied</c>.</strong> An action that errored after its bytes landed still changed the server and
    /// still has to be undone.
    /// </para>
    /// </remarks>
    /// <exception cref="PlanRevertException">
    /// The plan cannot be reverted (raised before anything is written), or a revert write failed partway
    /// through — in which case the exception enumerates, per action, whether its restoring write reached the
    /// server.
    /// </exception>
    Task<RevertReceipt> RevertAsync(string planId, CancellationToken ct = default);
}

/// <summary>
/// Thrown when <see cref="IPlanExecutor.ApplyAsync"/> is called against a plan whose bound surfaces have
/// drifted since preview.
/// </summary>
public sealed class PlanStaleException : Exception
{
    /// <summary>Creates a <see cref="PlanStaleException"/> with a default message.</summary>
    public PlanStaleException()
        : base("The plan's bound surfaces have drifted since it was previewed.")
    {
    }

    /// <summary>Creates a <see cref="PlanStaleException"/> with the given message.</summary>
    public PlanStaleException(string message) : base(message) { }

    /// <summary>Creates a <see cref="PlanStaleException"/> with the given message and inner exception.</summary>
    public PlanStaleException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="PlanStaleException"/> carrying the id of the stale plan.</summary>
    public PlanStaleException(string message, string planId) : base(message)
    {
        PlanId = planId;
    }

    /// <summary>
    /// Creates a <see cref="PlanStaleException"/> carrying the id of the stale plan and the underlying
    /// failure that revealed the staleness.
    /// </summary>
    /// <remarks>
    /// Needed by the apply path's TOCTOU backstop: a <c>TargetDriftException</c> raised by a transport's own
    /// pre-image check is restated as the staleness this contract promises, and both the plan id (so a caller
    /// can act on it) and the original drift (so the expected/actual hashes survive) have to travel with it.
    /// </remarks>
    /// <param name="message">The message.</param>
    /// <param name="planId">The plan that was found to be stale.</param>
    /// <param name="innerException">The failure that revealed the staleness.</param>
    public PlanStaleException(string message, string planId, Exception innerException)
        : base(message, innerException)
    {
        PlanId = planId;
    }

    /// <summary>The id of the plan that was found to be stale, if known.</summary>
    public string? PlanId { get; }
}

/// <summary>
/// Thrown when applying an action produced content that does not match the post-image the operator approved.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three distinct checks raise this, and they are not equally strong.</strong>
/// </para>
/// <list type="bullet">
/// <item><description>
/// The <em>pre-flight</em> check runs BEFORE anything is written and asks whether the stored row agrees with
/// itself — whether <c>ChangePlanActionRecord.PostImageHash</c> really is the digest of
/// <c>ChangePlanActionRecord.PostImageContent</c>, and whether it is present at all. A row that fails it is
/// refused outright and <strong>nothing is written</strong>, which is what makes the two post-write checks
/// below meaningful: without it they would be comparing bytes against a number that was never theirs.
/// </description></item>
/// <item><description>
/// The <em>receipt</em> check compares <c>FileWriteReceipt.PostImageSha256</c> against the approved digest.
/// Every current transport computes that receipt over the bytes it was HANDED, before or independently of
/// placing them — it is not a read-back — so this check attests only that <strong>the transport agrees about
/// the bytes it was given</strong>. It does NOT establish what is on disk. Against the transports that exist
/// today it can only fire for one that miscomputes or misreports its own receipt; it is kept as a cheap guard
/// against a future transport that transforms content, and against a receipt bug.
/// </description></item>
/// <item><description>
/// The <em>read-back</em> check re-reads the surface after the write and hashes what it finds. This one does
/// speak to bytes actually on the server, and it is what catches a transport that reflowed, re-encoded or
/// truncated the content between the stream it accepted and the file it produced.
/// </description></item>
/// </list>
/// <para>
/// For either of the two post-write checks the write already happened, so the action is recorded as failed
/// and the plan is left partially applied — never repaired or rewritten automatically. A second write chasing
/// a bad first one risks turning one damaged file into two.
/// </para>
/// <para>
/// <strong>Both digests survive that failure in their own columns</strong>, not only in the prose of
/// <c>ChangePlanActionRecord.FailureReason</c>: the approved one stays on
/// <c>ChangePlanActionRecord.PostImageHash</c>, which apply never overwrites, and what was found goes to
/// <c>ChangePlanActionRecord.ObservedPostImageHash</c>. The row additionally records
/// <c>ChangePlanActionRecord.WriteReachedServer</c>, and — for the read-back check specifically —
/// <c>PostWriteVerification.Mismatched</c>, so a Failed row on this path can never be mistaken for one where
/// nothing was written or nothing was looked at.
/// </para>
/// </remarks>
public sealed class PlanApplyFidelityException : Exception
{
    /// <summary>Creates a <see cref="PlanApplyFidelityException"/> with a default message.</summary>
    public PlanApplyFidelityException()
        : base("An applied action's content did not match the approved post-image.")
    {
    }

    /// <summary>Creates a <see cref="PlanApplyFidelityException"/> with the given message.</summary>
    public PlanApplyFidelityException(string message) : base(message) { }

    /// <summary>Creates a <see cref="PlanApplyFidelityException"/> with the given message and inner exception.</summary>
    public PlanApplyFidelityException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="PlanApplyFidelityException"/> naming the action and both digests.</summary>
    /// <param name="message">The message.</param>
    /// <param name="planId">The plan being applied.</param>
    /// <param name="ordinal">The action within it.</param>
    /// <param name="surfaceId">The surface that action targets.</param>
    /// <param name="approvedHash">The post-image digest the operator approved.</param>
    /// <param name="observedHash">The digest actually reported or observed.</param>
    public PlanApplyFidelityException(
        string message,
        string planId,
        int ordinal,
        string surfaceId,
        string approvedHash,
        string? observedHash)
        : base(message)
    {
        PlanId = planId;
        Ordinal = ordinal;
        SurfaceId = surfaceId;
        ApprovedHash = approvedHash;
        ObservedHash = observedHash;
    }

    /// <summary>The plan whose apply stopped here, if known.</summary>
    public string? PlanId { get; }

    /// <summary>The ordinal of the offending action, if known.</summary>
    public int? Ordinal { get; }

    /// <summary>The surface the offending action targets, if known.</summary>
    public string? SurfaceId { get; }

    /// <summary>The post-image digest the operator approved, if known.</summary>
    public string? ApprovedHash { get; }

    /// <summary>The digest actually reported by the transport, or observed on the server, if known.</summary>
    public string? ObservedHash { get; }
}

/// <summary>Record of a successfully applied plan.</summary>
/// <param name="PlanId">The plan that was applied.</param>
/// <param name="AppliedAt">When the plan was applied.</param>
/// <param name="Actions">The actions that were applied.</param>
public sealed record ChangeReceipt(string PlanId, DateTimeOffset AppliedAt, IReadOnlyList<PlannedAction> Actions);

/// <summary>What reverting one action did — the per-action honesty a whole-plan outcome cannot carry.</summary>
/// <remarks>
/// <para>
/// <strong><paramref name="WriteReachedServer"/> is the load-bearing field.</strong> A revert is a sequence of
/// unrollbackable writes against a live server, so the only useful thing a partial outcome can say is which
/// files were put back and which were left holding the applied content. It travels on the receipt AND on
/// <see cref="PlanRevertException"/> for that reason: the answer is equally needed whether the revert finished
/// or stopped.
/// </para>
/// <para>
/// <paramref name="Verification"/> is <see langword="null"/> when no revert write was attempted for this
/// action at all — distinct from <see cref="PostWriteVerification.NotAttempted"/>, which means one was
/// attempted and nothing then read the surface back.
/// </para>
/// </remarks>
/// <param name="Ordinal">The action's ordinal within the plan.</param>
/// <param name="SurfaceId">The surface it targets.</param>
/// <param name="WriteReachedServer">Whether this action's restoring write or delete reached the server.</param>
/// <param name="Verification">What reading the surface back after that write found, if it was read at all.</param>
public sealed record RevertedAction(
    int Ordinal,
    string SurfaceId,
    bool WriteReachedServer,
    PostWriteVerification? Verification);

/// <summary>Record of a completed revert.</summary>
/// <remarks>
/// Returned for a revert that reached the end of its action list, which is NOT the same as one where every
/// surface was confirmed restored: a read-back that could not be performed, or that found something other than
/// the pre-image, is reported through <see cref="RevertedAction.Verification"/> and durably through the plan's
/// own <c>ChangePlanStatus</c> rather than by throwing. Callers that need "fully restored" must ask for it —
/// see <see cref="FullyVerified"/>.
/// </remarks>
/// <param name="PlanId">The plan that was reverted.</param>
/// <param name="RevertedAt">When the revert was recorded.</param>
/// <param name="Actions">Every action in the revert set, with what its revert did.</param>
public sealed record RevertReceipt(
    string PlanId,
    DateTimeOffset RevertedAt,
    IReadOnlyList<RevertedAction> Actions)
{
    /// <summary>Whether every action's revert write was read back and found to hold the recorded pre-image.</summary>
    public bool FullyVerified =>
        Actions.Count > 0 && Actions.All(a => a.Verification == PostWriteVerification.Verified);
}

/// <summary>
/// Thrown when a plan cannot be reverted, or when a revert stopped partway through.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two very different forces, told apart by <see cref="AnyWriteReachedServer"/>.</strong> Raised from
/// the pre-flight phase it means the revert was REFUSED and nothing whatsoever was written — a purged
/// pre-image, a pre-image that disagrees with its own recorded digest, an action the plan itself marked
/// non-reversible, an unreachable surface. Raised from the write phase it means some restoring writes landed
/// and one did not, and the server is now in a state that is neither the applied one nor the pre-apply one.
/// </para>
/// <para>
/// <strong><see cref="Actions"/> is not decoration.</strong> A revert cannot be rolled back any more than the
/// apply it was undoing could, so the only actionable thing a failure can hand an operator is the per-action
/// account of which surfaces were put back. Reporting a bare failure would leave them with a server whose
/// state is unknown from the exception alone.
/// </para>
/// </remarks>
public sealed class PlanRevertException : Exception
{
    /// <summary>Creates a <see cref="PlanRevertException"/> with a default message.</summary>
    public PlanRevertException()
        : base("The change plan could not be reverted.")
    {
    }

    /// <summary>Creates a <see cref="PlanRevertException"/> with the given message.</summary>
    public PlanRevertException(string message) : base(message) { }

    /// <summary>Creates a <see cref="PlanRevertException"/> with the given message and inner exception.</summary>
    public PlanRevertException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="PlanRevertException"/> naming the plan and disclosing what each action's revert did.</summary>
    /// <param name="message">The message.</param>
    /// <param name="planId">The plan whose revert stopped or was refused.</param>
    /// <param name="actions">Every action in the revert set, with what its revert did.</param>
    /// <param name="innerException">The failure that stopped the revert, when there was one.</param>
    public PlanRevertException(
        string message,
        string planId,
        IReadOnlyList<RevertedAction> actions,
        Exception? innerException = null)
        : base(message, innerException)
    {
        PlanId = planId;
        Actions = actions ?? [];
    }

    /// <summary>The plan whose revert stopped or was refused, if known.</summary>
    public string? PlanId { get; }

    /// <summary>
    /// Every action in the revert set with what its revert did. Empty for a failure raised before the revert
    /// set could be determined at all (an unknown plan id, a plan whose server is no longer tracked).
    /// </summary>
    public IReadOnlyList<RevertedAction> Actions { get; } = [];

    /// <summary>
    /// Whether ANY restoring write reached the server. <see langword="false"/> is the pre-flight refusal's
    /// guarantee: the plan is exactly as it was and the server was not touched.
    /// </summary>
    public bool AnyWriteReachedServer => Actions.Any(a => a.WriteReachedServer);
}
