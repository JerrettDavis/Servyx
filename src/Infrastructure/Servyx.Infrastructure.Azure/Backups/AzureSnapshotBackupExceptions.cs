namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>Thrown when a backup id does not resolve to any snapshot Azure currently reports.</summary>
/// <remarks>
/// A snapshot can vanish provider-side between two Servyx calls — deleted in the portal, deleted by another
/// tool, expired by an Azure Backup policy, or removed with its resource group. Resolution therefore always
/// goes back through a fresh listing and matches on the whole id, so an id naming a set that is no longer there
/// fails as "not found" rather than being trusted as something to act on.
/// </remarks>
public sealed class AzureSnapshotNotFoundException : Exception
{
    /// <summary>Creates an <see cref="AzureSnapshotNotFoundException"/> with a default message.</summary>
    public AzureSnapshotNotFoundException()
        : base("The requested Azure managed-disk snapshot backup does not exist.")
    {
    }

    /// <summary>Creates an <see cref="AzureSnapshotNotFoundException"/> with the given message.</summary>
    public AzureSnapshotNotFoundException(string message) : base(message) { }

    /// <summary>Creates an <see cref="AzureSnapshotNotFoundException"/> with the given message and inner exception.</summary>
    public AzureSnapshotNotFoundException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="AzureSnapshotNotFoundException"/> carrying the offending backup id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="backupId">The backup id that did not resolve.</param>
    public AzureSnapshotNotFoundException(string message, string backupId) : base(message) => BackupId = backupId;

    /// <summary>The backup id that did not resolve, if known.</summary>
    public string? BackupId { get; }
}

/// <summary>
/// Thrown when something asks this provider to delete a snapshot it is not entitled to delete — one Servyx did
/// not create, or one taken from a different machine or for a different server.
/// </summary>
/// <remarks>
/// This exception exists to be un-throwable in practice. It is the innermost of the barriers described on
/// <see cref="AzureSnapshotBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was bypassed by a
/// code change, and failing loudly at that point is the difference between a caught regression and an
/// irreversibly deleted snapshot that Servyx never created and may have been somebody's only copy.
/// </remarks>
public sealed class ForeignAzureSnapshotProtectedException : Exception
{
    /// <summary>Creates a <see cref="ForeignAzureSnapshotProtectedException"/> with a default message.</summary>
    public ForeignAzureSnapshotProtectedException()
        : base("Servyx does not delete Azure snapshots it did not create.")
    {
    }

    /// <summary>Creates a <see cref="ForeignAzureSnapshotProtectedException"/> with the given message.</summary>
    public ForeignAzureSnapshotProtectedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ForeignAzureSnapshotProtectedException"/> with the given message and inner exception.</summary>
    public ForeignAzureSnapshotProtectedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="ForeignAzureSnapshotProtectedException"/> carrying the protected artifact.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="location">The snapshot or set location that was protected.</param>
    public ForeignAzureSnapshotProtectedException(string message, string location) : base(message) =>
        Location = location;

    /// <summary>The snapshot or set location that was protected, if known.</summary>
    public string? Location { get; }
}

/// <summary>
/// Thrown when ARM accepted one or more snapshot writes but the snapshots were never observed reaching a
/// finished, fully-copied state — so Servyx does not know whether the backup exists.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from a failure, and the distinction is the point. A <em>failed</em> snapshot operation
/// is over and may be retried. A snapshot still provisioning, or still running its incremental background copy,
/// is not over, and "retrying" it submits the same mutation a second time — a second set that bills alongside
/// the first.
/// </para>
/// <para>
/// <strong>There are two distinct ways to land here, and both are real.</strong> ARM's long-running operation
/// may not have reached a terminal state within the polls; or ARM may report the snapshot resource
/// <c>Succeeded</c> while <c>completionPercent</c> is still below 100, which is Azure's way of saying the data
/// has not finished copying and the snapshot cannot yet be used to create a disk. The second has no EBS
/// analogue, and treating the provisioning state as the finish line would report a backup that does not yet
/// contain the data.
/// </para>
/// <para>
/// <see cref="SnapshotNames"/> names the snapshots that <em>do</em> exist and <em>are</em> billing, because the
/// most damaging thing this exception could do is imply nothing was created.
/// </para>
/// </remarks>
public sealed class AzureSnapshotNotConfirmedException : Exception
{
    /// <summary>Creates an <see cref="AzureSnapshotNotConfirmedException"/> with a default message.</summary>
    public AzureSnapshotNotConfirmedException()
        : base("Azure accepted the snapshot request but never reported it finished, so its outcome is unknown.")
    {
    }

    /// <summary>Creates an <see cref="AzureSnapshotNotConfirmedException"/> with the given message.</summary>
    public AzureSnapshotNotConfirmedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="AzureSnapshotNotConfirmedException"/> with the given message and inner exception.</summary>
    public AzureSnapshotNotConfirmedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="AzureSnapshotNotConfirmedException"/> naming the snapshots that exist.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotNames">The snapshots ARM created, which exist and are billing.</param>
    /// <param name="submitted">Whether ARM accepted the operation and it may still be running.</param>
    public AzureSnapshotNotConfirmedException(string message, IReadOnlyList<string> snapshotNames, bool submitted)
        : base(message)
    {
        SnapshotNames = snapshotNames;
        Submitted = submitted;
    }

    /// <summary>The ARM names of the snapshots that exist and are being charged for, if known.</summary>
    public IReadOnlyList<string>? SnapshotNames { get; }

    /// <summary>Whether the operation was accepted by ARM and may still be running there.</summary>
    public bool Submitted { get; }
}

/// <summary>
/// Thrown when ARM reported a snapshot operation as failed, when a machine has no snapshottable managed disk,
/// or when a capture did not cover every disk the machine had.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="AzureSnapshotNotConfirmedException"/> and deliberately a different type: this
/// one means the operation is <em>over</em> and did not produce a usable backup, which is the case where
/// retrying is reasonable. Collapsing the two into one failure answer is the mistake these signatures are
/// shaped to prevent.
/// </remarks>
public sealed class AzureSnapshotFailedException : Exception
{
    /// <summary>Creates an <see cref="AzureSnapshotFailedException"/> with a default message.</summary>
    public AzureSnapshotFailedException()
        : base("Azure reported the snapshot operation as failed, so no backup was taken.")
    {
    }

    /// <summary>Creates an <see cref="AzureSnapshotFailedException"/> with the given message.</summary>
    public AzureSnapshotFailedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="AzureSnapshotFailedException"/> with the given message and inner exception.</summary>
    public AzureSnapshotFailedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates an <see cref="AzureSnapshotFailedException"/> naming the snapshots involved.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotNames">The snapshots ARM created, if any. They exist and are billing.</param>
    public AzureSnapshotFailedException(string message, IReadOnlyList<string> snapshotNames) : base(message) =>
        SnapshotNames = snapshotNames;

    /// <summary>
    /// Creates an <see cref="AzureSnapshotFailedException"/> naming the snapshots involved and preserving the
    /// ARM refusal underneath.
    /// </summary>
    /// <remarks>
    /// The overload that exists because both halves matter and neither may be dropped: the caller needs the
    /// structured list of resources that are billing <em>and</em> Azure's own account of why the call failed.
    /// Collapsing one into the other's message is how a billing resource stops being enumerable.
    /// </remarks>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotNames">The snapshots ARM created before the failure. They exist and are billing.</param>
    /// <param name="innerException">The underlying Azure API failure.</param>
    public AzureSnapshotFailedException(
        string message,
        IReadOnlyList<string> snapshotNames,
        Exception innerException)
        : base(message, innerException) => SnapshotNames = snapshotNames;

    /// <summary>The ARM names of the snapshots that were created before the failure, if any.</summary>
    public IReadOnlyList<string>? SnapshotNames { get; }
}

/// <summary>
/// Thrown when snapshots were taken successfully but could not be verified as Servyx-owned.
/// </summary>
/// <remarks>
/// <para>
/// The snapshots exist and are billing. Servyx cannot recognise them as its own, so they will be listed as
/// <see cref="Domain.Backups.BackupOwnership.Foreign"/> forever and <strong>will never be pruned by
/// retention</strong>. That is the safe direction — Servyx does not delete what it cannot prove it owns — but
/// it is also a charge that will not stop on its own, so it is raised as an error naming the snapshot names and
/// the cost ceiling rather than returned as a successful backup.
/// </para>
/// <para>
/// Structurally rare here, for the same reason it is rare on the EBS adapter: an ARM snapshot write carries its
/// <c>tags</c> in the same request that creates the resource, so there is no window in which a billing snapshot
/// exists untagged. Reaching this exception means ARM accepted a tagged write and then reported the resource
/// without the tags, which should not happen — and is exactly why it is checked rather than assumed.
/// </para>
/// </remarks>
public sealed class AzureSnapshotOwnershipNotRecordedException : Exception
{
    /// <summary>Creates an <see cref="AzureSnapshotOwnershipNotRecordedException"/> with a default message.</summary>
    public AzureSnapshotOwnershipNotRecordedException()
        : base("The snapshots were taken but could not be verified as Servyx-owned, so they are billing and unmanaged.")
    {
    }

    /// <summary>Creates an <see cref="AzureSnapshotOwnershipNotRecordedException"/> with the given message.</summary>
    public AzureSnapshotOwnershipNotRecordedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="AzureSnapshotOwnershipNotRecordedException"/> with the given message and inner exception.</summary>
    public AzureSnapshotOwnershipNotRecordedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="AzureSnapshotOwnershipNotRecordedException"/> naming the unmanaged snapshots.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotNames">The snapshots that exist and are billing.</param>
    public AzureSnapshotOwnershipNotRecordedException(string message, IReadOnlyList<string> snapshotNames)
        : base(message) => SnapshotNames = snapshotNames;

    /// <summary>The ARM names of the snapshots that exist, are billing, and are not Servyx-owned.</summary>
    public IReadOnlyList<string>? SnapshotNames { get; }
}

/// <summary>
/// Thrown by <see cref="AzureSnapshotBackupProvider.RestoreAsync"/>, because restoring from a managed-disk
/// snapshot is not an operation this provider can carry out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a shape difference, not a missing feature.</strong> Restoring a DigitalOcean droplet from a
/// snapshot is one API call that replaces the droplet's disk in place. An Azure managed-disk snapshot cannot do
/// that: it restores by <em>creating a new managed disk</em> from it, which then has to be attached — and
/// putting a restored OS disk back under a machine means <c>deallocating</c> the machine (a full stop, not a
/// reboot), rewriting its storage profile to point at the new disk, and starting it again. That is several
/// mutating calls plus downtime, spanning the machine's lifecycle rather than its backups, and Servyx will not
/// present it as "call RestoreAsync".
/// </para>
/// <para>
/// <strong>Refusing beats doing half of it.</strong> The tempting middle ground — create the disks and stop
/// there — leaves unattached managed disks billing per GB-month at their <em>full provisioned</em> size (a
/// restored disk is a normal disk and is not billed incrementally the way its snapshot was) next to a machine
/// still running on its original disks, while having returned success from something called "restore". A caller
/// would reasonably believe the server had been restored. It would not have been.
/// </para>
/// <para>
/// <see cref="AzureSnapshotBackupProvider.PlanRestoreAsync"/> is fully supported and issues no mutating call:
/// it names each snapshot, the disk and LUN it came from, the region a restored disk must be created in, and
/// the exact ordered procedure — so the operator gets something they can actually carry out, rather than a
/// method that pretends to.
/// </para>
/// </remarks>
public sealed class AzureSnapshotRestoreNotPerformedException : Exception
{
    /// <summary>Creates an <see cref="AzureSnapshotRestoreNotPerformedException"/> with a default message.</summary>
    public AzureSnapshotRestoreNotPerformedException()
        : base("Restoring from an Azure managed-disk snapshot is not a single call and this provider does not perform it.")
    {
    }

    /// <summary>Creates an <see cref="AzureSnapshotRestoreNotPerformedException"/> with the given message.</summary>
    public AzureSnapshotRestoreNotPerformedException(string message) : base(message) { }

    /// <summary>Creates an <see cref="AzureSnapshotRestoreNotPerformedException"/> with the given message and inner exception.</summary>
    public AzureSnapshotRestoreNotPerformedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates an <see cref="AzureSnapshotRestoreNotPerformedException"/> carrying the refused plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public AzureSnapshotRestoreNotPerformedException(string message, string restorePlanId) : base(message) =>
        RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}
