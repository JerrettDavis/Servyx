namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>Thrown when a backup id does not resolve to any snapshot set AWS currently reports.</summary>
/// <remarks>
/// A snapshot can vanish provider-side between two Servyx calls — deleted in the console, deleted by another
/// tool, expired by an AWS Backup or Data Lifecycle Manager policy, or removed with the account. Resolution
/// therefore always goes back through a fresh listing and matches on the whole id, so an id naming a set that
/// is no longer there fails as "not found" rather than being trusted as something to act on.
/// </remarks>
public sealed class EbsSnapshotNotFoundException : Exception
{
    /// <summary>Creates an <see cref="EbsSnapshotNotFoundException"/> with a default message.</summary>
    public EbsSnapshotNotFoundException()
        : base("The requested EBS snapshot backup does not exist.")
    {
    }

    /// <summary>Creates an <see cref="EbsSnapshotNotFoundException"/> with the given message.</summary>
    public EbsSnapshotNotFoundException(string message) : base(message) { }

    /// <summary>Creates an <see cref="EbsSnapshotNotFoundException"/> with the given message and inner exception.</summary>
    public EbsSnapshotNotFoundException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="EbsSnapshotNotFoundException"/> carrying the offending backup id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="backupId">The backup id that did not resolve.</param>
    public EbsSnapshotNotFoundException(string message, string backupId) : base(message) => BackupId = backupId;

    /// <summary>The backup id that did not resolve, if known.</summary>
    public string? BackupId { get; }
}

/// <summary>
/// Thrown when something asks this provider to delete a snapshot it is not entitled to delete — one Servyx did
/// not create, or one taken from a different instance or for a different server.
/// </summary>
/// <remarks>
/// This exception exists to be un-throwable in practice. It is the innermost of the barriers described on
/// <see cref="EbsSnapshotBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was bypassed by a
/// code change, and failing loudly at that point is the difference between a caught regression and an
/// irreversibly deleted snapshot that Servyx never created and may have been somebody's only copy.
/// </remarks>
public sealed class ForeignEbsSnapshotProtectedException : Exception
{
    /// <summary>Creates a <see cref="ForeignEbsSnapshotProtectedException"/> with a default message.</summary>
    public ForeignEbsSnapshotProtectedException()
        : base("Servyx does not delete EBS snapshots it did not create.")
    {
    }

    /// <summary>Creates a <see cref="ForeignEbsSnapshotProtectedException"/> with the given message.</summary>
    public ForeignEbsSnapshotProtectedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ForeignEbsSnapshotProtectedException"/> with the given message and inner exception.</summary>
    public ForeignEbsSnapshotProtectedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="ForeignEbsSnapshotProtectedException"/> carrying the protected artifact.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="location">The snapshot or set location that was protected.</param>
    public ForeignEbsSnapshotProtectedException(string message, string location) : base(message) => Location = location;

    /// <summary>The snapshot or set location that was protected, if known.</summary>
    public string? Location { get; }
}

/// <summary>
/// Thrown when AWS accepted a <c>CreateSnapshots</c> call but the snapshots were never observed reaching a
/// terminal state — so Servyx does not know whether the backup exists.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from a failure, and the distinction is the point. An <em>errored</em> snapshot is over
/// and may be retried. A snapshot still <c>pending</c> is not over, and "retrying" it submits the same mutation
/// a second time — a second set that bills alongside the first, and which the first set's blocks will be
/// shared with in ways that make the storage arithmetic even harder to reason about.
/// </para>
/// <para>
/// <see cref="SnapshotIds"/> names the snapshots that <em>do</em> exist and <em>are</em> billing, because the
/// most damaging thing this exception could do is imply nothing was created.
/// </para>
/// </remarks>
public sealed class EbsSnapshotNotConfirmedException : Exception
{
    /// <summary>Creates an <see cref="EbsSnapshotNotConfirmedException"/> with a default message.</summary>
    public EbsSnapshotNotConfirmedException()
        : base("AWS accepted the snapshot request but never reported it finished, so its outcome is unknown.")
    {
    }

    /// <summary>Creates an <see cref="EbsSnapshotNotConfirmedException"/> with the given message.</summary>
    public EbsSnapshotNotConfirmedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="EbsSnapshotNotConfirmedException"/> with the given message and inner exception.</summary>
    public EbsSnapshotNotConfirmedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="EbsSnapshotNotConfirmedException"/> naming the snapshots that exist.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotIds">The snapshots AWS created, which exist and are billing.</param>
    /// <param name="submitted">Whether AWS accepted the operation and it may still be running.</param>
    public EbsSnapshotNotConfirmedException(string message, IReadOnlyList<string> snapshotIds, bool submitted)
        : base(message)
    {
        SnapshotIds = snapshotIds;
        Submitted = submitted;
    }

    /// <summary>The snapshots AWS created and is charging for, if known.</summary>
    public IReadOnlyList<string>? SnapshotIds { get; }

    /// <summary>Whether the operation was accepted by AWS and may still be running there.</summary>
    public bool Submitted { get; }
}

/// <summary>
/// Thrown when AWS reported a snapshot as <c>error</c>, produced no snapshots at all, or produced a set that
/// does not cover every EBS volume the instance had.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="EbsSnapshotNotConfirmedException"/> and deliberately a different type: this
/// one means the operation is <em>over</em> and did not produce a usable backup, which is the case where
/// retrying is reasonable. Collapsing the two into one failure answer is the mistake these signatures are
/// shaped to prevent.
/// </remarks>
public sealed class EbsSnapshotFailedException : Exception
{
    /// <summary>Creates an <see cref="EbsSnapshotFailedException"/> with a default message.</summary>
    public EbsSnapshotFailedException()
        : base("AWS reported the snapshot as errored, so no backup was taken.")
    {
    }

    /// <summary>Creates an <see cref="EbsSnapshotFailedException"/> with the given message.</summary>
    public EbsSnapshotFailedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="EbsSnapshotFailedException"/> with the given message and inner exception.</summary>
    public EbsSnapshotFailedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="EbsSnapshotFailedException"/> naming the snapshots involved.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotIds">The snapshots AWS created, if any. They exist and are billing.</param>
    public EbsSnapshotFailedException(string message, IReadOnlyList<string> snapshotIds) : base(message) =>
        SnapshotIds = snapshotIds;

    /// <summary>The snapshots AWS created before the failure, if any.</summary>
    public IReadOnlyList<string>? SnapshotIds { get; }
}

/// <summary>
/// Thrown when snapshots were taken successfully but could not be verified as Servyx-owned.
/// </summary>
/// <remarks>
/// <para>
/// The snapshots exist and are billing. Servyx cannot recognise them as its own, so they will be listed as
/// <see cref="Domain.Backups.BackupOwnership.Foreign"/> forever and <strong>will never be pruned by
/// retention</strong>. That is the safe direction — Servyx does not delete what it cannot prove it owns — but
/// it is also a charge that will not stop on its own, so it is raised as an error naming the snapshot ids and
/// the cost ceiling rather than returned as a successful backup.
/// </para>
/// <para>
/// Rarer here than on the DigitalOcean adapter, and for a structural reason: <c>CreateSnapshots</c> applies the
/// ownership tags in the same call that creates the snapshots, so there is no window between creation and
/// tagging. Reaching this exception means AWS accepted a tagged create and then reported the snapshots without
/// the tags, which should not happen — and is exactly why it is checked rather than assumed.
/// </para>
/// </remarks>
public sealed class EbsSnapshotOwnershipNotRecordedException : Exception
{
    /// <summary>Creates an <see cref="EbsSnapshotOwnershipNotRecordedException"/> with a default message.</summary>
    public EbsSnapshotOwnershipNotRecordedException()
        : base("The snapshots were taken but could not be verified as Servyx-owned, so they are billing and unmanaged.")
    {
    }

    /// <summary>Creates an <see cref="EbsSnapshotOwnershipNotRecordedException"/> with the given message.</summary>
    public EbsSnapshotOwnershipNotRecordedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="EbsSnapshotOwnershipNotRecordedException"/> with the given message and inner exception.</summary>
    public EbsSnapshotOwnershipNotRecordedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="EbsSnapshotOwnershipNotRecordedException"/> naming the unmanaged snapshots.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotIds">The EBS snapshots that exist and are billing.</param>
    public EbsSnapshotOwnershipNotRecordedException(string message, IReadOnlyList<string> snapshotIds)
        : base(message) => SnapshotIds = snapshotIds;

    /// <summary>The EBS snapshots that exist, are billing, and are not Servyx-owned.</summary>
    public IReadOnlyList<string>? SnapshotIds { get; }
}

/// <summary>
/// Thrown by every restore entry point on <see cref="EbsSnapshotBackupProvider"/>, because restoring from an
/// EBS snapshot is not an operation this provider can carry out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a shape difference, not a missing feature.</strong> Restoring a DigitalOcean droplet from a
/// snapshot is one API call that replaces the droplet's disk in place. An EBS snapshot cannot do that: it
/// restores by <em>creating a new volume</em>, which then has to be attached, and putting a restored root
/// volume back under a running instance means stopping the instance, detaching the current root, attaching the
/// new one at the same device, and starting it again. That is four mutating calls plus downtime, spanning the
/// instance's lifecycle rather than its backups, and Servyx will not present it as "call RestoreAsync".
/// </para>
/// <para>
/// <strong>Refusing beats doing half of it.</strong> The tempting middle ground — create the volumes and stop
/// there — leaves unattached volumes billing per GB-month next to an instance that is still running on its
/// original disks, while having returned success from something called "restore". A caller would reasonably
/// believe the server had been restored. It would not have been.
/// </para>
/// <para>
/// <see cref="EbsSnapshotBackupProvider.PlanRestoreAsync"/> is fully supported and issues no mutating call: it
/// names each snapshot, the volume and device it came from, the availability zone a restored volume must be
/// created in, and the exact ordered procedure — so the operator gets something they can actually carry out,
/// rather than a method that pretends to.
/// </para>
/// </remarks>
public sealed class EbsSnapshotRestoreNotPerformedException : Exception
{
    /// <summary>Creates an <see cref="EbsSnapshotRestoreNotPerformedException"/> with a default message.</summary>
    public EbsSnapshotRestoreNotPerformedException()
        : base("Restoring from an EBS snapshot is not a single call and this provider does not perform it.")
    {
    }

    /// <summary>Creates an <see cref="EbsSnapshotRestoreNotPerformedException"/> with the given message.</summary>
    public EbsSnapshotRestoreNotPerformedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="EbsSnapshotRestoreNotPerformedException"/> with the given message and inner exception.</summary>
    public EbsSnapshotRestoreNotPerformedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="EbsSnapshotRestoreNotPerformedException"/> carrying the refused plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public EbsSnapshotRestoreNotPerformedException(string message, string restorePlanId) : base(message) =>
        RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}
