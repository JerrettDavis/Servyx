namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>Thrown when a backup id does not resolve to any snapshot DigitalOcean currently reports.</summary>
/// <remarks>
/// A snapshot can vanish provider-side between two Servyx calls — deleted in the console, deleted by another
/// tool, or removed with the account. Resolution therefore always goes back through a fresh listing and
/// matches on the whole id, so an id naming a snapshot that is no longer there fails as "not found" rather
/// than being trusted as something to act on.
/// </remarks>
public sealed class SnapshotNotFoundException : Exception
{
    /// <summary>Creates a <see cref="SnapshotNotFoundException"/> with a default message.</summary>
    public SnapshotNotFoundException()
        : base("The requested DigitalOcean snapshot does not exist.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotNotFoundException"/> with the given message.</summary>
    public SnapshotNotFoundException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotNotFoundException"/> with the given message and inner exception.</summary>
    public SnapshotNotFoundException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotNotFoundException"/> carrying the offending backup id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="backupId">The backup id that did not resolve.</param>
    public SnapshotNotFoundException(string message, string backupId) : base(message) => BackupId = backupId;

    /// <summary>The backup id that did not resolve, if known.</summary>
    public string? BackupId { get; }
}

/// <summary>
/// Thrown when something asks this provider to delete a snapshot it is not entitled to delete — one Servyx
/// did not create, or one taken from a different droplet.
/// </summary>
/// <remarks>
/// This exception exists to be un-throwable in practice. It is the innermost of the barriers described on
/// <see cref="DigitalOceanSnapshotBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was
/// bypassed by a code change, and failing loudly at that point is the difference between a caught regression
/// and an irreversibly deleted snapshot that Servyx never created and may have been somebody's only copy.
/// </remarks>
public sealed class ForeignSnapshotProtectedException : Exception
{
    /// <summary>Creates a <see cref="ForeignSnapshotProtectedException"/> with a default message.</summary>
    public ForeignSnapshotProtectedException()
        : base("Servyx does not delete DigitalOcean snapshots it did not create.")
    {
    }

    /// <summary>Creates a <see cref="ForeignSnapshotProtectedException"/> with the given message.</summary>
    public ForeignSnapshotProtectedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ForeignSnapshotProtectedException"/> with the given message and inner exception.</summary>
    public ForeignSnapshotProtectedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="ForeignSnapshotProtectedException"/> carrying the protected snapshot.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="location">The snapshot location that was protected.</param>
    public ForeignSnapshotProtectedException(string message, string location) : base(message) => Location = location;

    /// <summary>The snapshot location that was protected, if known.</summary>
    public string? Location { get; }
}

/// <summary>
/// Thrown when a restore is asked to apply a plan that is unknown, already applied, expired, or was computed
/// against a snapshot that has changed or vanished since.
/// </summary>
/// <remarks>
/// Restoring a droplet from a snapshot erases the droplet's current disk, so the plan is single-use and
/// time-bounded, and the snapshot it was computed from is re-read before the action is submitted. An operator
/// who previewed a restore ten minutes ago and walked away gets a refusal, not a silently-different outcome.
/// </remarks>
public sealed class SnapshotRestorePlanStaleException : Exception
{
    /// <summary>Creates a <see cref="SnapshotRestorePlanStaleException"/> with a default message.</summary>
    public SnapshotRestorePlanStaleException()
        : base("The restore plan is unknown, already applied, or no longer valid.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotRestorePlanStaleException"/> with the given message.</summary>
    public SnapshotRestorePlanStaleException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotRestorePlanStaleException"/> with the given message and inner exception.</summary>
    public SnapshotRestorePlanStaleException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotRestorePlanStaleException"/> carrying the offending plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public SnapshotRestorePlanStaleException(string message, string restorePlanId) : base(message) =>
        RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}

/// <summary>
/// Thrown when a restore was asked for without the separate acknowledgement that restoring a droplet from a
/// snapshot destroys the droplet's current disk.
/// </summary>
/// <remarks>
/// <para>
/// This is what <see cref="DigitalOceanSnapshotBackupProvider.RestoreAsync(string, System.Threading.CancellationToken)"/> —
/// the <c>IBackupProvider</c> member — always does. That signature takes a plan id and nothing else, so it
/// cannot carry evidence that anybody accepted the data loss, and a disk-erasing operation is not something
/// this adapter performs on an argument list that cannot express consent. The acknowledging overload
/// is the only path to a provider call.
/// </para>
/// <para>
/// A refusal issues no HTTP request of any kind, and does <em>not</em> consume the restore plan: the operator
/// who now supplies the acknowledgement can use the plan they already previewed rather than being sent back
/// to preview a second one.
/// </para>
/// </remarks>
public sealed class SnapshotRestoreNotAcknowledgedException : Exception
{
    /// <summary>Creates a <see cref="SnapshotRestoreNotAcknowledgedException"/> with a default message.</summary>
    public SnapshotRestoreNotAcknowledgedException()
        : base("Restoring a droplet from a snapshot destroys the droplet's current disk and was not acknowledged.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotRestoreNotAcknowledgedException"/> with the given message.</summary>
    public SnapshotRestoreNotAcknowledgedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotRestoreNotAcknowledgedException"/> with the given message and inner exception.</summary>
    public SnapshotRestoreNotAcknowledgedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotRestoreNotAcknowledgedException"/> carrying the refused plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public SnapshotRestoreNotAcknowledgedException(string message, string restorePlanId) : base(message) =>
        RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}

/// <summary>
/// Thrown when a snapshot or restore action was accepted by DigitalOcean but was never observed reaching a
/// terminal state — so Servyx does not know whether it worked.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from a failure, and the distinction is the point. An <em>errored</em> action is over
/// and may be retried. An action still running is not over, and "retrying" it submits the same mutation a
/// second time: a second snapshot that bills alongside the first, or a second restore that overwrites the
/// disk again including anything the first one had already put back.
/// </para>
/// <para>
/// <see cref="Submitted"/> distinguishes the two situations this type covers: <see langword="true"/> means
/// DigitalOcean accepted the operation and it may yet complete at the provider; the operation is not a
/// no-op and must not be resubmitted blindly.
/// </para>
/// </remarks>
public sealed class SnapshotActionNotConfirmedException : Exception
{
    /// <summary>Creates a <see cref="SnapshotActionNotConfirmedException"/> with a default message.</summary>
    public SnapshotActionNotConfirmedException()
        : base("DigitalOcean accepted the action but never reported it finished, so its outcome is unknown.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotActionNotConfirmedException"/> with the given message.</summary>
    public SnapshotActionNotConfirmedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotActionNotConfirmedException"/> with the given message and inner exception.</summary>
    public SnapshotActionNotConfirmedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotActionNotConfirmedException"/> carrying the action that was watched.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="actionId">The DigitalOcean action id, so an operator can look it up at the provider.</param>
    /// <param name="submitted">Whether DigitalOcean accepted the operation and it may still be running.</param>
    public SnapshotActionNotConfirmedException(string message, long actionId, bool submitted) : base(message)
    {
        ActionId = actionId;
        Submitted = submitted;
    }

    /// <summary>The DigitalOcean action that was watched, if known.</summary>
    public long? ActionId { get; }

    /// <summary>Whether the operation was accepted by DigitalOcean and may still be running there.</summary>
    public bool Submitted { get; }
}

/// <summary>
/// Thrown when DigitalOcean reported a snapshot or restore action as <c>errored</c>, or completed it without
/// producing the snapshot it was supposed to produce.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="SnapshotActionNotConfirmedException"/> and deliberately a different type:
/// this one means the operation is <em>over</em> and did not work, which is the case where retrying is
/// reasonable. Collapsing the two into one failure answer is the mistake these signatures are shaped to
/// prevent.
/// </remarks>
public sealed class SnapshotActionFailedException : Exception
{
    /// <summary>Creates a <see cref="SnapshotActionFailedException"/> with a default message.</summary>
    public SnapshotActionFailedException()
        : base("DigitalOcean reported the snapshot action as errored.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotActionFailedException"/> with the given message.</summary>
    public SnapshotActionFailedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotActionFailedException"/> with the given message and inner exception.</summary>
    public SnapshotActionFailedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotActionFailedException"/> carrying the action that failed.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="actionId">The DigitalOcean action id, so an operator can look it up at the provider.</param>
    public SnapshotActionFailedException(string message, long actionId) : base(message) => ActionId = actionId;

    /// <summary>The DigitalOcean action that failed, if known.</summary>
    public long? ActionId { get; }
}

/// <summary>
/// Thrown when a snapshot was taken successfully but could not be marked as Servyx-owned.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot exists and is billing. Servyx cannot recognise it as its own, so it will be listed as
/// <see cref="Domain.Backups.BackupOwnership.Foreign"/> forever and <strong>will never be pruned by
/// retention</strong>. That is the safe direction — Servyx does not delete what it cannot prove it owns —
/// but it is also a charge that will not stop on its own, so it is raised as an error naming the snapshot id
/// and its monthly cost rather than returned as a successful backup.
/// </para>
/// <para>
/// Reporting this as success would be the worse lie in both directions: the caller would believe a
/// retention-managed backup exists when the artifact is outside retention, and nobody would learn about the
/// recurring charge.
/// </para>
/// </remarks>
public sealed class SnapshotOwnershipNotRecordedException : Exception
{
    /// <summary>Creates a <see cref="SnapshotOwnershipNotRecordedException"/> with a default message.</summary>
    public SnapshotOwnershipNotRecordedException()
        : base("The snapshot was taken but could not be marked as Servyx-owned, so it is billing and unmanaged.")
    {
    }

    /// <summary>Creates a <see cref="SnapshotOwnershipNotRecordedException"/> with the given message.</summary>
    public SnapshotOwnershipNotRecordedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="SnapshotOwnershipNotRecordedException"/> with the given message and inner exception.</summary>
    public SnapshotOwnershipNotRecordedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="SnapshotOwnershipNotRecordedException"/> naming the unmanaged snapshot.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotId">The DigitalOcean snapshot id that exists and is billing.</param>
    /// <param name="innerException">The failure that prevented the ownership mark, if any.</param>
    public SnapshotOwnershipNotRecordedException(string message, string snapshotId, Exception? innerException = null)
        : base(message, innerException) => SnapshotId = snapshotId;

    /// <summary>The DigitalOcean snapshot that exists, is billing, and is not Servyx-owned.</summary>
    public string? SnapshotId { get; }
}
