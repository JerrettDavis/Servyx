using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Azure;

/// <summary>The body sent to <c>PUT /subscriptions/{id}/resourcegroups/{name}</c>.</summary>
/// <remarks>
/// ARM's resource-group PUT is an upsert, and its status code is the only way to learn which it did:
/// <c>201 Created</c> means the group did not exist, <c>200 OK</c> means it did. The distinction is surfaced by
/// the API client and then deliberately not acted on - see <c>AzureVirtualMachineProvisioner</c>'s remarks on
/// why no code path here ever deletes a resource group, and what that costs.
/// </remarks>
internal sealed class ArmResourceGroupRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }
}

/// <summary>The body sent to <c>PUT .../virtualNetworks/{name}</c>, subnet included inline.</summary>
/// <remarks>
/// The subnet is written as a child of the virtual network rather than as a separate PUT because ARM models
/// it as a sub-resource: it has no independent lifetime and, critically for this adapter, <em>cannot carry
/// tags</em>. Creating it inline is therefore not a shortcut - it is the only shape available, and it is the
/// one created object in this sequence that an orphan sweep can never see directly. It is reachable only
/// through its parent virtual network, which is tagged, and dies with it.
/// </remarks>
internal sealed class ArmVirtualNetworkRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("properties")]
    public required ArmVirtualNetworkRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmVirtualNetworkRequest"/>.</summary>
internal sealed class ArmVirtualNetworkRequestProperties
{
    [JsonPropertyName("addressSpace")]
    public required ArmAddressSpace AddressSpace { get; init; }

    [JsonPropertyName("subnets")]
    public required IReadOnlyList<ArmSubnetRequest> Subnets { get; init; }
}

/// <summary>A virtual network's address space.</summary>
internal sealed class ArmAddressSpace
{
    [JsonPropertyName("addressPrefixes")]
    public required IReadOnlyList<string> AddressPrefixes { get; init; }
}

/// <summary>A subnet written inline as a child of its virtual network. Carries no tags; ARM has nowhere to put them.</summary>
internal sealed class ArmSubnetRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("properties")]
    public required ArmSubnetRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmSubnetRequest"/>.</summary>
internal sealed class ArmSubnetRequestProperties
{
    [JsonPropertyName("addressPrefix")]
    public required string AddressPrefix { get; init; }
}

/// <summary>The body sent to <c>PUT .../publicIPAddresses/{name}</c>.</summary>
/// <remarks>
/// A <c>Standard</c>-SKU, <c>Static</c> address is used rather than a Basic/Dynamic one for a reason that
/// matters to the shape claim: a static address is allocated the moment the resource exists, so the address
/// this adapter hands to the SSH stage does not change if the machine is later stopped and started. It is
/// also the one billable resource in this sequence other than the VM itself, which is why it is tagged and
/// why the sweep can find it on its own.
/// </remarks>
internal sealed class ArmPublicIpRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("sku")]
    public required ArmSku Sku { get; init; }

    [JsonPropertyName("properties")]
    public required ArmPublicIpRequestProperties Properties { get; init; }
}

/// <summary>An ARM SKU selector.</summary>
internal sealed class ArmSku
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmPublicIpRequest"/>.</summary>
internal sealed class ArmPublicIpRequestProperties
{
    [JsonPropertyName("publicIPAllocationMethod")]
    public required string PublicIpAllocationMethod { get; init; }

    [JsonPropertyName("publicIPAddressVersion")]
    public required string PublicIpAddressVersion { get; init; }
}

/// <summary>The body sent to <c>PUT .../networkInterfaces/{name}</c>.</summary>
internal sealed class ArmNetworkInterfaceRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("properties")]
    public required ArmNetworkInterfaceRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmNetworkInterfaceRequest"/>.</summary>
internal sealed class ArmNetworkInterfaceRequestProperties
{
    [JsonPropertyName("ipConfigurations")]
    public required IReadOnlyList<ArmIpConfigurationRequest> IpConfigurations { get; init; }
}

/// <summary>One IP configuration on a new NIC, binding it to the subnet and the public address.</summary>
internal sealed class ArmIpConfigurationRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("properties")]
    public required ArmIpConfigurationRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmIpConfigurationRequest"/>.</summary>
internal sealed class ArmIpConfigurationRequestProperties
{
    [JsonPropertyName("privateIPAllocationMethod")]
    public required string PrivateIpAllocationMethod { get; init; }

    [JsonPropertyName("subnet")]
    public required ArmSubResource Subnet { get; init; }

    [JsonPropertyName("publicIPAddress")]
    public required ArmSubResource PublicIpAddress { get; init; }
}

/// <summary>The body sent to <c>PUT .../virtualMachines/{name}</c>: the last and only billable-by-the-hour write.</summary>
internal sealed class ArmVirtualMachineRequest
{
    [JsonPropertyName("location")]
    public required string Location { get; init; }

    [JsonPropertyName("tags")]
    public required IReadOnlyDictionary<string, string> Tags { get; init; }

    [JsonPropertyName("properties")]
    public required ArmVirtualMachineRequestProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmVirtualMachineRequest"/>.</summary>
internal sealed class ArmVirtualMachineRequestProperties
{
    [JsonPropertyName("hardwareProfile")]
    public required ArmHardwareProfileRequest HardwareProfile { get; init; }

    [JsonPropertyName("storageProfile")]
    public required ArmStorageProfileRequest StorageProfile { get; init; }

    [JsonPropertyName("osProfile")]
    public required ArmOsProfileRequest OsProfile { get; init; }

    [JsonPropertyName("networkProfile")]
    public required ArmNetworkProfileRequest NetworkProfile { get; init; }
}

/// <summary>The VM's size selection.</summary>
internal sealed class ArmHardwareProfileRequest
{
    [JsonPropertyName("vmSize")]
    public required string VmSize { get; init; }
}

/// <summary>The VM's image and OS disk.</summary>
internal sealed class ArmStorageProfileRequest
{
    [JsonPropertyName("imageReference")]
    public required ArmImageReference ImageReference { get; init; }

    [JsonPropertyName("osDisk")]
    public required ArmOsDiskRequest OsDisk { get; init; }
}

/// <summary>
/// An Azure Marketplace image, expanded from the four-part URN <c>publisher:offer:sku:version</c>.
/// </summary>
/// <remarks>
/// A real divergence from DigitalOcean worth noting where it happens: <c>MachineSpec.ImageRef</c> is one
/// opaque string, which is exactly a DigitalOcean image slug (<c>ubuntu-24-04-x64</c>) but is four
/// colon-separated fields here. The string is split rather than the domain type being widened, because the
/// four-part shape is Azure's, not shape I's.
/// </remarks>
internal sealed class ArmImageReference
{
    [JsonPropertyName("publisher")]
    public required string Publisher { get; init; }

    [JsonPropertyName("offer")]
    public required string Offer { get; init; }

    [JsonPropertyName("sku")]
    public required string Sku { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// The VM's OS disk.
/// </summary>
/// <remarks>
/// <strong><c>deleteOption: "Delete"</c> is load-bearing, not tidiness.</strong> The managed OS disk is
/// created implicitly by ARM as part of the VM write - Servyx never PUTs it, so Servyx cannot tag it, so an
/// orphan sweep can never find it. Azure's default is to <em>detach</em> the disk when the VM is deleted,
/// which would leave an untagged, unsweepable, per-GB-billing disk behind after every destroy. Declaring the
/// cascade at create time is the only point at which this adapter can close that hole.
/// </remarks>
internal sealed class ArmOsDiskRequest
{
    [JsonPropertyName("createOption")]
    public required string CreateOption { get; init; }

    [JsonPropertyName("deleteOption")]
    public required string DeleteOption { get; init; }

    [JsonPropertyName("managedDisk")]
    public required ArmManagedDiskRequest ManagedDisk { get; init; }
}

/// <summary>The OS disk's storage tier.</summary>
internal sealed class ArmManagedDiskRequest
{
    [JsonPropertyName("storageAccountType")]
    public required string StorageAccountType { get; init; }
}

/// <summary>The VM's OS-level configuration: the login name, the public key, and any caller-supplied cloud-init.</summary>
internal sealed class ArmOsProfileRequest
{
    [JsonPropertyName("computerName")]
    public required string ComputerName { get; init; }

    [JsonPropertyName("adminUsername")]
    public required string AdminUsername { get; init; }

    /// <summary>
    /// Caller-supplied cloud-init, base64-encoded because that is the only form ARM accepts. Nothing in this
    /// assembly authors one; when the caller supplies none, the member is omitted entirely.
    /// </summary>
    [JsonPropertyName("customData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CustomData { get; init; }

    [JsonPropertyName("linuxConfiguration")]
    public required ArmLinuxConfigurationRequest LinuxConfiguration { get; init; }
}

/// <summary>Linux specifics: password login off, one authorised public key on.</summary>
internal sealed class ArmLinuxConfigurationRequest
{
    [JsonPropertyName("disablePasswordAuthentication")]
    public required bool DisablePasswordAuthentication { get; init; }

    [JsonPropertyName("ssh")]
    public required ArmSshConfigurationRequest Ssh { get; init; }
}

/// <summary>The authorised-keys block.</summary>
internal sealed class ArmSshConfigurationRequest
{
    [JsonPropertyName("publicKeys")]
    public required IReadOnlyList<ArmSshPublicKeyRequest> PublicKeys { get; init; }
}

/// <summary>
/// One authorised public key.
/// </summary>
/// <remarks>
/// The place shape I fits Azure <em>better</em> than it fits DigitalOcean. <c>MachineSpec.SshPublicKey</c>
/// holds raw public key material, which <c>POST /v2/droplets</c> cannot consume at all (it takes only the ids
/// of keys already registered on the account). ARM takes the raw key here, so the domain field maps directly
/// onto the wire and no account-level key registration step exists.
/// </remarks>
internal sealed class ArmSshPublicKeyRequest
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("keyData")]
    public required string KeyData { get; init; }
}

/// <summary>The VM's NIC attachment.</summary>
internal sealed class ArmNetworkProfileRequest
{
    [JsonPropertyName("networkInterfaces")]
    public required IReadOnlyList<ArmNetworkInterfaceReference> NetworkInterfaces { get; init; }
}

/// <summary>
/// A reference from the VM to the NIC it was created against.
/// </summary>
/// <remarks>
/// Deliberately <em>without</em> a <c>deleteOption: "Delete"</c>, unlike the OS disk above, and the asymmetry
/// is intentional. The NIC is a resource Servyx PUT itself and therefore tagged, so the orphan sweep can find
/// it; making the VM cascade onto it would hide the multi-resource lifetime rather than manage it, and would
/// silently delete a NIC in the case where a caller destroys the VM but means to keep the address wiring. The
/// OS disk has no such option because it can never be found by a sweep.
/// </remarks>
internal sealed class ArmNetworkInterfaceReference
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("properties")]
    public required ArmNetworkInterfaceReferenceProperties Properties { get; init; }
}

/// <summary>The <c>properties</c> of <see cref="ArmNetworkInterfaceReference"/>.</summary>
internal sealed class ArmNetworkInterfaceReferenceProperties
{
    [JsonPropertyName("primary")]
    public required bool Primary { get; init; }
}
