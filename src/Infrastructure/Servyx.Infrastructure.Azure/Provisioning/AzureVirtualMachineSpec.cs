using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// Everything needed to create one Azure host: the provider-independent <see cref="MachineSpec"/> the domain
/// already defines for shape I, plus the things ARM needs that <see cref="MachineSpec"/> does not carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this wraps <see cref="MachineSpec"/> rather than replacing it.</strong> Same reason
/// <c>DigitalOceanDropletSpec</c> does: <see cref="MachineSpec"/> is the domain's statement that shape I's
/// clouds "differ trivially", and keeping it as a member rather than flattening it keeps the correspondence
/// visible. Most of it does map: <see cref="MachineSpec.SizeRef"/> is <c>hardwareProfile.vmSize</c>,
/// <see cref="MachineSpec.Region"/> is the ARM <c>location</c>, <see cref="MachineSpec.Tags"/> is the ARM
/// <c>tags</c> dictionary directly, and — unlike DigitalOcean — <see cref="MachineSpec.SshPublicKey"/> is
/// consumed as written, because ARM accepts raw key material where <c>POST /v2/droplets</c> cannot.
/// </para>
/// <para>
/// <strong>Where the correspondence stops being trivial, honestly.</strong> Three places:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="MachineSpec.ImageRef"/> is one opaque string, which is exactly a DigitalOcean slug but is a
/// four-part <c>publisher:offer:sku:version</c> URN here. It is split by <see cref="ParseImageUrn"/> rather
/// than the domain type being widened, because the four-part shape is Azure's rather than shape I's.
/// </description></item>
/// <item><description>
/// <see cref="MachineSpec.CloudInit"/> is forwarded verbatim by the DigitalOcean adapter, because
/// <c>user_data</c> is a plain string. ARM's <c>osProfile.customData</c> is base64, so this adapter
/// <em>encodes</em> it. That is a transformation, not authoring — the bytes round-trip exactly — and it is
/// the only transformation applied to caller content anywhere in this assembly.
/// </description></item>
/// <item><description>
/// A droplet is one object with one name. An Azure host is five, and four of them need names that ARM will
/// accept and that a teardown can find again. They are derived from <see cref="VmName"/> by default and are
/// overridable, and their names are recorded as tags on the VM so a handle can drive a teardown.
/// </description></item>
/// </list>
/// </remarks>
public sealed record AzureVirtualMachineSpec
{
    /// <summary>The default address space of the virtual network created for a host.</summary>
    public const string DefaultVirtualNetworkAddressPrefix = "10.20.0.0/16";

    /// <summary>The default address range of the subnet created inside that virtual network.</summary>
    public const string DefaultSubnetAddressPrefix = "10.20.0.0/24";

    /// <summary>The default managed-disk tier for the VM's OS disk.</summary>
    public const string DefaultOsDiskStorageAccountType = "Premium_LRS";

    /// <summary>Creates a spec.</summary>
    /// <param name="vmName">The virtual machine's name at the provider, and the stem the sibling resources are named from.</param>
    /// <param name="resourceGroup">The ARM resource group the host's resources live in. Created if it does not exist.</param>
    /// <param name="machine">The provider-independent machine shape.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException"><paramref name="vmName"/> or <paramref name="resourceGroup"/> is blank.</exception>
    public AzureVirtualMachineSpec(
        string vmName,
        string resourceGroup,
        MachineSpec machine,
        ServyxAzureTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tags);

        VmName = vmName;
        ResourceGroup = resourceGroup;
        Machine = machine;
        Tags = tags;

        VirtualNetworkName = vmName + "-vnet";
        SubnetName = vmName + "-subnet";
        PublicIpName = vmName + "-ip";
        NetworkInterfaceName = vmName + "-nic";
    }

    /// <summary>The virtual machine's name at the provider.</summary>
    public string VmName { get; }

    /// <summary>The ARM resource group the host's resources live in.</summary>
    public string ResourceGroup { get; }

    /// <summary>The provider-independent machine shape this host realises.</summary>
    public MachineSpec Machine { get; }

    /// <summary>The mandatory Servyx identity stamped onto every resource created for this host.</summary>
    public ServyxAzureTags Tags { get; }

    /// <summary>The name of the virtual network created for the host.</summary>
    public string VirtualNetworkName { get; init; }

    /// <summary>The name of the subnet created inside <see cref="VirtualNetworkName"/>.</summary>
    /// <remarks>A subnet is an ARM sub-resource: it carries no tags of its own and dies with its parent network.</remarks>
    public string SubnetName { get; init; }

    /// <summary>The name of the static public IPv4 address created for the host.</summary>
    public string PublicIpName { get; init; }

    /// <summary>The name of the network interface created for the host.</summary>
    public string NetworkInterfaceName { get; init; }

    /// <summary>The address space of <see cref="VirtualNetworkName"/>.</summary>
    public string VirtualNetworkAddressPrefix { get; init; } = DefaultVirtualNetworkAddressPrefix;

    /// <summary>The address range of <see cref="SubnetName"/>.</summary>
    public string SubnetAddressPrefix { get; init; } = DefaultSubnetAddressPrefix;

    /// <summary>The managed-disk tier for the VM's OS disk. Billed separately from the VM; see the pricing remarks.</summary>
    public string OsDiskStorageAccountType { get; init; } = DefaultOsDiskStorageAccountType;

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Splits an Azure image URN of the form <c>publisher:offer:sku:version</c> into the four fields ARM's
    /// <c>imageReference</c> requires.
    /// </summary>
    /// <remarks>
    /// Rejected loudly rather than defaulted: an image reference that cannot be parsed would otherwise be
    /// discovered by ARM at the <em>last</em> write in the create sequence, by which point the resource group,
    /// virtual network, public IP and NIC already exist and are already billing.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="imageRef"/> is not four non-empty colon-separated fields.</exception>
    public static (string Publisher, string Offer, string Sku, string Version) ParseImageUrn(string imageRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRef);

        var parts = imageRef.Split(':');
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"'{imageRef}' is not a valid Azure image reference. Azure names an image with a four-part URN, "
                + "'publisher:offer:sku:version' (for example "
                + "'Canonical:ubuntu-24_04-lts:server:latest'), not with a single slug as DigitalOcean does.",
                nameof(imageRef));
        }

        return (parts[0], parts[1], parts[2], parts[3]);
    }
}
