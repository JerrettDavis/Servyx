using System.Text.Json.Serialization;

namespace Servyx.Infrastructure.Azure;

// ---------------------------------------------------------------------------------------------------
// Token endpoint (login.microsoftonline.com)
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// The body <c>POST /{tenant}/oauth2/v2.0/token</c> answers a successful client-credentials exchange with.
/// </summary>
/// <remarks>
/// Only the three members Servyx actually reads are modelled. Note what is deliberately absent:
/// <c>refresh_token</c> (the client-credentials flow does not issue one - a new exchange is cheaper and
/// carries less to protect) and <c>id_token</c> (there is no user to describe here).
/// </remarks>
internal sealed class AzureTokenResponse
{
    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    /// <summary>The token's lifetime in seconds, as Entra ID states it. Never assumed; always read.</summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }
}

// ---------------------------------------------------------------------------------------------------
// ARM (management.azure.com)
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// The smallest shape every ARM resource shares, used for the provisioning-state poll after a PUT.
/// </summary>
/// <remarks>
/// Deserialised regardless of which resource type was written, so one poll implementation serves the
/// resource group, the virtual network, the public IP, the NIC and the VM. An ARM PUT that answers
/// <c>201 Created</c> with <c>provisioningState: "Updating"</c> has accepted the write, not completed it,
/// and issuing the next PUT (a NIC referencing a subnet that is still being created) against that state is
/// how a multi-resource sequence fails halfway.
/// </remarks>
internal sealed class ArmProvisioningProbe
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("properties")]
    public ArmProvisioningProbeProperties? Properties { get; init; }
}

/// <summary>The <c>properties</c> bag of <see cref="ArmProvisioningProbe"/>.</summary>
internal sealed class ArmProvisioningProbeProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }
}

/// <summary>The <c>{ "value": [ ... ], "nextLink": "..." }</c> envelope ARM's resource list answers with.</summary>
internal sealed class ArmResourceListEnvelope
{
    [JsonPropertyName("value")]
    public IReadOnlyList<ArmResourceSummary>? Value { get; init; }

    /// <summary>The absolute URL of the next page, or null on the last page. Followed, never truncated.</summary>
    [JsonPropertyName("nextLink")]
    public string? NextLink { get; init; }
}

/// <summary>
/// One row of ARM's <c>/subscriptions/{id}/resources</c> listing: enough to build a
/// <see cref="Domain.Provisioning.ResourceHandle"/> and no more.
/// </summary>
/// <remarks>
/// The listing spans <em>every</em> resource type in the subscription, which is exactly why the orphan sweep
/// can find a NIC or a public IP left behind by a half-finished create rather than only the VMs.
/// </remarks>
internal sealed class ArmResourceSummary
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>e.g. <c>Microsoft.Compute/virtualMachines</c>, <c>Microsoft.Network/networkInterfaces</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>A virtual machine as ARM reports it. Only the fields Servyx actually reads are modelled.</summary>
internal sealed class ArmVirtualMachine
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
    public ArmVirtualMachineProperties? Properties { get; init; }
}

/// <summary>The subset of a VM's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmVirtualMachineProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    /// <summary>
    /// When ARM reports the VM was created. Unlike a droplet's <c>created_at</c>, this member is only
    /// present from the 2021-11-01 compute API onwards and can legitimately be absent.
    /// </summary>
    [JsonPropertyName("timeCreated")]
    public DateTimeOffset? TimeCreated { get; init; }

    [JsonPropertyName("hardwareProfile")]
    public ArmHardwareProfile? HardwareProfile { get; init; }

    /// <summary>
    /// The VM's image reference and OS disk, as ARM currently reports them.
    /// </summary>
    /// <remarks>
    /// Read only by maintenance, and both halves are load-bearing there. The image reference is what a drift
    /// check compares against and what tells update planning whether the plan it is about to describe is a
    /// VM replacement. The OS disk's <c>deleteOption</c> is what lets a replacement plan state the fate of
    /// the machine's data as something read off the live machine rather than assumed from what this adapter
    /// wrote at create time.
    /// </remarks>
    [JsonPropertyName("storageProfile")]
    public ArmStorageProfile? StorageProfile { get; init; }

    /// <summary>
    /// The VM's OS-level configuration, as ARM currently reports it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read only by the replace path, where it is the difference between a replacement an operator can log in
    /// to and a machine nobody can reach. A replacement VM's <c>osProfile</c> cannot be inferred from a
    /// <see cref="Domain.Provisioning.UpdatePlan"/> — a plan describes a difference, not a whole machine — so
    /// the only honest source for the admin username and the authorised public key is the machine being
    /// replaced.
    /// </para>
    /// <para>
    /// <strong>What ARM does and does not return here.</strong> <c>adminUsername</c>, <c>computerName</c> and
    /// <c>linuxConfiguration.ssh.publicKeys[].keyData</c> are returned on every read: a public key is public,
    /// and this is the same material the create request sent. <c>customData</c> is <em>not</em> returned, and
    /// <c>adminPassword</c> never exists on a machine this adapter created. Neither is modelled, because a
    /// member that could never be populated would invite code that believed it had been.
    /// </para>
    /// </remarks>
    [JsonPropertyName("osProfile")]
    public ArmOsProfile? OsProfile { get; init; }

    [JsonPropertyName("networkProfile")]
    public ArmNetworkProfile? NetworkProfile { get; init; }
}

/// <summary>A VM's OS-level configuration, as ARM reports it on a read.</summary>
/// <remarks>
/// A separate type from <c>ArmOsProfileRequest</c> for the same reason <see cref="ArmStorageProfile"/> is
/// separate from <c>ArmStorageProfileRequest</c>: the request model's members are <c>required</c>, and a read
/// must be able to report a machine that carries none of them rather than fail to deserialise.
/// </remarks>
internal sealed class ArmOsProfile
{
    /// <summary>The machine's hostname as ARM records it.</summary>
    [JsonPropertyName("computerName")]
    public string? ComputerName { get; init; }

    /// <summary>The login name the machine's SSH keys are authorised for.</summary>
    [JsonPropertyName("adminUsername")]
    public string? AdminUsername { get; init; }

    [JsonPropertyName("linuxConfiguration")]
    public ArmLinuxConfiguration? LinuxConfiguration { get; init; }
}

/// <summary>A VM's Linux specifics, as ARM reports them.</summary>
internal sealed class ArmLinuxConfiguration
{
    /// <summary>
    /// Whether password login is disabled. Nullable rather than defaulted to <see langword="false"/>: a
    /// replacement built from a machine ARM said nothing about must not silently enable password login.
    /// </summary>
    [JsonPropertyName("disablePasswordAuthentication")]
    public bool? DisablePasswordAuthentication { get; init; }

    [JsonPropertyName("ssh")]
    public ArmSshConfiguration? Ssh { get; init; }
}

/// <summary>The authorised-keys block, as ARM reports it.</summary>
internal sealed class ArmSshConfiguration
{
    [JsonPropertyName("publicKeys")]
    public IReadOnlyList<ArmSshPublicKey>? PublicKeys { get; init; }
}

/// <summary>One authorised public key, as ARM reports it.</summary>
internal sealed class ArmSshPublicKey
{
    /// <summary>The authorized_keys path the key is installed at.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>The public key material itself. Public, and returned by ARM on every read.</summary>
    [JsonPropertyName("keyData")]
    public string? KeyData { get; init; }
}

/// <summary>A VM's size selection.</summary>
internal sealed class ArmHardwareProfile
{
    [JsonPropertyName("vmSize")]
    public string? VmSize { get; init; }
}

/// <summary>A VM's image and OS disk, as ARM reports them on a read.</summary>
/// <remarks>
/// A separate type from <c>ArmStorageProfileRequest</c> on purpose: the request model's members are
/// <c>required</c>, because a write that omitted one would be rejected by ARM, whereas a read must cope with
/// an older API version, a VM created from something other than a marketplace image, or a field ARM simply
/// does not return. Reusing the write model here would turn a partial response into a deserialisation
/// failure on a read path whose whole job is to report what is actually there.
/// </remarks>
internal sealed class ArmStorageProfile
{
    [JsonPropertyName("imageReference")]
    public ArmImageReferenceInfo? ImageReference { get; init; }

    [JsonPropertyName("osDisk")]
    public ArmOsDisk? OsDisk { get; init; }
}

/// <summary>The marketplace image a VM was created from, as ARM reports it.</summary>
internal sealed class ArmImageReferenceInfo
{
    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("offer")]
    public string? Offer { get; init; }

    [JsonPropertyName("sku")]
    public string? Sku { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary>A VM's OS disk, as ARM reports it.</summary>
internal sealed class ArmOsDisk
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// What happens to the managed disk when the VM is deleted: <c>Delete</c> or <c>Detach</c>.
    /// </summary>
    /// <remarks>
    /// This adapter writes <c>Delete</c> at create time (see <c>ArmOsDiskRequest</c>'s remarks for why), but
    /// maintenance reads the value back rather than assuming it: a plan that replaces the VM has to state
    /// what becomes of the machine's data, and that fate is decided by whatever this field says on the live
    /// machine, not by what the create request said months earlier.
    /// </remarks>
    [JsonPropertyName("deleteOption")]
    public string? DeleteOption { get; init; }

    /// <summary>
    /// The managed disk backing the OS disk, as ARM reports it.
    /// </summary>
    /// <remarks>
    /// Read by the replace path so a replacement machine's disk is created at the tier the machine being
    /// replaced actually had, rather than at whichever tier this adapter's spec happens to default to. A
    /// replace that silently downgraded Premium to Standard would change the machine's performance under an
    /// operator who approved an image change.
    /// </remarks>
    [JsonPropertyName("managedDisk")]
    public ArmManagedDisk? ManagedDisk { get; init; }
}

/// <summary>The managed disk behind a VM's OS disk, as ARM reports it.</summary>
internal sealed class ArmManagedDisk
{
    /// <summary>The disk's storage tier, e.g. <c>Premium_LRS</c>.</summary>
    [JsonPropertyName("storageAccountType")]
    public string? StorageAccountType { get; init; }
}

/// <summary>A VM's NIC attachments. The address a descriptor names is reached through these.</summary>
internal sealed class ArmNetworkProfile
{
    [JsonPropertyName("networkInterfaces")]
    public IReadOnlyList<ArmSubResource>? NetworkInterfaces { get; init; }
}

/// <summary>A bare <c>{ "id": "/subscriptions/..." }</c> reference to another ARM resource.</summary>
internal sealed class ArmSubResource
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>A network interface as ARM reports it.</summary>
internal sealed class ArmNetworkInterface
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public ArmNetworkInterfaceProperties? Properties { get; init; }
}

/// <summary>The subset of a NIC's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmNetworkInterfaceProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    [JsonPropertyName("ipConfigurations")]
    public IReadOnlyList<ArmIpConfiguration>? IpConfigurations { get; init; }
}

/// <summary>One IP configuration on a NIC.</summary>
internal sealed class ArmIpConfiguration
{
    [JsonPropertyName("properties")]
    public ArmIpConfigurationProperties? Properties { get; init; }
}

/// <summary>The subset of an IP configuration's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmIpConfigurationProperties
{
    [JsonPropertyName("privateIPAddress")]
    public string? PrivateIpAddress { get; init; }

    [JsonPropertyName("publicIPAddress")]
    public ArmSubResource? PublicIpAddress { get; init; }
}

/// <summary>A public IP address resource as ARM reports it.</summary>
internal sealed class ArmPublicIpAddress
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("properties")]
    public ArmPublicIpAddressProperties? Properties { get; init; }
}

/// <summary>The subset of a public IP's <c>properties</c> Servyx reads.</summary>
internal sealed class ArmPublicIpAddressProperties
{
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; init; }

    /// <summary>
    /// The allocated address, or null while allocation is pending. A <c>Static</c> Standard-SKU public IP
    /// carries one as soon as it is created, which is why the address poll normally completes on its first
    /// attempt - the VM does not have to have booted.
    /// </summary>
    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; init; }
}
