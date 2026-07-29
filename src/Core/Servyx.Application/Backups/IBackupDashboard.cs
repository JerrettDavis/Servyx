using Servyx.Domain.Backups;

namespace Servyx.Application.Backups;

/// <summary>
/// The Application-layer surface a user interface drives backups through: list, create, inspect, preview a
/// restore, apply a restore, and apply retention.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than injecting <see cref="IBackupProvider"/> into a page.</strong> Three
/// of the six operations on the provider are irreversible, and two of them come in preview/apply pairs
/// whose ordering is the safety property. Putting the pairing here — rather than in whichever component
/// happens to render a button — means a second UI, a background service, or a future API cannot obtain the
/// destructive half without the preview half, because there is no method on this interface that skips it.
/// </para>
/// <para>
/// <strong>Foreign artifacts are structurally unprunable at this layer too.</strong> There is no member
/// here that takes a <see cref="BackupArtifact"/> and deletes it: pruning is expressed only as "apply this
/// retention policy to this server", and both prune members re-audit the provider's answer against the
/// listing's foreign half before anything is deleted. See
/// <see cref="BackupPruneResult.RefusedForeign"/>.
/// </para>
/// <para>
/// <strong>The absent-provider case is loud, not silent.</strong> When the composition root registered no
/// <see cref="IBackupProvider"/>, <see cref="ProviderConfigured"/> is <see langword="false"/> and every
/// operation throws <see cref="InvalidOperationException"/> rather than returning a failure result. A
/// missing registration is a composition defect, not an outcome of the attempt, and rendering it beside
/// genuine provider failures would imply something was tried. This mirrors
/// <c>ProvisioningDashboardService.ApplyAsync</c>'s treatment of a missing executor.
/// </para>
/// </remarks>
public interface IBackupDashboard
{
    /// <summary>
    /// Whether an <see cref="IBackupProvider"/> is registered in this process. When
    /// <see langword="false"/> nothing here can be called and the UI must render an explanation rather
    /// than a control.
    /// </summary>
    bool ProviderConfigured { get; }

    /// <summary>Lists every artifact known for a server, partitioned by ownership.</summary>
    /// <param name="serverId">The server to list.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BackupListResult> ListAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new backup. May quiesce the server first when the definition declares a quiesce step —
    /// see <see cref="IBackupProvider.CreateAsync"/>. A failed quiesce produces no artifact at all.
    /// </summary>
    /// <param name="serverId">The server to back up.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BackupCreateResult> CreateAsync(string serverId, CancellationToken ct = default);

    /// <summary>Reads an archive's index without extracting it. Writes nothing.</summary>
    /// <param name="backupId">The artifact to inspect. May be Servyx-owned or foreign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BackupInspectResult> InspectAsync(string backupId, CancellationToken ct = default);

    /// <summary>
    /// Previews what restoring an artifact would overwrite. <strong>Writes nothing.</strong> The returned
    /// plan id is the only thing <see cref="ApplyRestoreAsync"/> accepts.
    /// </summary>
    /// <param name="backupId">The artifact to preview a restore of. May be Servyx-owned or foreign.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RestorePlanResult> PlanRestoreAsync(string backupId, CancellationToken ct = default);

    /// <summary>
    /// Applies a previously previewed restore. <strong>This overwrites live save data and there is no
    /// undo.</strong> It accepts only a plan id produced by <see cref="PlanRestoreAsync"/>; there is no
    /// overload that takes a backup id, so a restore that was never previewed cannot be applied.
    /// </summary>
    /// <param name="restorePlanId">The plan id from <see cref="PlanRestoreAsync"/>.</param>
    /// <param name="expectedPathCount">
    /// How many affected paths the caller was shown. Re-checked against the plan the caller claims to have
    /// approved so a UI cannot confirm one preview and apply another; pass the count from the
    /// <see cref="RestorePlanResult.Planned"/> the operator actually saw.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<RestoreApplyResult> ApplyRestoreAsync(string restorePlanId, int expectedPathCount, CancellationToken ct = default);

    /// <summary>
    /// Reports what retention would remove, deleting nothing. Foreign artifacts are never candidates.
    /// </summary>
    /// <param name="serverId">The server whose artifacts are evaluated.</param>
    /// <param name="policy">The retention policy to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BackupPruneResult> PreviewPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Applies retention, deleting the Servyx-owned artifacts it selects. Runs
    /// <see cref="PreviewPruneAsync"/>'s dry run first and refuses outright if a foreign artifact appears
    /// among the candidates, so the audit happens <em>before</em> anything is deleted rather than after.
    /// </summary>
    /// <param name="serverId">The server whose artifacts are evaluated.</param>
    /// <param name="policy">The retention policy to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BackupPruneResult> ApplyPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default);
}
