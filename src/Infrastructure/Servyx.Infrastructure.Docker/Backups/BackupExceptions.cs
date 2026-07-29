namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// Thrown when a definition declares a pre-archive quiesce step that could not be completed, so no
/// archive is written.
/// </summary>
/// <remarks>
/// A backup taken without the flush the definition asked for is not a slightly-worse backup; it is a
/// backup of whatever the server happened to have written to disk last, silently labelled as if it were
/// consistent. Surfacing the failure — and producing no artifact at all — is the only outcome that does
/// not lie to whoever later restores it.
/// </remarks>
public sealed class BackupQuiesceFailedException : Exception
{
    /// <summary>Creates a <see cref="BackupQuiesceFailedException"/> with a default message.</summary>
    public BackupQuiesceFailedException()
        : base("The pre-archive quiesce step failed, so no backup was written.")
    {
    }

    /// <summary>Creates a <see cref="BackupQuiesceFailedException"/> with the given message.</summary>
    public BackupQuiesceFailedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="BackupQuiesceFailedException"/> with the given message and inner exception.</summary>
    public BackupQuiesceFailedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="BackupQuiesceFailedException"/> carrying the command that failed.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="serverId">The server whose quiesce failed.</param>
    /// <param name="commandId">The control command id that failed.</param>
    public BackupQuiesceFailedException(string message, string serverId, string commandId) : base(message)
    {
        ServerId = serverId;
        CommandId = commandId;
    }

    /// <summary>The server whose quiesce failed, if known.</summary>
    public string? ServerId { get; }

    /// <summary>The control command id that failed, if known.</summary>
    public string? CommandId { get; }
}

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
/// Thrown when a restore is asked to apply a plan that is unknown, already applied, expired, or was
/// computed against an artifact that has changed since.
/// </summary>
/// <remarks>
/// Restores are the most destructive thing this provider does, so the plan is single-use and
/// time-bounded, and the archive it was computed from is re-checked before a byte is written. An
/// operator who previewed a restore ten minutes ago and walked away should get a refusal, not a
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
/// <see cref="DockerBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was bypassed by a
/// code change, and failing loudly at that point is the difference between a caught regression and a
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
