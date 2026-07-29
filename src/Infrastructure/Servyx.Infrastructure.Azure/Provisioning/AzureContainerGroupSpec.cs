using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// The persistent Azure Files share a container group mounts, and the only place its storage account key is
/// named — as a <see cref="SecretUrn"/>, never as a value.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mandatory by construction, because an unmounted container group is a data-loss machine.</strong>
/// A container group's writable layer dies with the group, and ACI restarts a group on its own schedule
/// (node maintenance, image pull failure, an <c>Always</c> restart policy doing its job). A game server
/// running with no mount would therefore lose every save without any Servyx operation having been performed.
/// <c>AzureContainerGroupSpec</c> takes this type as a required constructor argument, so "provision without
/// persistent storage" is not a configuration mistake an operator can make — it is a spec that cannot be
/// built.
/// </para>
/// <para>
/// <strong>The storage account key, and the credentials-by-URN rule.</strong> ACI does not support managed
/// identity for SMB mounts, so mounting Azure Files requires the account key inside the ARM request body —
/// which <c>docs/provisioning.md</c> §11.3 recorded as conflicting with Servyx's credential discipline. It
/// does not, once the rule is read precisely. The rule is that a credential is <em>held</em> only as a
/// locator and resolved through <see cref="ISecretStore"/> at the point of use, as late as possible; it is
/// not that a credential never appears on a wire. Servyx's own Azure authentication already does exactly
/// this: <c>AzureArmApiClient</c> holds the service principal's client-secret URN and puts the resolved
/// secret into an <c>application/x-www-form-urlencoded</c> body on every token exchange. This type is the
/// same shape one layer down. What matters is what is <em>durable</em>, and the key reaches nothing durable:
/// not this spec, not the ARM tags, not the <see cref="ResourceHandle"/>, not the plan or its hash, not the
/// ledger row, and not a log — this assembly references no logging package at all.
/// </para>
/// <para>
/// <strong>The share is not Servyx's to create or destroy.</strong> It must already exist. This adapter
/// issues no <c>Microsoft.Storage</c> call of any kind: it does not create the account, does not rotate the
/// key, does not read the share, and — most importantly — never deletes either. Deleting a save directory is
/// not a provisioning verb (see <see cref="ProvisioningCapabilities.Destroy"/>), and here the same rule has
/// a second, colder edge: the share must outlive the container group, because the whole point of mounting it
/// is that the workload is disposable and the data is not.
/// </para>
/// </remarks>
public sealed record AzureFileShareMount
{
    /// <summary>The name the volume is given inside the container group's ARM body.</summary>
    public const string VolumeName = "servyx-data";

    /// <summary>Creates a mount description.</summary>
    /// <param name="storageAccountName">The Azure storage account holding the file share. Must already exist.</param>
    /// <param name="shareName">The Azure Files share mounted into the container. Must already exist.</param>
    /// <param name="storageAccountKeyUrn">
    /// Where the storage account key lives in the secret store. A locator only — the key itself is resolved
    /// at the moment the ARM body is built and is never held here.
    /// </param>
    /// <param name="mountPath">The absolute path the share is mounted at inside the container.</param>
    /// <exception cref="ArgumentException">
    /// A name is blank, <paramref name="mountPath"/> is not an absolute POSIX path, or
    /// <paramref name="storageAccountKeyUrn"/> is a <c>default(SecretUrn)</c> rather than a real locator.
    /// </exception>
    public AzureFileShareMount(
        string storageAccountName,
        string shareName,
        SecretUrn storageAccountKeyUrn,
        string mountPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(shareName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPath);

        // default(SecretUrn) is constructible because SecretUrn is a struct - that type's own remarks say so
        // and say a default instance must never be treated as valid. Caught here rather than at the ARM call,
        // because by then the caller has already approved a plan.
        if (storageAccountKeyUrn.Value is null)
        {
            throw new ArgumentException(
                "A default(SecretUrn) is not a secret locator. Build the storage account key's URN with "
                + "SecretUrn.Create or SecretUrn.TryParse; an Azure Container Instances group cannot mount "
                + "Azure Files without a key, and this adapter will not provision one without persistent "
                + "storage.",
                nameof(storageAccountKeyUrn));
        }

        if (!mountPath.StartsWith('/'))
        {
            throw new ArgumentException(
                $"'{mountPath}' is not an absolute path. A container mount path must begin with '/'.",
                nameof(mountPath));
        }

        StorageAccountName = storageAccountName;
        ShareName = shareName;
        StorageAccountKeyUrn = storageAccountKeyUrn;
        MountPath = mountPath;
    }

    /// <summary>The storage account holding the share. Never created or destroyed by this adapter.</summary>
    public string StorageAccountName { get; }

    /// <summary>The Azure Files share mounted into the container.</summary>
    public string ShareName { get; }

    /// <summary>Where the storage account key lives. A locator; the key itself is never held on this type.</summary>
    public SecretUrn StorageAccountKeyUrn { get; }

    /// <summary>The absolute path the share is mounted at inside the container.</summary>
    public string MountPath { get; }
}

/// <summary>
/// Everything needed to create one Azure Container Instances container group.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this does not wrap <see cref="MachineSpec"/>, unlike every shape-I spec in this codebase.</strong>
/// <see cref="MachineSpec"/> describes a machine: an image URN to boot, a size to boot it at, an SSH public
/// key to authorise, and optional cloud-init to run on first boot. A container group has none of those
/// concepts. Its image is an OCI reference, not a <c>publisher:offer:sku:version</c> URN; its "size" is two
/// independent continuous numbers (vCPU and GB) rather than a named catalogue entry; it has no SSH key
/// because it has no sshd, and no cloud-init because it has no init. Wrapping <see cref="MachineSpec"/> here
/// would mean carrying four fields of which three are structurally inapplicable, which would misrepresent
/// shape M as a variant of shape I. It is not one — that is the finding <c>docs/provisioning.md</c> §11
/// records.
/// </para>
/// <para>
/// <strong>What it shares with the shape-I specs.</strong> The Servyx identity is a
/// <see cref="ServyxAzureTags"/> that cannot be constructed incompletely, extra tags are additive and can
/// never shadow a canonical key, and nothing on this type is a credential value.
/// </para>
/// </remarks>
public sealed record AzureContainerGroupSpec
{
    /// <summary>The default vCPU allocation, in cores. ACI bills per vCPU-second.</summary>
    public const decimal DefaultCpu = 2m;

    /// <summary>The default memory allocation, in GB. ACI bills per GB-second.</summary>
    public const decimal DefaultMemoryInGb = 4m;

    /// <summary>The only OS type this adapter provisions.</summary>
    public const string LinuxOsType = "Linux";

    /// <summary>The restart policy a long-lived game server wants.</summary>
    public const string DefaultRestartPolicy = "Always";

    /// <summary>Creates a spec.</summary>
    /// <param name="containerGroupName">The container group's name at the provider.</param>
    /// <param name="resourceGroup">
    /// The ARM resource group the container group is created in. <strong>Must already exist</strong> — see
    /// <c>AzureContainerInstanceProvisioner</c>'s remarks on why this adapter creates no resource group.
    /// </param>
    /// <param name="region">The ARM location the container group is created in.</param>
    /// <param name="image">The OCI image reference the container runs, e.g. <c>docker.io/library/nginx:1.27</c>.</param>
    /// <param name="mount">The mandatory persistent Azure Files mount.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException">A required string is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mount"/> or <paramref name="tags"/> is null.</exception>
    public AzureContainerGroupSpec(
        string containerGroupName,
        string resourceGroup,
        string region,
        string image,
        AzureFileShareMount mount,
        ServyxAzureTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerGroupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(tags);

        ContainerGroupName = containerGroupName;
        ResourceGroup = resourceGroup;
        Region = region;
        Image = image;
        Mount = mount;
        Tags = tags;
        ContainerName = containerGroupName;
    }

    /// <summary>The container group's name at the provider.</summary>
    public string ContainerGroupName { get; }

    /// <summary>The ARM resource group the container group lives in. Never created by this adapter.</summary>
    public string ResourceGroup { get; }

    /// <summary>The ARM location the container group is created in.</summary>
    public string Region { get; }

    /// <summary>The OCI image reference the container runs.</summary>
    public string Image { get; }

    /// <summary>The mandatory persistent Azure Files mount. There is no way to build a spec without one.</summary>
    public AzureFileShareMount Mount { get; }

    /// <summary>The mandatory Servyx identity stamped onto the container group.</summary>
    public ServyxAzureTags Tags { get; }

    /// <summary>The single container's name within the group. Defaults to the group's own name.</summary>
    public string ContainerName { get; init; }

    /// <summary>The vCPU allocation, in cores. One of the two meters ACI bills per second.</summary>
    public decimal Cpu { get; init; } = DefaultCpu;

    /// <summary>The memory allocation, in GB. The other of the two meters ACI bills per second.</summary>
    public decimal MemoryInGb { get; init; } = DefaultMemoryInGb;

    /// <summary>The container group's restart policy.</summary>
    public string RestartPolicy { get; init; } = DefaultRestartPolicy;

    /// <summary>
    /// The ports published on the group's public address.
    /// </summary>
    /// <remarks>
    /// A <see cref="FirewallRule"/> is reused as the domain's existing shape for "port, protocol, source",
    /// but only two thirds of it can be honoured: ACI attaches no network security group to a public-IP
    /// container group and offers no source-address filter at all. A rule carrying a
    /// <see cref="FirewallRule.SourceCidr"/> therefore has its port published to the whole internet, and the
    /// plan says so as an explicit NOT-APPLIED stage rather than dropping the restriction quietly.
    /// </remarks>
    public IReadOnlyList<FirewallRule> Ports { get; init; } = [];

    /// <summary>
    /// Environment variables handed to the container.
    /// </summary>
    /// <remarks>
    /// Plain values only, and deliberately so: ACI has a <c>secureValue</c> member, but a value written into
    /// this dictionary would have had to be a literal in a caller's configuration to get here, which is the
    /// thing the URN rule exists to prevent. Anything secret belongs behind a <see cref="SecretUrn"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The DNS label for the group's public address, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Worth setting: ACI's documentation warns a container group's public IP may change when the group
    /// restarts, and a label gives a name that survives that where the address does not. It is still not a
    /// static address, which is why <see cref="ProvisioningCapabilities.StaticAddress"/> is not claimed.
    /// </remarks>
    public string? DnsNameLabel { get; init; }

    /// <summary>Whether the mounted share is read-only. Almost never true for a game server's data directory.</summary>
    public bool MountReadOnly { get; init; }

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
