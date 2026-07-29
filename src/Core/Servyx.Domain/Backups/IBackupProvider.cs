namespace Servyx.Domain.Backups;

/// <summary>Creates, lists, inspects, restores, and prunes backups for a server.</summary>
public interface IBackupProvider
{
    /// <summary>Creates a new backup of the given server.</summary>
    Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default);

    /// <summary>Lists all backups (Servyx-owned and adopted) known for the given server.</summary>
    Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default);

    /// <summary>Reads an archive's index/manifest without extracting its content.</summary>
    Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default);

    /// <summary>Previews what a restore of the given backup would affect, without performing it.</summary>
    Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default);

    /// <summary>Executes a previously previewed restore plan.</summary>
    Task RestoreAsync(string restorePlanId, CancellationToken ct = default);

    /// <summary>
    /// Applies retention. MUST skip every <see cref="BackupOwnership.Foreign"/> artifact regardless of
    /// the <paramref name="dryRun"/> flag — foreign artifacts are never candidates for pruning, not even
    /// hypothetically.
    /// </summary>
    Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default);
}

/// <summary>
/// Discovers backups created outside Servyx by a workload's own mechanism (e.g. a container's built-in
/// cron job), so they can be surfaced as <see cref="BackupOwnership.Foreign"/> without Servyx ever
/// managing their lifecycle.
/// </summary>
public interface IBackupAdopter
{
    /// <summary>e.g. "palworld-docker-cron".</summary>
    string AdapterId { get; }

    /// <summary>Whether this adopter knows how to discover backups for the given deployment kind.</summary>
    bool Supports(string deploymentKind);

    /// <summary>Read-only discovery; never creates, moves, or deletes anything.</summary>
    Task<IReadOnlyList<BackupArtifact>> DiscoverAsync(string serverId, CancellationToken ct = default);
}
