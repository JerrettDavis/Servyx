using Servyx.Domain.Common;

namespace Servyx.Domain.Entities;

/// <summary>Lifecycle state of a persisted <see cref="ChangePlanRecord"/>.</summary>
public enum ChangePlanStatus
{
    /// <summary>Previewed but not yet applied — the plan an operator is currently looking at.</summary>
    Previewed,

    /// <summary>Apply is in progress: at least one action has started, none has necessarily finished.</summary>
    Applying,

    /// <summary>Every action applied successfully.</summary>
    Applied,

    /// <summary>Apply stopped partway through: some actions applied, at least one did not.</summary>
    PartiallyApplied,

    /// <summary>Apply did not complete: no action applied, or the attempt failed before any could.</summary>
    Failed,

    /// <summary>Previewed, never applied, and no longer safe to apply — e.g. its TTL elapsed or a bound surface drifted.</summary>
    Stale,

    /// <summary>Applied, then reverted from its recorded pre-images.</summary>
    Reverted,

    /// <summary>Never applied because a later plan for the same server was previewed and applied instead.</summary>
    Superseded,
}

/// <summary>
/// The durable row backing one <c>ConfigChangePlan</c> preview, from the moment <c>IPlanExecutor.PreviewAsync</c>
/// produces it through to <c>ApplyAsync</c> or <c>RevertAsync</c> acting on it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this table exists.</strong> <c>ConfigChangePlan</c> itself is an in-memory record with no
/// status field and no durability — it is what <c>PreviewAsync</c> returns, not what <c>ApplyAsync</c> reads
/// back. A <c>planId</c> handed to a Blazor Server circuit must still resolve correctly after that circuit's
/// process recycles, after a second browser tab opens the same plan, or after any other gap between preview
/// and the operator's apply click — so the plan has to live in the database, not in a server-side cache tied
/// to one circuit's lifetime.
/// </para>
/// <para>
/// <strong>Persistence-only, this phase.</strong> This entity and its sibling
/// <see cref="ChangePlanActionRecord"/> are storage for <c>IPlanExecutor</c> to be built against later —
/// nothing here implements preview, apply, or revert semantics. <see cref="Status"/> exists so a future
/// executor has somewhere to record its state machine; this phase does not drive it.
/// </para>
/// <para>
/// <strong>No FK-less orphaning, unlike <c>ProvisionedResourceRecord</c>.</strong> That ledger deliberately
/// carries no foreign key to <c>Server</c>/<c>Host</c> because a leaked billable resource must outlive the
/// entity that requested it. A change plan has the opposite lifecycle: it is meaningless without the server
/// it targets, so <see cref="ServerId"/> is a real foreign key with cascade delete — forgetting a server must
/// discard its plans (and, transitively, their actions) rather than leave them orphaned.
/// </para>
/// </remarks>
public sealed class ChangePlanRecord
{
    /// <summary>How long a freshly previewed plan stays applicable before it must be treated as stale.</summary>
    /// <remarks>
    /// Matches the 15-minute restore-plan TTL already used by <c>SshBackupProvider.DefaultRestorePlanTtl</c>,
    /// <c>DockerBackupProvider.DefaultRestorePlanTtl</c>, and <c>LocalProcessBackupProvider.DefaultRestorePlanTtl</c>
    /// — a short-lived plan window is an established Servyx convention, not a new number invented for this
    /// table. Declared here, not computed inline, so a later phase's plan-creation code (which will use the
    /// repo's injected <c>TimeProvider</c> pattern, not <see cref="DateTimeOffset.UtcNow"/>, to compute
    /// <see cref="ExpiresAt"/> — see <c>EfServerSettingsService</c>'s own <c>TimeProvider</c> field for that
    /// pattern) has one constant to reference instead of re-deciding the number.
    /// </remarks>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    /// <summary>This plan's identifier — the <c>planId</c> that must survive across Blazor Server circuits.</summary>
    public required ChangePlanId Id { get; set; }

    /// <summary>The server this plan targets.</summary>
    public required ServerId ServerId { get; set; }

    /// <summary>The plan's current lifecycle state.</summary>
    public required ChangePlanStatus Status { get; set; }

    /// <summary>When this plan was previewed (written).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who requested the preview. Servyx has one shared operator identity; see <see cref="ServerSettingValue.UpdatedBy"/>.</summary>
    public required string CreatedBy { get; set; }

    /// <summary>
    /// When this plan stops being applicable. No default is applied here — the entity does not invent a
    /// clock reading; the value must be supplied by the writer (a future phase, using its injected
    /// <c>TimeProvider</c>). See <see cref="DefaultTtl"/> for the interval that writer is expected to use.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When this plan was applied, if it ever was.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Who applied this plan, if it ever was.</summary>
    public string? AppliedBy { get; set; }

    /// <summary>
    /// When this plan's <c>IPlanExecutor.RevertAsync</c> call was recorded, if it was ever reverted. Plan-level,
    /// not per-action: <c>RevertAsync(planId)</c> takes a single <c>planId</c>, exactly like
    /// <c>ApplyAsync(planId)</c> does — one invocation, one operator, one moment, matching the
    /// <see cref="AppliedAt"/>/<see cref="AppliedBy"/> pair already on this row. Per-action revert timing
    /// (which of a plan's actions individually finished reverting, and when — the same partial-completion
    /// concern <see cref="ChangePlanStatus.PartiallyApplied"/> exists for on apply) is recorded separately on
    /// <see cref="ChangePlanActionRecord.RevertedAt"/>.
    /// </summary>
    public DateTimeOffset? RevertedAt { get; set; }

    /// <summary>
    /// Who invoked <c>RevertAsync</c> for this plan, if it ever was. Recorded here rather than on
    /// <see cref="ChangePlanActionRecord"/>, for the same reason <see cref="AppliedBy"/> is not duplicated onto
    /// every action row: Servyx has one shared operator identity per invocation of a whole-plan operation, not
    /// per action, so "who" is a plan-level fact and "when" (per action) is not — see <see cref="RevertedAt"/>'s
    /// own remarks.
    /// </summary>
    public string? RevertedBy { get; set; }

    /// <summary>
    /// The <c>metadata.id</c> of the game definition that governed this server at preview time. Recorded so a
    /// later apply attempt can detect the definition changing underneath the plan and refuse to apply it.
    /// </summary>
    public required string DefinitionId { get; set; }

    /// <summary>The definition's content hash/version at preview time, for the same drift check as <see cref="DefinitionId"/>.</summary>
    public required string DefinitionVersion { get; set; }

    /// <summary>
    /// The plan's <c>Consequence</c> list, serialized as JSON by the writer. Stored as opaque text — this
    /// table has no opinion about <c>ConsequenceKind</c>'s shape, matching how <c>ServerSettingValue.Value</c>
    /// carries an opinion-free desired value — so a shape change to <c>Consequence</c> does not require a
    /// schema migration here.
    /// </summary>
    public required string ConsequencesJson { get; set; }

    /// <summary>The plan's <c>SurfaceHashes</c> map, serialized as JSON by the writer. Same opaque-text treatment as <see cref="ConsequencesJson"/>.</summary>
    public required string SurfaceHashesJson { get; set; }

    /// <summary>
    /// Changes the preview producer declined to include in the plan (e.g. blocked by write mode or missing
    /// capability), serialized as JSON by the writer. May be an empty JSON array (<c>"[]"</c>) when nothing
    /// was blocked. Same opaque-text treatment as <see cref="ConsequencesJson"/>.
    /// </summary>
    public required string BlockedJson { get; set; }

    /// <summary>
    /// The plan's advisory notes — a malformed definition worked around, or a downstream surface that only
    /// regenerates by hand — serialized as JSON by the writer. May be an empty JSON array (<c>"[]"</c>).
    /// Same opaque-text treatment as <see cref="ConsequencesJson"/>.
    /// </summary>
    /// <remarks>
    /// Persisted rather than left as a return-value-only detail because this table exists precisely so a plan
    /// survives being read back — across a recycled Blazor circuit, or in a second browser tab. A note saying
    /// "the surface this change feeds only regenerates when an operator runs the generator by hand" is the
    /// single piece of a plan whose loss is most dangerous: without it the re-read plan looks unconditionally
    /// applicable, which is the opposite of what it is.
    /// </remarks>
    public required string DiagnosticsJson { get; set; }

    /// <summary>
    /// Optimistic concurrency token. This is the mechanism that makes a double-apply impossible: two
    /// concurrent attempts to transition <see cref="Status"/> on the same row race on this token, and the
    /// second <c>SaveChanges</c> throws <c>DbUpdateConcurrencyException</c> instead of silently applying the
    /// plan twice.
    /// </summary>
    public Guid RowVersion { get; set; }
}
