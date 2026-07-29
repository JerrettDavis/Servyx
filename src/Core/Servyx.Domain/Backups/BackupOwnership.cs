namespace Servyx.Domain.Backups;

/// <summary>
/// Ownership of a backup artifact. This distinction exists to guarantee that Servyx never touches a
/// backup it did not create: <see cref="Foreign"/> artifacts are listed, inspectable, and restorable, but
/// are never pruned, moved, renamed, or counted against Servyx's own retention policy, regardless of how
/// retention is configured.
/// </summary>
public enum BackupOwnership
{
    /// <summary>Created by Servyx and subject to Servyx's retention policy.</summary>
    Servyx,

    /// <summary>Discovered via an <see cref="IBackupAdopter"/>. Read-only from Servyx's perspective, forever.</summary>
    Foreign,
}

/// <summary>A single backup artifact, Servyx-owned or adopted.</summary>
/// <param name="Id">Unique identifier of the artifact.</param>
/// <param name="Ownership">Who created this artifact.</param>
/// <param name="CreatedAt">When the artifact was created.</param>
/// <param name="SizeBytes">Size of the artifact in bytes.</param>
/// <param name="Location">Where the artifact is stored (a path or URI, format defined by the provider).</param>
public sealed record BackupArtifact(string Id, BackupOwnership Ownership, DateTimeOffset CreatedAt, long SizeBytes, string Location);

/// <summary>Retention configuration for Servyx-owned backups.</summary>
/// <param name="KeepHourly">Number of most recent hourly backups to retain.</param>
/// <param name="KeepDaily">Number of most recent daily backups to retain.</param>
/// <param name="KeepWeekly">Number of most recent weekly backups to retain.</param>
public sealed record RetentionPolicy(int KeepHourly, int KeepDaily, int KeepWeekly);

/// <summary>A previewed restore operation.</summary>
/// <param name="Id">Identifier for this restore plan.</param>
/// <param name="BackupId">The backup this plan would restore.</param>
/// <param name="AffectedPaths">Paths that would be overwritten by the restore.</param>
public sealed record RestorePlan(string Id, string BackupId, IReadOnlyList<string> AffectedPaths);

/// <summary>Result of a prune operation.</summary>
/// <param name="Removed">Identifiers of artifacts that were (or, under a dry run, would be) removed.</param>
/// <param name="SkippedForeign">Count of foreign artifacts encountered and left untouched.</param>
public sealed record PruneResult(IReadOnlyList<string> Removed, int SkippedForeign);
