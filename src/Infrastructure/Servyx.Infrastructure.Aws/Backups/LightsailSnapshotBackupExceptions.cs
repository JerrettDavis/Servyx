namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>Thrown when a backup id does not resolve to any instance snapshot Lightsail currently reports.</summary>
/// <remarks>
/// A snapshot can vanish provider-side between two Servyx calls — deleted in the console, deleted by another
/// tool, rotated away by Lightsail's own automatic-snapshot add-on, or removed with the account. Resolution
/// therefore always goes back through a fresh listing and matches on the whole id, so an id naming a snapshot
/// that is no longer there fails as "not found" rather than being trusted as something to act on.
/// </remarks>
public sealed class LightsailSnapshotNotFoundException : Exception
{
    /// <summary>Creates a <see cref="LightsailSnapshotNotFoundException"/> with a default message.</summary>
    public LightsailSnapshotNotFoundException()
        : base("The requested Lightsail instance snapshot does not exist.")
    {
    }

    /// <summary>Creates a <see cref="LightsailSnapshotNotFoundException"/> with the given message.</summary>
    public LightsailSnapshotNotFoundException(string message) : base(message) { }

    /// <summary>Creates a <see cref="LightsailSnapshotNotFoundException"/> with the given message and inner exception.</summary>
    public LightsailSnapshotNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="LightsailSnapshotNotFoundException"/> carrying the offending backup id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="backupId">The backup id that did not resolve.</param>
    public LightsailSnapshotNotFoundException(string message, string backupId) : base(message) => BackupId = backupId;

    /// <summary>The backup id that did not resolve, if known.</summary>
    public string? BackupId { get; }
}

/// <summary>
/// Thrown when something asks this provider to delete a snapshot it is not entitled to delete — one Servyx did
/// not create, or one taken from a different instance or for a different server.
/// </summary>
/// <remarks>
/// This exception exists to be un-throwable in practice. It is the innermost of the barriers described on
/// <see cref="LightsailSnapshotBackupProvider.PruneAsync"/>; reaching it means an earlier barrier was bypassed by
/// a code change, and failing loudly at that point is the difference between a caught regression and an
/// irreversibly deleted snapshot that Servyx never created and may have been somebody's only copy.
/// </remarks>
public sealed class ForeignLightsailSnapshotProtectedException : Exception
{
    /// <summary>Creates a <see cref="ForeignLightsailSnapshotProtectedException"/> with a default message.</summary>
    public ForeignLightsailSnapshotProtectedException()
        : base("Servyx does not delete Lightsail instance snapshots it did not create.")
    {
    }

    /// <summary>Creates a <see cref="ForeignLightsailSnapshotProtectedException"/> with the given message.</summary>
    public ForeignLightsailSnapshotProtectedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ForeignLightsailSnapshotProtectedException"/> with the given message and inner exception.</summary>
    public ForeignLightsailSnapshotProtectedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="ForeignLightsailSnapshotProtectedException"/> carrying the protected artifact.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="location">The snapshot location that was protected.</param>
    public ForeignLightsailSnapshotProtectedException(string message, string location) : base(message) =>
        Location = location;

    /// <summary>The snapshot location that was protected, if known.</summary>
    public string? Location { get; }
}

/// <summary>
/// Thrown when Lightsail accepted a <c>CreateInstanceSnapshot</c> call but the snapshot was never observed
/// reaching a terminal state — so Servyx does not know whether the backup exists.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately distinct from a failure, and the distinction is the point. An <c>error</c> snapshot is over and
/// may be retried. One still <c>pending</c> is not over, and "retrying" it submits the same mutation a second
/// time — a second snapshot billing alongside the first, sharing blocks with it in ways that make the storage
/// arithmetic even harder to reason about.
/// </para>
/// <para>
/// <see cref="SnapshotName"/> names the snapshot, and <see cref="Observed"/> says whether Servyx ever saw it as
/// an object at Lightsail. Both matter to whoever reads this: a snapshot that was observed <em>exists and is
/// billing now</em>, while one that was never observed may be materialising, may have been created under a name
/// nobody is watching, or may not exist at all — and the honest answer is that this adapter cannot tell which.
/// </para>
/// </remarks>
public sealed class LightsailSnapshotNotConfirmedException : Exception
{
    /// <summary>Creates a <see cref="LightsailSnapshotNotConfirmedException"/> with a default message.</summary>
    public LightsailSnapshotNotConfirmedException()
        : base("Lightsail accepted the snapshot request but never reported it finished, so its outcome is unknown.")
    {
    }

    /// <summary>Creates a <see cref="LightsailSnapshotNotConfirmedException"/> with the given message.</summary>
    public LightsailSnapshotNotConfirmedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="LightsailSnapshotNotConfirmedException"/> with the given message and inner exception.</summary>
    public LightsailSnapshotNotConfirmedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="LightsailSnapshotNotConfirmedException"/> naming the snapshot that was watched.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotName">The snapshot name Lightsail was asked to create.</param>
    /// <param name="submitted">Whether Lightsail accepted the operation and it may still be running.</param>
    /// <param name="observed">Whether Servyx ever read the snapshot back as an object, i.e. whether it demonstrably exists.</param>
    public LightsailSnapshotNotConfirmedException(
        string message,
        string snapshotName,
        bool submitted,
        bool observed)
        : base(message)
    {
        SnapshotName = snapshotName;
        Submitted = submitted;
        Observed = observed;
    }

    /// <summary>The snapshot Lightsail was asked to create, if known.</summary>
    public string? SnapshotName { get; }

    /// <summary>Whether the operation was accepted by Lightsail and may still be running there.</summary>
    public bool Submitted { get; }

    /// <summary>Whether Servyx read the snapshot back at least once, so it demonstrably exists and is billing.</summary>
    public bool Observed { get; }
}

/// <summary>
/// Thrown when Lightsail reported a snapshot as <c>error</c>, or reported the create operation itself as
/// <c>Failed</c>.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="LightsailSnapshotNotConfirmedException"/> and deliberately a different type:
/// this one means the operation is <em>over</em> and did not produce a usable backup, which is the case where
/// retrying is reasonable. Collapsing the two into one failure answer is the mistake these signatures are shaped
/// to prevent.
/// </remarks>
public sealed class LightsailSnapshotFailedException : Exception
{
    /// <summary>Creates a <see cref="LightsailSnapshotFailedException"/> with a default message.</summary>
    public LightsailSnapshotFailedException()
        : base("Lightsail reported the instance snapshot as errored, so no backup was taken.")
    {
    }

    /// <summary>Creates a <see cref="LightsailSnapshotFailedException"/> with the given message.</summary>
    public LightsailSnapshotFailedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="LightsailSnapshotFailedException"/> with the given message and inner exception.</summary>
    public LightsailSnapshotFailedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="LightsailSnapshotFailedException"/> naming the snapshot involved.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotName">The snapshot name Lightsail was asked to create.</param>
    public LightsailSnapshotFailedException(string message, string snapshotName) : base(message) =>
        SnapshotName = snapshotName;

    /// <summary>The snapshot Lightsail was asked to create, if known.</summary>
    public string? SnapshotName { get; }
}

/// <summary>
/// Thrown when a snapshot was taken successfully but could not be verified as Servyx-owned.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot exists and is billing. Servyx cannot recognise it as its own, so it will be listed as
/// <see cref="Domain.Backups.BackupOwnership.Foreign"/> forever and <strong>will never be pruned by
/// retention</strong>. That is the safe direction — Servyx does not delete what it cannot prove it owns — but it
/// is also a charge that will not stop on its own, so it is raised as an error naming the snapshot and its cost
/// ceiling rather than returned as a successful backup.
/// </para>
/// <para>
/// Rare, and for a structural reason: <c>CreateInstanceSnapshot</c> applies the ownership tags in the same call
/// that creates the snapshot, so there is no window between creation and tagging — and AWS's own Lightsail
/// documentation states that if tags cannot be applied during resource creation, Lightsail rolls the creation
/// back. Reaching this exception means Lightsail accepted a tagged create and then reported the snapshot without
/// the tags, which should not happen, and is exactly why it is checked rather than assumed.
/// </para>
/// </remarks>
public sealed class LightsailSnapshotOwnershipNotRecordedException : Exception
{
    /// <summary>Creates a <see cref="LightsailSnapshotOwnershipNotRecordedException"/> with a default message.</summary>
    public LightsailSnapshotOwnershipNotRecordedException()
        : base("The snapshot was taken but could not be verified as Servyx-owned, so it is billing and unmanaged.")
    {
    }

    /// <summary>Creates a <see cref="LightsailSnapshotOwnershipNotRecordedException"/> with the given message.</summary>
    public LightsailSnapshotOwnershipNotRecordedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="LightsailSnapshotOwnershipNotRecordedException"/> with the given message and inner exception.</summary>
    public LightsailSnapshotOwnershipNotRecordedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="LightsailSnapshotOwnershipNotRecordedException"/> naming the unmanaged snapshot.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="snapshotName">The Lightsail instance snapshot that exists and is billing.</param>
    public LightsailSnapshotOwnershipNotRecordedException(string message, string snapshotName)
        : base(message) => SnapshotName = snapshotName;

    /// <summary>The Lightsail snapshot that exists, is billing, and is not Servyx-owned.</summary>
    public string? SnapshotName { get; }
}

/// <summary>
/// Thrown by every restore entry point on <see cref="LightsailSnapshotBackupProvider"/>, because restoring from a
/// Lightsail instance snapshot does not restore the server — it creates a second one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a shape difference, not a missing feature, and it is a different shape from both siblings.</strong>
/// Restoring a DigitalOcean droplet from a snapshot is one call that replaces the droplet's disk <em>in place</em>:
/// the machine keeps its id and its address, and everything on it is destroyed. Restoring from an EBS snapshot
/// creates a new <em>volume</em> that must then be attached. Restoring from a Lightsail instance snapshot does
/// neither: <c>CreateInstancesFromSnapshot</c> requires a new instance name, an availability zone and a bundle,
/// and it produces a <strong>new, separate, additionally-billing instance</strong>. The original instance is not
/// touched, not stopped, and not overwritten.
/// </para>
/// <para>
/// <strong>That makes the create call the safe part and everything after it the dangerous part.</strong> The one
/// call this provider could make destroys nothing. What would make the server actually <em>restored</em> is
/// everything that follows: moving the static IP, re-pointing DNS, re-creating the firewall rules that
/// <em>do not copy</em> from the original instance, telling Servyx that this server now lives on a different
/// Lightsail instance, and deleting or stopping the old machine. Those are lifecycle and record-keeping
/// operations spanning the provisioning path and Servyx's own state, not a backup provider's business — and
/// until they happen, both instances exist and both bill.
/// </para>
/// <para>
/// <strong>Doing only the first step would be worse than refusing.</strong> A method called "restore" that
/// returned successfully having launched a second instance would leave the caller believing their server had been
/// recovered, while the server they were actually using still ran the unrecovered data and the account quietly
/// paid for two machines. So this provider refuses, and
/// <see cref="LightsailSnapshotBackupProvider.PlanRestoreAsync"/> is made good enough that the refusal is not
/// obstructive: it issues no mutating call and names the snapshot, the bundle floor, the zone, the disks that
/// would come back, and every step Servyx will not take.
/// </para>
/// </remarks>
public sealed class LightsailSnapshotRestoreNotPerformedException : Exception
{
    /// <summary>Creates a <see cref="LightsailSnapshotRestoreNotPerformedException"/> with a default message.</summary>
    public LightsailSnapshotRestoreNotPerformedException()
        : base("Restoring from a Lightsail instance snapshot creates a new instance and this provider does not perform it.")
    {
    }

    /// <summary>Creates a <see cref="LightsailSnapshotRestoreNotPerformedException"/> with the given message.</summary>
    public LightsailSnapshotRestoreNotPerformedException(string message) : base(message) { }

    /// <summary>Creates a <see cref="LightsailSnapshotRestoreNotPerformedException"/> with the given message and inner exception.</summary>
    public LightsailSnapshotRestoreNotPerformedException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Creates a <see cref="LightsailSnapshotRestoreNotPerformedException"/> carrying the refused plan id.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="restorePlanId">The plan id that was refused.</param>
    public LightsailSnapshotRestoreNotPerformedException(string message, string restorePlanId) : base(message) =>
        RestorePlanId = restorePlanId;

    /// <summary>The plan id that was refused, if known.</summary>
    public string? RestorePlanId { get; }
}
