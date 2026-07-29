using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Azure;

// ---------------------------------------------------------------------------------------------------
// Reading a virtual machine's disks
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// A virtual machine read for one purpose only: to enumerate the managed disks attached to it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A second read model for the same ARM resource, and that is deliberate.</strong>
/// <see cref="ArmVirtualMachine"/> exists to answer the provisioning path's questions — size, image, address —
/// and its <see cref="ArmStorageProfile"/> models the OS disk's <c>deleteOption</c> and storage tier because
/// that is what maintenance and replace need. It models neither the OS disk's <em>managed disk id</em> nor the
/// <c>dataDisks</c> array at all, and a snapshot cannot be taken without both: the id is the
/// <c>sourceResourceId</c> a snapshot is created from, and the array is the difference between backing up a
/// game server and backing up the operating system it happens to run.
/// </para>
/// <para>
/// Widening the existing model instead would have made two unrelated code paths share a shape, so that a change
/// made for the backup adapter could alter what a replacement machine is built from. A separate model is read
/// from the identical payload by the identical GET; nothing about the provisioning path changes.
/// </para>
/// </remarks>
internal sealed class ArmVirtualMachineDisks
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The Azure region the machine is in. A snapshot must be created in the same one.</summary>
    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public ArmVirtualMachineDiskProperties? Properties { get; init; }
}

/// <summary>The subset of a VM's <c>properties</c> the snapshot adapter reads.</summary>
internal sealed class ArmVirtualMachineDiskProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    [JsonPropertyName("storageProfile")]
    public ArmDiskStorageProfile? StorageProfile { get; init; }
}

/// <summary>A VM's storage profile as the snapshot adapter reads it: the OS disk and every data disk.</summary>
internal sealed class ArmDiskStorageProfile
{
    [JsonPropertyName("osDisk")]
    public ArmVmOsDiskReference? OsDisk { get; init; }

    /// <summary>
    /// Every data disk attached to the machine. Absent, not empty, on a machine that has none — which is why
    /// this is nullable and every consumer treats null and empty identically.
    /// </summary>
    [JsonPropertyName("dataDisks")]
    public IReadOnlyList<ArmVmDataDiskReference>? DataDisks { get; init; }
}

/// <summary>The VM's OS disk attachment.</summary>
internal sealed class ArmVmOsDiskReference
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The disk's provisioned size in GB, as ARM reports it on the attachment.</summary>
    [JsonPropertyName("diskSizeGB")]
    public int? DiskSizeGb { get; init; }

    /// <summary>
    /// The managed disk behind the attachment.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> on a machine using unmanaged (page-blob VHD) disks. That is not a defensive
    /// branch: <c>Microsoft.Compute/snapshots</c> can only be created from a managed disk, so a machine whose
    /// OS disk is unmanaged cannot be backed up by this adapter at all and is refused rather than partially
    /// captured.
    /// </remarks>
    [JsonPropertyName("managedDisk")]
    public ArmManagedDiskReference? ManagedDisk { get; init; }
}

/// <summary>One data disk attachment on the VM.</summary>
internal sealed class ArmVmDataDiskReference
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The logical unit number the disk is attached at — the Azure analogue of an EC2 device name.</summary>
    [JsonPropertyName("lun")]
    public int? Lun { get; init; }

    [JsonPropertyName("diskSizeGB")]
    public int? DiskSizeGb { get; init; }

    [JsonPropertyName("managedDisk")]
    public ArmManagedDiskReference? ManagedDisk { get; init; }
}

/// <summary>A reference to a managed disk resource from a VM's storage profile.</summary>
/// <remarks>
/// Distinct from <see cref="ArmManagedDisk"/>, which models only the storage tier because that is all the
/// replace path needs. This one carries the <c>id</c>, which is the whole point: it is the value that goes into
/// a snapshot's <c>creationData.sourceResourceId</c>.
/// </remarks>
internal sealed class ArmManagedDiskReference
{
    /// <summary>The managed disk's own ARM resource id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The disk's storage tier, e.g. <c>Premium_LRS</c>.</summary>
    [JsonPropertyName("storageAccountType")]
    public string? StorageAccountType { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// The snapshot resource itself
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// A <c>Microsoft.Compute/snapshots</c> resource as ARM reports it.
/// </summary>
/// <remarks>
/// <strong>Note what this type has that an EBS snapshot's model does not: an <c>id</c>, a <c>location</c> and a
/// <c>tags</c> collection of its own.</strong> An Azure snapshot is a first-class ARM resource living in a
/// resource group, not an attribute of the disk it came from. That is why it is visible to the same
/// subscription-wide tag sweep the VM adapter uses — and why an orphaned one is a billable resource sitting in
/// somebody's resource group rather than an invisible line on a bill.
/// </remarks>
internal sealed class ArmSnapshot
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public ArmSnapshotProperties? Properties { get; init; }
}

/// <summary>The subset of a snapshot's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmSnapshotProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    /// <summary>When ARM created the snapshot resource.</summary>
    [JsonPropertyName("timeCreated")]
    public DateTimeOffset? TimeCreated { get; init; }

    /// <summary>
    /// The <em>source disk's provisioned</em> size in GB.
    /// </summary>
    /// <remarks>
    /// Emphatically not the snapshot's billed size, and the distinction is the entire subject of
    /// <c>AzureSnapshotPricing</c>. An incremental snapshot stores only the blocks that changed since the
    /// previous snapshot of the same disk, and ARM does not report how many that is.
    /// </remarks>
    [JsonPropertyName("diskSizeGB")]
    public int? DiskSizeGb { get; init; }

    /// <summary>Whether the snapshot was created as an incremental one. Servyx always asks for <c>true</c>.</summary>
    [JsonPropertyName("incremental")]
    public bool? Incremental { get; init; }

    /// <summary>
    /// How much of an incremental snapshot's background data copy has finished, as a percentage.
    /// </summary>
    /// <remarks>
    /// <strong>This member is the reason an Azure snapshot create needs a second poll, and it has no EBS
    /// analogue.</strong> ARM reports an incremental snapshot's <c>provisioningState</c> as <c>Succeeded</c>
    /// while the data copy is still running in the background; the snapshot is not usable as the source of a
    /// disk until this reaches 100. Treating the provisioning state as the finish line would report a backup
    /// that does not yet contain the data. Absent for a full (non-incremental) snapshot, and absent on older
    /// api-versions, so every consumer distinguishes "not reported" from "not finished".
    /// </remarks>
    [JsonPropertyName("completionPercent")]
    public double? CompletionPercent { get; init; }

    /// <summary>The snapshot's disk state, e.g. <c>Unattached</c> or <c>ActiveSAS</c>.</summary>
    [JsonPropertyName("diskState")]
    public string? DiskState { get; init; }

    [JsonPropertyName("creationData")]
    public ArmSnapshotCreationData? CreationData { get; init; }
}

/// <summary>How a snapshot came to exist, as ARM reports it.</summary>
internal sealed class ArmSnapshotCreationData
{
    /// <summary>e.g. <c>Copy</c> — the option that snapshots an existing managed disk.</summary>
    [JsonPropertyName("createOption")]
    public string? CreateOption { get; init; }

    /// <summary>The ARM id of the managed disk the snapshot was taken from.</summary>
    /// <remarks>
    /// The only link between a snapshot and the disk it came from, and therefore the only way to tell whether a
    /// snapshot Servyx did <em>not</em> create is nonetheless a snapshot of this server's data.
    /// </remarks>
    [JsonPropertyName("sourceResourceId")]
    public string? SourceResourceId { get; init; }
}

/// <summary>The <c>{ "value": [ ... ], "nextLink": "..." }</c> envelope a snapshot listing answers with.</summary>
internal sealed class ArmSnapshotListEnvelope
{
    [JsonPropertyName("value")]
    public IReadOnlyList<ArmSnapshot>? Value { get; init; }

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// Creating a snapshot
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// The body sent to <c>PUT</c> a <c>Microsoft.Compute/snapshots</c> resource.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every member is <c>required</c>, and the one that matters most is
/// <see cref="ArmSnapshotRequestProperties.Incremental"/>.</strong> ARM defaults <c>incremental</c> to
/// <see langword="false"/>, which is a full snapshot billed against the disk's whole stored contents on every
/// capture. Leaving it unstated would make the cost of a Servyx backup depend on an ARM default nobody here
/// would notice changing, so it is named explicitly on every write — the same reason the EBS adapter names
/// <c>ExcludeBootVolume</c> rather than accepting AWS's default.
/// </para>
/// <para>
/// There is deliberately no member for a source <em>VM</em>. <c>Microsoft.Compute/snapshots</c> takes exactly
/// one <c>sourceResourceId</c>, and it is a disk. That single fact is why a multi-disk Azure backup cannot be
/// one atomic operation, and the shape of this type is where that is unavoidable rather than merely stated.
/// </para>
/// </remarks>
internal sealed class ArmSnapshotRequest
{
    /// <summary>The region to create the snapshot in. Must match the source disk's region.</summary>
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    /// <summary>The snapshot's own ARM tags — where all four ownership marks live.</summary>
    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("properties")]
    public required ArmSnapshotRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> object of a snapshot write.</summary>
internal sealed class ArmSnapshotRequestProperties
{
    [JsonPropertyName("creationData")]
    public required ArmSnapshotCreationDataRequest CreationData { get; init; }

    /// <summary>
    /// Whether to store only the blocks that changed since the previous snapshot of the same disk.
    /// </summary>
    /// <remarks>
    /// Servyx always sends <see langword="true"/>. The choice is genuinely the caller's — unlike EBS, where
    /// incremental storage is how the service works and there is no flag — and the alternative costs money on
    /// every single capture rather than only on the first.
    /// </remarks>
    [JsonPropertyName("incremental")]
    public required bool Incremental { get; init; }
}

/// <summary>The <c>creationData</c> object of a snapshot write: one disk, copied.</summary>
internal sealed class ArmSnapshotCreationDataRequest
{
    /// <summary>Always <c>Copy</c> — the option that snapshots an existing managed disk.</summary>
    [JsonPropertyName("createOption")]
    public required string CreateOption { get; init; }

    /// <summary>The ARM id of the managed disk to snapshot. Exactly one, by ARM's design.</summary>
    [JsonPropertyName("sourceResourceId")]
    public required string SourceResourceId { get; init; }
}
