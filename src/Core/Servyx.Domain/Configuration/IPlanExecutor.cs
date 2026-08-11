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

    /// <summary>Reverts a previously applied plan using its recorded pre-images.</summary>
    Task RevertAsync(string planId, CancellationToken ct = default);
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
