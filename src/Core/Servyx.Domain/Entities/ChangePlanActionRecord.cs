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

    /// <summary>This action was applied, then reverted from <see cref="PreImageContent"/>.</summary>
    Reverted,
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

    /// <summary>The surface's full content this action will write, rendered once at preview time. Unmasked — see this type's own remarks.</summary>
    public string? PostImageContent { get; set; }

    /// <summary>Content hash of <see cref="PostImageContent"/>, recorded so apply-time drift against the previewed render is detectable.</summary>
    public string? PostImageHash { get; set; }

    /// <summary>Whether <see cref="PreImageContent"/>/<see cref="PostImageContent"/> may contain secret values.</summary>
    public required bool ContainsSecrets { get; set; }

    /// <summary>This action's current lifecycle state.</summary>
    public required ChangePlanActionStatus Status { get; set; }

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
}
