using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Transport;

namespace Servyx.Domain.Entities;

/// <summary>Lifecycle state of a single persisted <see cref="ChangePlanActionRecord"/>.</summary>
public enum ChangePlanActionStatus
{
    /// <summary>Recorded at preview time; apply has not reached this action yet.</summary>
    Pending,

    /// <summary>This action is currently being applied.</summary>
    Applying,

    /// <summary>This action applied successfully.</summary>
    Applied,

    /// <summary>This action was attempted and failed.</summary>
    Failed,

    /// <summary>This action was never attempted because an earlier action in the same plan failed.</summary>
    Skipped,

    /// <summary>
    /// This action's revert write is in flight — recorded write-ahead, before the transport is called, so a
    /// process that dies mid-revert leaves a row naming the file to go and look at. The mirror of
    /// <see cref="Applying"/>, and needed for the same reason.
    /// </summary>
    Reverting,

    /// <summary>This action was applied, then reverted from <see cref="PreImageContent"/>.</summary>
    Reverted,
}

/// <summary>
/// What happened when an action's write was read back off the server afterwards — confirmed, contradicted,
/// impossible to check, or never looked at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="ChangePlanActionStatus.Applied"/> because they answer different
/// questions.</strong> <see cref="ChangePlanActionStatus.Applied"/> means the write call returned without
/// error. This says whether anyone then went and looked. The two are not the same, and conflating them is
/// how a reflowed, re-encoded or truncated file gets recorded as a clean success.
/// </para>
/// <para>
/// <strong>Why a write receipt is not enough on its own.</strong> Every current transport computes
/// <c>FileWriteReceipt.PostImageSha256</c> over the bytes it was HANDED, before or independently of placing
/// them — it is not a read-back. So the receipt attests that the transport agrees about its input, and
/// nothing more. Only <see cref="Verified"/> reflects bytes actually observed on the server.
/// </para>
/// </remarks>
public enum PostWriteVerification
{
    /// <summary>
    /// No read-back was performed. The state of every action that has not been applied, and of an applied
    /// action whose verification never ran.
    /// </summary>
    NotAttempted,

    /// <summary>The surface was read back after the write and its content hashed to the approved post-image digest.</summary>
    Verified,

    /// <summary>
    /// The write completed but could NOT be confirmed — the session does not advertise
    /// <c>TransportCapabilities.FileRead</c> for this surface, or the read-back itself failed. The change is
    /// believed to have landed; nobody has looked. Deliberately a distinct value rather than folding into
    /// <see cref="NotAttempted"/>, so "we could not check" is visible to an operator instead of being
    /// indistinguishable from "we did not get that far".
    /// </summary>
    Unverifiable,

    /// <summary>
    /// The surface WAS read back after the write and its content did NOT hash to the approved post-image
    /// digest: a live server is holding bytes nobody approved. The observed digest is recorded on
    /// <see cref="ChangePlanActionRecord.ObservedPostImageHash"/> and the approved one stays on
    /// <see cref="ChangePlanActionRecord.PostImageHash"/>, so the two can be compared long after the images
    /// themselves are purged.
    /// </summary>
    /// <remarks>
    /// The one member that can accompany <see cref="ChangePlanActionStatus.Failed"/> and still mean the server
    /// changed. It exists because the alternative — leaving the row at <see cref="NotAttempted"/>, whose
    /// documented meaning is that nobody looked — states the exact opposite of what happened on the single
    /// highest-stakes path in the apply engine. No auto-repair follows it: a mismatch is a human's decision.
    /// </remarks>
    Mismatched,
}

/// <summary>
/// One persisted, ordered action within a <see cref="ChangePlanRecord"/> — the durable counterpart of
/// <c>PlannedAction</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both pre- and post-image, not a diff.</strong> <see cref="PreImageContent"/> and
/// <see cref="PostImageContent"/> are each recorded in full at preview time, alongside the already-rendered
/// <see cref="UnifiedDiff"/> a human reviews. This is deliberate, not redundant: rendering the post-image once
/// at preview time and then, at apply time, writing exactly the bytes that were previewed (rather than
/// re-rendering from the desired values a second time) is what makes apply deterministic — nothing can have
/// changed between "what the operator approved" and "what got written". The same recorded
/// <see cref="PreImageContent"/> is what makes <c>IPlanExecutor.RevertAsync</c> exact: reverting restores the
/// literal pre-image, not an inferred inverse of a diff, which a unified diff alone cannot guarantee for every
/// surface shape.
/// </para>
/// <para>
/// <strong><see cref="UnifiedDiff"/> arrives already masked.</strong> The producer that builds
/// <c>PlannedAction.UnifiedDiff</c> masks secret values before this row is ever written; this table stores
/// exactly what it is given and performs no masking of its own. <see cref="ContainsSecrets"/> is recorded
/// alongside so a future read path can decide whether <see cref="PreImageContent"/>/<see cref="PostImageContent"/>
/// — which are NOT masked, because an exact revert needs the real bytes — are safe to surface to a caller.
/// </para>
/// </remarks>
public sealed class ChangePlanActionRecord
{
    /// <summary>This action row's own identifier.</summary>
    public required Guid Id { get; set; }

    /// <summary>The plan this action belongs to.</summary>
    public required ChangePlanId ChangePlanId { get; set; }

    /// <summary>Execution order within the plan, zero-based. Apply must process actions in this order.</summary>
    public required int Ordinal { get; set; }

    /// <summary>What kind of action this is — matches <see cref="PlannedActionKind"/>.</summary>
    public required PlannedActionKind Kind { get; set; }

    /// <summary>
    /// The surface this action targets. <strong>This is the routing key</strong> — see
    /// <see cref="ResolvedPath"/>'s remarks.
    /// </summary>
    public required string SurfaceId { get; set; }

    /// <summary>The concrete path/location the bound surface resolved to at preview time.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Not directly actionable, and carries no session identifier.</strong> This value is relative to
    /// the root of whichever session the surface resolved on, and a server routinely has more than one — a
    /// <c>kind: docker</c> deployment reaches <c>${DATA_DIR}</c> inside the container and
    /// <c>${COMPOSE_DIR}</c> on the host, through two different sessions with two different roots. Nothing in
    /// this row says which. An apply cannot avoid re-resolving surfaces anyway, because an
    /// <c>IExecutionTarget</c> is a live connection and cannot be persisted, so the correct sequence is:
    /// re-resolve by <see cref="SurfaceId"/>, then use the session and path that resolution produces.
    /// </para>
    /// <para>
    /// What this column is for is the cross-check on that re-resolution. If the freshly resolved path differs
    /// from the one recorded here, the deployment moved underneath the plan (a reconfigured compose
    /// directory, a re-adopted container with a different mount) and the plan must be treated as stale rather
    /// than applied to a path the operator never approved.
    /// </para>
    /// </remarks>
    public required string ResolvedPath { get; set; }

    /// <summary>The transport capabilities required to apply this action.</summary>
    public required TransportCapabilities RequiredCapabilities { get; set; }

    /// <summary>A unified diff of the change, already masked by the producer — see this type's own remarks.</summary>
    public required string UnifiedDiff { get; set; }

    /// <summary>Whether this action can be reverted from <see cref="PreImageContent"/>.</summary>
    public required bool Reversible { get; set; }

    /// <summary>Content hash of the surface before this action, at preview time. Null when there was no prior content (e.g. a new file).</summary>
    public string? PreImageHash { get; set; }

    /// <summary>The surface's full content before this action, at preview time. Unmasked — see this type's own remarks. Null when there was no prior content.</summary>
    public string? PreImageContent { get; set; }

    /// <summary>
    /// Whether the surface this action targets EXISTED before the action was applied — the discriminator that
    /// makes a null <see cref="PreImageContent"/> unambiguous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this column a null <see cref="PreImageContent"/> means two opposite things.</strong> It
    /// is what a file that did not exist before the write looks like (whose revert is a DELETE), and it is
    /// equally what a purged pre-image looks like after <c>IChangePlanStore.PurgeImagesAsync</c> has swept the
    /// row (whose revert must be REFUSED, because the bytes to restore are gone). Guessing between them is not
    /// an option in either direction: guessing "purged" makes every file-creating plan permanently
    /// non-revertible, and guessing "did not exist" deletes a real configuration file off a live server on the
    /// strength of a column the retention sweep nulled.
    /// </para>
    /// <para>
    /// <strong><see langword="true"/> for every row written before this column existed</strong> — the
    /// migration backfills it that way on purpose. A legacy row therefore refuses its revert (no content, and
    /// the row claims a file was there) instead of performing a delete nobody can justify from the data.
    /// </para>
    /// </remarks>
    public bool PreImageExisted { get; set; } = true;

    /// <summary>The surface's full content this action will write, rendered once at preview time. Unmasked — see this type's own remarks.</summary>
    public string? PostImageContent { get; set; }

    /// <summary>
    /// Content hash of <see cref="PostImageContent"/> — the digest the operator approved. Written once, at
    /// preview time, and never overwritten afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This column is an invariant, not a scratch pad.</strong> While <see cref="PostImageContent"/>
    /// is present this must equal the hash of it; <c>IPlanExecutor.ApplyAsync</c> re-checks exactly that
    /// before it writes anything and refuses a row where the two disagree. Apply therefore never assigns to
    /// this property — whatever it observes on the server goes to
    /// <see cref="ObservedPostImageHash"/> instead. Overwriting this one would break the pre-flight check that
    /// depends on it, and would destroy the only surviving record of what was approved once the retention
    /// sweep nulls <see cref="PostImageContent"/>.
    /// </para>
    /// </remarks>
    public string? PostImageHash { get; set; }

    /// <summary>
    /// The post-image digest apply actually saw for this action, or <see langword="null"/> when apply never
    /// got far enough to see one. Written only by <c>IPlanExecutor.ApplyAsync</c>, never at preview time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The counterpart to <see cref="PostImageHash"/>, never a replacement for it.</strong> That one
    /// says what was approved; this one says what was found. Keeping them in two columns is what lets an
    /// operator — or a query, rather than a human reading
    /// <see cref="FailureReason"/> prose — compare the two after the images are gone.
    /// </para>
    /// <para>
    /// <strong>Where the value came from is stated by <see cref="PostWriteVerification"/></strong>, which apply
    /// writes on the same paths:
    /// <see cref="Entities.PostWriteVerification.Verified"/> and
    /// <see cref="Entities.PostWriteVerification.Mismatched"/> mean this is the digest of bytes read back off
    /// the server; <see cref="Entities.PostWriteVerification.NotAttempted"/> on a
    /// <see cref="ChangePlanActionStatus.Failed"/> row means no read-back happened and this is the transport's
    /// own write receipt, recorded because that receipt disagreed with <see cref="PostImageHash"/>;
    /// <see cref="Entities.PostWriteVerification.Unverifiable"/> means nothing was observed at all and this
    /// stays <see langword="null"/>.
    /// </para>
    /// </remarks>
    public string? ObservedPostImageHash { get; set; }

    /// <summary>
    /// Whether a write for this action reached the server — set the moment the transport's write call returns
    /// a receipt, BEFORE any verification is attempted, and never cleared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not the same question as <see cref="Status"/>, and the retention sweep depends on the
    /// difference.</strong> A read-back fidelity mismatch leaves this action
    /// <see cref="ChangePlanActionStatus.Failed"/> and every later one
    /// <see cref="ChangePlanActionStatus.Skipped"/>, so no action in the plan says
    /// <see cref="ChangePlanActionStatus.Applied"/> — yet a write did land, wrongly, and
    /// <see cref="PreImageContent"/> is the only way back. This flag is how that fact survives on the row
    /// itself instead of only in the plan's summary status, and it is what
    /// <c>IChangePlanStore.PurgeImagesAsync</c> consults before discarding images.
    /// </para>
    /// <para>
    /// Deliberately true for a write that landed and then failed verification: the server was touched either
    /// way. It stays false for refusals that happen before any I/O — a revoked write grant, a drift the
    /// transport detects during its own pre-image check — because nothing was sent on those paths.
    /// </para>
    /// </remarks>
    public bool WriteReachedServer { get; set; }

    /// <summary>Whether <see cref="PreImageContent"/>/<see cref="PostImageContent"/> may contain secret values.</summary>
    public required bool ContainsSecrets { get; set; }

    /// <summary>This action's current lifecycle state.</summary>
    public required ChangePlanActionStatus Status { get; set; }

    /// <summary>
    /// What reading this action's surface back after the write found — see
    /// <see cref="Entities.PostWriteVerification"/> for why this is not implied by
    /// <see cref="ChangePlanActionStatus.Applied"/>, and why
    /// <see cref="Entities.PostWriteVerification.Mismatched"/> can sit on a
    /// <see cref="ChangePlanActionStatus.Failed"/> row.
    /// </summary>
    /// <remarks>
    /// Defaulted rather than <c>required</c> so every existing construction site — the previewer, and every
    /// test that builds an action row — keeps compiling and keeps meaning what it meant before: nobody has
    /// read anything back yet.
    /// </remarks>
    public PostWriteVerification PostWriteVerification { get; set; } = PostWriteVerification.NotAttempted;

    /// <summary>When this action was applied, if it ever was.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>
    /// When this specific action finished reverting, if it ever was — mirrors <see cref="AppliedAt"/>'s own
    /// per-action granularity, needed because a revert sweep processes a plan's actions one at a time and can
    /// stop partway through (an action may not be <see cref="Reversible"/>, or an individual revert write may
    /// fail while others already succeeded). No action-level "who": attribution for the whole revert operation
    /// lives once on <c>ChangePlanRecord.RevertedBy</c>, the same split <see cref="AppliedAt"/> already has
    /// with <c>ChangePlanRecord.AppliedBy</c> — see that property's own remarks.
    /// </summary>
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>Why this action failed, if <see cref="Status"/> is <see cref="ChangePlanActionStatus.Failed"/>.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Whether a REVERT write for this action reached the server — the revert-phase counterpart of
    /// <see cref="WriteReachedServer"/>, set the moment the restoring write or delete returns and never
    /// cleared.
    /// </summary>
    /// <remarks>
    /// A separate column rather than a reuse of <see cref="WriteReachedServer"/>, because the two answer
    /// questions about opposite operations and a revert must never be able to erase the record that an apply
    /// touched the server. It is this column that lets a partial revert be reported honestly: an operator
    /// reading a failed revert needs to know which files were put back and which were left holding the applied
    /// content.
    /// </remarks>
    public bool RevertWriteReachedServer { get; set; }

    /// <summary>
    /// The digest reverting this action actually found on the server when it read the surface back, or
    /// <see langword="null"/> when nothing was read (or the revert was a delete, which has no content to hash).
    /// </summary>
    /// <remarks>
    /// The revert-phase counterpart of <see cref="ObservedPostImageHash"/>, and separate from
    /// <see cref="PreImageHash"/> for exactly the reason that one is separate from
    /// <see cref="PostImageHash"/>: <see cref="PreImageHash"/> is what SHOULD be there once the revert lands
    /// and stays the row's statement of what was restored from; this is what WAS there. Overwriting the
    /// expectation with the observation would destroy the only pair a mismatch can be diagnosed from.
    /// </remarks>
    public string? RevertObservedImageHash { get; set; }

    /// <summary>
    /// What reading this action's surface back after its REVERT write found, or <see langword="null"/> when no
    /// revert was ever attempted for this action.
    /// </summary>
    /// <remarks>
    /// Deliberately the same <see cref="Entities.PostWriteVerification"/> enum the apply path uses rather than
    /// a parallel revert-only one: the four states a read-back can land in — never attempted, confirmed,
    /// impossible to check, contradicted — do not change because the bytes being written happen to be a
    /// pre-image. Nullable so "no revert has been attempted" is a distinct answer from
    /// <see cref="Entities.PostWriteVerification.NotAttempted"/>, which on this column would mean a revert ran
    /// and got as far as writing without ever looking.
    /// </remarks>
    public PostWriteVerification? RevertVerification { get; set; }

    /// <summary>Why reverting this action failed, if it was attempted and did not succeed.</summary>
    /// <remarks>
    /// Kept apart from <see cref="FailureReason"/>, which belongs to the apply attempt. A revert that fails on
    /// an action whose apply also failed would otherwise overwrite the account of the original failure with
    /// the account of the failed recovery, and an operator needs both.
    /// </remarks>
    public string? RevertFailureReason { get; set; }
}
