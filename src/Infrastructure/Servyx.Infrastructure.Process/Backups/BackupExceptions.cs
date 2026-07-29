namespace Servyx.Infrastructure.Process.Backups;

/// <summary>Thrown when a backup id does not resolve to any artifact currently known for its server.</summary>
public sealed class BackupNotFoundException : Exception
{
    /// <summary>Creates a <see cref="BackupNotFoundException"/> with a default message.</summary>
    public BackupNotFoundException()
        : base("The requested backup does not exist.")
    {
    }

    /// <summary>Creates a <see cref="BackupNotFoundException"/> with the given message.</summary>
    public BackupNotFoundException(string message) : base(message) { }

    /// <summary>Creates a <see cref="BackupNotFoundException"/> with the given message and inner exception.</summary>
    public BackupNotFoundException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="BackupNotFoundException"/> carrying the offending backup id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="backupId">The backup id that did not resolve.</param>
    public BackupNotFoundException(string message, string backupId) : base(message) => BackupId = backupId;

    /// <summary>The backup id that did not resolve, if known.</summary>
    public string? BackupId { get; }
}

/// <summary>
/// Thrown when a restore is asked to apply a plan that is unknown, already applied, expired, or was computed
/// against an artifact that has changed since.
/// </summary>
/// <remarks>
/// Restoring is the most destructive thing this provider does, and locally it is more destructive than
/// anywhere else Servyx does it: the files it overwrites are the live save data of a server running on the
/// same machine as the panel, with no network hop and no container boundary in between. So the plan is
/// single-use and time-bounded, and the archive it was computed from is re-checked before a byte is written.
/// An operator who previewed a restore ten minutes ago and walked away gets a refusal, not a
/// silently-different outcome.
/// </remarks>
public sealed class RestorePlanStaleException : Exception
{
    /// <summary>Creates a <see cref="RestorePlanStaleException"/> with a default message.</summary>
    public RestorePlanStaleException()
        : base("The restore plan is unknown, already applied, or no longer valid.")
    {
    }

    /// <summary>Creates a <see cref="RestorePlanStaleException"/> with the given message.</summary>
    public RestorePlanStaleException(string message) : base(message) { }

    /// <summary>Creates a <see cref="RestorePlanStaleException"/> with the given message and inner exception.</summary>
    public RestorePlanStaleException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="RestorePlanStaleException"/> carrying the offending plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public RestorePlanStaleException(string message, string restorePlanId) : base(message) => RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}

/// <summary>
/// Thrown when something asks this provider to delete a backup it is not entitled to delete — a foreign
/// artifact, or any path outside the Servyx-owned artifact directory.
/// </summary>
/// <remarks>
/// This exception exists to be un-throwable in practice. It is the innermost of the barriers described on
/// <see cref="LocalProcessBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was bypassed by
/// a code change, and failing loudly at that point is the difference between a caught regression and a
/// deleted archive Servyx never created.
/// </remarks>
public sealed class ForeignBackupProtectedException : Exception
{
    /// <summary>Creates a <see cref="ForeignBackupProtectedException"/> with a default message.</summary>
    public ForeignBackupProtectedException()
        : base("Servyx does not delete backups it did not create.")
    {
    }

    /// <summary>Creates a <see cref="ForeignBackupProtectedException"/> with the given message.</summary>
    public ForeignBackupProtectedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ForeignBackupProtectedException"/> with the given message and inner exception.</summary>
    public ForeignBackupProtectedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="ForeignBackupProtectedException"/> carrying the protected artifact's location.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="location">The artifact location that was protected.</param>
    public ForeignBackupProtectedException(string message, string location) : base(message) => Location = location;

    /// <summary>The artifact location that was protected, if known.</summary>
    public string? Location { get; }
}
