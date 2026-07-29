using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates Azure virtual machines — and nothing else. The second
/// implementation of "shape I" in this codebase, and therefore the test of whether shape I's clouds really do
/// "differ trivially".
/// </summary>
/// <remarks>
/// <para>
/// <strong>The headline: this is not a mechanical repeat of the DigitalOcean adapter.</strong> The shape claim
/// survives — what comes out of here is a <see cref="TargetDescriptor"/> whose
/// <see cref="TargetDescriptor.TransportId"/> is <c>"ssh"</c>, there is no install logic, and shape H consumes
/// it unchanged. But three things underneath it are structurally different, not cosmetically:
/// </para>
/// <list type="number">
/// <item><description>
/// <strong>Authentication is an exchange, not a header.</strong> DigitalOcean's stored secret <em>is</em> the
/// bearer token. Azure's stored client secret cannot be sent to ARM at all; it buys a short-lived access token
/// from a second service on a second host first. That means one extra HTTP round trip before any provisioning
/// call, a second class of authentication failure (a bad tenant is a 400 from the token service, not a 401
/// from ARM), and — the part that matters to Servyx's secret discipline — a derived credential cached in
/// memory, where the DigitalOcean adapter caches nothing at all. See
/// <c>AzureArmApiClient</c>'s remarks, which state that trade-off rather than hiding it.
/// </description></item>
/// <item><description>
/// <strong>A host is five resources, not one.</strong> A droplet is one <c>POST</c>. An Azure host is a
/// resource group, a virtual network (with an inline subnet), a public IP address, a network interface, and
/// finally the VM that references the NIC — each PUT waiting for the previous one to reach
/// <c>provisioningState: "Succeeded"</c> before the next can legally reference it. Teardown walks the same
/// list backwards, and every step of it is a place a failure can leave something billing. This is the
/// difference that most breaks "one MachineSpec mapping plus a price table".
/// </description></item>
/// <item><description>
/// <strong>Tagging needs no encoding at all.</strong> The one place Azure is materially <em>simpler</em>. See
/// <see cref="ServyxAzureTags"/>.
/// </description></item>
/// </list>
/// <para>
/// <strong>Shape I produces a host, not a game server.</strong> Visible, as in the DigitalOcean adapter, in
/// what this type does not contain: no SteamCMD invocation, no package manager, no archive extraction, no game
/// definition, no shell script, no cloud-init authoring — no install step of any kind. The adapter creates a
/// machine, waits for it to have an address, and hands back an SSH target. From there the existing SSH
/// host-install adapter (shape H) installs onto it exactly as it would onto a bare-metal box a human had
/// racked.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request at all — not even the
/// token exchange. A plan is pure computation over the request, including its cost figure, which is why
/// <see cref="AzureVirtualMachinePricing"/> is a static snapshot rather than a Retail Prices API call. There is
/// no request to audit, and no way for a plan to spend money or to resolve the client secret.
/// </para>
/// <para>
/// <strong>What <see cref="ReconcileAsync"/> does and does not clean up — stated precisely, because
/// overstating it would be the most expensive mistake in this file.</strong> The sweep asks ARM for every
/// resource in the subscription carrying <c>servyx.managed=true</c>, across every resource type. So it
/// <em>does</em> find, individually and independently of any local record:
/// </para>
/// <list type="bullet">
/// <item><description>virtual machines;</description></item>
/// <item><description>network interfaces — including one left behind when the VM write failed after it;</description></item>
/// <item><description>public IP addresses — the second billable resource here, and the one most likely to be orphaned, since it is created two steps before the VM;</description></item>
/// <item><description>virtual networks.</description></item>
/// </list>
/// <para>
/// It does <strong>not</strong> find three things, and no ordering or tagging discipline in this adapter can
/// make it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Resource groups.</strong> ARM's <c>/subscriptions/{id}/resources</c> endpoint lists resources
/// <em>within</em> groups and never the groups themselves, so a group Servyx created is structurally invisible
/// to this sweep. An empty Servyx-created resource group can therefore be left behind indefinitely. Resource
/// groups are free, so this is a tidiness leak and not a billing leak — but it is a leak, and
/// <see cref="DestroyAsync"/> deliberately never deletes one, because deleting a resource group is recursive
/// and would destroy resources Servyx never created if the group was pre-existing or shared.
/// </description></item>
/// <item><description>
/// <strong>Subnets.</strong> An ARM sub-resource with no tags collection. It has no independent lifetime and
/// dies with its (tagged, sweepable) virtual network, so this is a gap in discovery rather than in cleanup.
/// </description></item>
/// <item><description>
/// <strong>The managed OS disk.</strong> Created implicitly by the VM write rather than PUT by Servyx, so
/// there is no request in which Servyx could tag it and no way for a sweep to attribute it. It is handled by
/// declaring <c>deleteOption: "Delete"</c> at create time so it dies with the VM. The residual risk is real
/// and is named here: if a VM write fails <em>after</em> ARM has materialised the disk, an untagged,
/// per-GB-billing disk can survive with nothing pointing at it. Nothing in this adapter can find that disk
/// afterwards.
/// </description></item>
/// </list>
/// <para>
/// <strong>Host-key trust: Azure is genuinely better than DigitalOcean here, and this adapter still does not
/// exploit it.</strong> <c>docs/provisioning.md</c> §6 asserts a freshly created VM's host key is "captured at
/// creation from the provider API or console output and pinned". For DigitalOcean that is aspirational — the
/// v2 API has no console-output endpoint at all. Azure does: with boot diagnostics enabled, ARM exposes
/// <c>POST .../virtualMachines/{name}/retrieveBootDiagnosticsData</c>, which returns a time-limited URL to the
/// serial console log, and on stock Linux images cloud-init prints the sshd host-key fingerprints into that
/// log between <c>-----BEGIN SSH HOST KEY FINGERPRINTS-----</c> markers during first boot. So the material
/// exists, is machine-readable, and is reachable over the same authenticated ARM channel this adapter already
/// uses. What this adapter does about it is nothing, on purpose: capturing it means enabling boot diagnostics
/// (which requires a storage account or the managed equivalent, a sixth resource with its own lifetime and its
/// own orphan story), polling an eventually-consistent log for a marker that a hardened or non-cloud-init
/// image may never print, and parsing free text into a security decision. That is a feature with a design of
/// its own, not a line in an adapter. Until it is built, this adapter is no better off in practice than the
/// DigitalOcean one: it stamps no <c>trustPolicy</c>, invents no bypass, and passes the caller's transport
/// options through to the SSH transport's existing host-key mechanism unchanged. An unattended
/// create-then-install pipeline will still stop at the SSH handshake unless the fingerprint was obtained out of
/// band and fed back through the <c>pinnedFingerprints</c> transport option this adapter forwards untouched.
/// </para>
/// <para>
/// <strong>Capabilities are what is implemented, not what Azure offers.</strong> ARM can resize VMs, snapshot
/// disks, attach reserved addresses and manage network security groups; this adapter calls none of those, so
/// <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
/// <see cref="ProvisioningCapabilities.StaticAddress"/> and
/// <see cref="ProvisioningCapabilities.FirewallRules"/> are all absent. In particular a
/// <see cref="MachineSpec.Ingress"/> rule is <em>described in the plan as not applied</em> rather than quietly
/// ignored, because a caller who believed a port had been opened when nothing had would expose a server it
/// thought was firewalled. Note the sharper edge of that here than on DigitalOcean: a new Azure VM created
/// without a network security group is governed by ARM's default rules, which deny inbound traffic from the
/// internet other than what the platform allows — so a game port a caller asked for is not merely un-opened,
/// it is actively closed.
/// </para>
/// <para>
/// <strong>Maintenance is preview-only.</strong> This type also implements <see cref="IMaintainer"/> — in
/// <c>AzureVirtualMachineProvisioner.Maintenance.cs</c>, whose remarks carry the full reasoning. Both members
/// there read the live machine and produce a description: an <see cref="UpdatePlan"/> or a
/// <see cref="DriftResult"/>. Neither issues a mutating request; there is no resize call, no VM replacement
/// sequence and no delete on that path, and this solution has no executor that applies an
/// <see cref="UpdatePlan"/>. The distinction is sharper here than for a container: a plan that replaces this
/// VM is a plan that deletes its managed OS disk, by the <c>deleteOption</c> the create sequence declared.
/// </para>
/// </remarks>
public sealed partial class AzureVirtualMachineProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "azure-vm";

    /// <summary>
    /// The username produced endpoints authenticate as when the caller names none.
    /// </summary>
    /// <remarks>
    /// Not <c>root</c>, and that is not a style preference: ARM rejects a VM whose
    /// <c>osProfile.adminUsername</c> is <c>root</c> (along with a list of other reserved names), so the
    /// DigitalOcean adapter's default does not carry over. <c>azureuser</c> is the name Azure's own tooling
    /// uses.
    /// </remarks>
    public const string DefaultSshUsername = "azureuser";

    /// <summary>The port the produced <see cref="TargetDescriptor"/> endpoint names.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this
    /// provisioner produces — the <em>existing</em> SSH transport, not an Azure-specific one.
    /// </summary>
    /// <remarks>
    /// Kept as a constant here (this project cannot reference <c>Servyx.Infrastructure.Ssh</c>: infrastructure
    /// projects reference <c>Servyx.Domain</c> and nothing else) and asserted equal to
    /// <c>SshTransport.TransportId</c> by the composition test, so drift is caught by a test rather than by a
    /// runtime "no transport for id" failure.
    /// </remarks>
    internal const string SshTransportId = "ssh";

    /// <summary>
    /// The root path stamped on every descriptor this provisioner produces.
    /// </summary>
    /// <remarks>
    /// Always <c>/</c>: shape I hands back a <em>host</em>, so there is no per-server data directory for it to
    /// record. A game's <c>dataDir</c> is chosen by the install stage that runs afterwards.
    /// </remarks>
    public const string HostRootPath = "/";

    /// <summary>The ARM resource provider for compute resources.</summary>
    internal const string ComputeProvider = "Microsoft.Compute";

    /// <summary>The ARM resource provider for network resources.</summary>
    internal const string NetworkProvider = "Microsoft.Network";

    /// <summary>
    /// Usernames ARM refuses for <c>osProfile.adminUsername</c> on a Linux VM.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to ARM because ARM checks it on the <em>last</em> write in the create
    /// sequence: by the time it says no, the resource group, virtual network, public IP and network interface
    /// already exist, and the public IP is already billing. A username fixed at construction can be rejected at
    /// construction instead.
    /// </remarks>
    private static readonly string[] ReservedAdminUsernames =
    [
        "root", "admin", "administrator", "user", "test", "guest", "adm", "actuser", "console", "1",
    ];

    private const string PlanCostUnavailable =
        "No VM size was named in the provisioning request, so no list price could be looked up.";

    private readonly AzureArmApiClient _api;
    private readonly string? _sshCredentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly string _sshUsername;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;

    /// <summary>
    /// Creates a provisioner acting on <paramref name="subscriptionId"/> as
    /// <paramref name="servicePrincipal"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every call, to both <c>login.microsoftonline.com</c> and
    /// <c>management.azure.com</c>. Tests substitute its <see cref="HttpMessageHandler"/>, so no network access
    /// is required or attempted. Note the divergence from the DigitalOcean adapter: one client, two hosts,
    /// because authentication and provisioning live at different services.
    /// </param>
    /// <param name="secretStore">Where the client secret is resolved from, freshly, on every token exchange.</param>
    /// <param name="servicePrincipal">The tenant, client id, and client-secret URN to authenticate with. Only the URN is held.</param>
    /// <param name="subscriptionId">The Azure subscription every resource is created in.</param>
    /// <param name="sshCredentialUrn">
    /// The <see cref="TargetDescriptor.CredentialUrn"/> to stamp on produced descriptors — the URN of the SSH
    /// private key matching the public key the VM boots with. Never a literal credential, and never the Azure
    /// client secret.
    /// </param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/> the SSH transport reads. Applied before Servyx-owned
    /// option keys, so they can never override one. This adapter adds nothing of its own to the host-key
    /// question — see the type remarks.
    /// </param>
    /// <param name="sshUsername">
    /// The username produced endpoints authenticate as, and the VM's <c>adminUsername</c>. Fixed at
    /// construction rather than per-request for the same reason the DigitalOcean adapter fixes its:
    /// <see cref="RefreshAsync"/> must be able to rebuild an identical descriptor without depending on the
    /// request that created the machine.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry, token expiry, and provisioning polls.</param>
    /// <param name="pollInterval">How long to wait between provisioning/deletion polls. Defaults to five seconds.</param>
    /// <param name="pollAttempts">How many polls to make before giving up. Defaults to 60.</param>
    /// <param name="armBaseAddress">Override for the ARM root. Defaults to <c>https://management.azure.com/</c>.</param>
    /// <param name="loginBaseAddress">Override for the token-service root. Defaults to <c>https://login.microsoftonline.com/</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="sshUsername"/> is blank or is a name ARM reserves.</exception>
    public AzureVirtualMachineProvisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        AzureServicePrincipal servicePrincipal,
        string subscriptionId,
        string? sshCredentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string sshUsername = DefaultSshUsername,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        int pollAttempts = 60,
        Uri? armBaseAddress = null,
        Uri? loginBaseAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sshUsername);
        ArgumentOutOfRangeException.ThrowIfLessThan(pollAttempts, 1);

        if (ReservedAdminUsernames.Contains(sshUsername, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Azure refuses '{sshUsername}' as a Linux VM adminUsername; the reserved names include "
                + $"{string.Join(", ", ReservedAdminUsernames)}. Note that this is a real divergence from the "
                + $"DigitalOcean adapter, whose default username is 'root'. Use '{DefaultSshUsername}' or another "
                + "non-reserved name.",
                nameof(sshUsername));
        }

        _sshCredentialUrn = sshCredentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _sshUsername = sshUsername;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _pollAttempts = pollAttempts;

        _api = new AzureArmApiClient(
            httpClient,
            secretStore,
            servicePrincipal,
            subscriptionId,
            _timeProvider,
            _pollInterval,
            _pollAttempts,
            armBaseAddress,
            loginBaseAddress);
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: VMs and public IP addresses
    /// bill by the hour, so an orphan that cannot be found by tag bills forever. It is the registry-backed form
    /// of the capability — every resource this adapter PUTs carries its tags in the same write that creates it,
    /// so there is no window in which a created resource exists untagged. Read it together with the type
    /// remarks, which name the three object kinds the sweep cannot see at all.
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is present because
    /// <see cref="AzureVirtualMachinePricing"/> carries real published list prices; it answers
    /// <see cref="CostEstimate.Unknown"/> for any size it does not know rather than approximating, and it says
    /// in its own <see cref="CostEstimate.Source"/> that the figure is compute-only.
    /// <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> and
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> are all deliberately absent — see the type remarks.
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> deserves a word, since this adapter <em>does</em>
    /// create a <c>Static</c>-allocation public IP: the bit means the adapter can allocate and attach a static
    /// address as an operation on an existing resource, which it cannot. Creating one as part of a new host is
    /// not the same promise.
    /// <para>
    /// <strong>The three maintenance bits, and what each one means here.</strong>
    /// <see cref="ProvisioningCapabilities.DetectDrift"/> is the registry-backed form: the comparison reads
    /// the machine back from ARM, so a "matches" answer is about the VM as Azure describes it right now.
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/> is claimed because a size change really is a
    /// mutation of the existing resource — an ARM write that changes
    /// <c>properties.hardwareProfile.vmSize</c> leaves the VM's ARM id, its network interface, its address
    /// and its managed OS disk exactly as they were — and so is a tag change.
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> is claimed because an image change is not:
    /// <c>properties.storageProfile.imageReference</c> is fixed when the machine is created, so reaching a
    /// different image means deleting this VM and creating another, with the interruption and the data
    /// consequence that implies.
    /// </para>
    /// <para>
    /// This is the first adapter in the codebase to hold both update bits, and the pair is a real fact about
    /// ARM rather than hedging: the container adapter holds only
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> because an engine offers no property edits at
    /// all, the SSH adapter holds only <see cref="ProvisioningCapabilities.UpdateInPlace"/> because a process
    /// install has no provider identity to discard, and a cloud VM genuinely has both shapes depending on
    /// which property is being changed. Which one a given request gets is answered per plan by
    /// <see cref="PlannedChange.RequiresRecreate"/>, never by the bits.
    /// </para>
    /// <para>
    /// None of the three implies execution, and what this type does execute is stated by the interfaces it
    /// implements rather than by these bits: <see cref="IUpdateApplier"/> carries out a lone size change (see
    /// <c>AzureVirtualMachineProvisioner.Resize.cs</c>) and <see cref="IDestructiveUpdateApplier"/> carries out
    /// a lone image change, behind two independent approvals (see
    /// <c>AzureVirtualMachineProvisioner.Replace.cs</c>). A tag write is still not implemented at all, and a
    /// region or resource-group change still is not an operation this or any adapter can perform.
    /// <see cref="ProvisioningCapabilities.Resize"/> nevertheless stays absent, because it means something
    /// narrower than "can execute a resize": it is the bit a caller reads to learn that the adapter offers
    /// resizing as a first-class operation with its own verb, and here a resize is reachable only as the
    /// execution of an approved <see cref="UpdatePlan"/>. <see cref="ProvisioningCapabilities.Snapshot"/> is
    /// absent and load-bearing in the other direction: no snapshot is taken before a replacement deletes the
    /// machine's OS disk, because this adapter cannot take one.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost
        | ProvisioningCapabilities.UpdateInPlace
        | ProvisioningCapabilities.RecreateToUpdate
        | ProvisioningCapabilities.DetectDrift;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the VM spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Issues no HTTP request whatsoever — including no OAuth2 token exchange — so
    /// it cannot create, change, or bill for anything, and cannot resolve, let alone transmit, the client
    /// secret.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var spec = BuildSpec(request);
        return Task.FromResult(BuildPlan(spec));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the
    /// spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    /// <remarks>
    /// The stage list is where the multi-resource reality becomes visible to whoever approves the plan. The
    /// DigitalOcean equivalent has three stages; this has seven, and each of the extra four is an object that
    /// will exist, may bill, and can be orphaned. Compressing them into one "create the machine" line would
    /// make the two adapters look alike on screen while hiding exactly the difference a reviewer needs.
    /// </remarks>
    public ProvisioningPlan BuildPlan(AzureVirtualMachineSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var tags = ServyxAzureTags.Validate(TagsFor(spec));

        var stages = new List<ProvisioningStage>
        {
            new(
                "create-resource-group",
                Id,
                $"Ensure resource group '{spec.ResourceGroup}' exists in location '{spec.Machine.Region}', tagged "
                + $"with {tags.Count} Servyx tag(s). If it already exists it is left in place and NOT deleted by any "
                + "later teardown, because deleting a resource group is recursive and it may hold resources Servyx "
                + "did not create."),
            new(
                "create-virtual-network",
                Id,
                $"Create virtual network '{spec.VirtualNetworkName}' ({spec.VirtualNetworkAddressPrefix}) with inline "
                + $"subnet '{spec.SubnetName}' ({spec.SubnetAddressPrefix}). The network is tagged; the subnet cannot "
                + "be - ARM sub-resources carry no tags - so it is discoverable only through its parent."),
            new(
                "create-public-ip",
                Id,
                $"Create Standard-SKU static public IPv4 address '{spec.PublicIpName}', tagged. BILLABLE, and billed "
                + "whether or not the VM is running; it is created two steps before the VM, so it is the resource "
                + "most likely to be left orphaned by a partial failure."),
            new(
                "create-network-interface",
                Id,
                $"Create network interface '{spec.NetworkInterfaceName}', tagged, bound to subnet '{spec.SubnetName}' "
                + $"and public address '{spec.PublicIpName}'."),
            new(
                "create-virtual-machine",
                Id,
                $"Create virtual machine '{spec.VmName}' at size '{spec.Machine.SizeRef}' from image "
                + $"'{spec.Machine.ImageRef}', tagged, referencing network interface '{spec.NetworkInterfaceName}', "
                + $"with password authentication disabled and one authorised SSH public key for '{_sshUsername}', "
                + $"and an OS disk ({spec.OsDiskStorageAccountType}) declared to delete with the VM. "
                + (string.IsNullOrEmpty(spec.Machine.CloudInit)
                    ? "No customData (this provisioner never authors cloud-init; nothing is installed on the machine here)."
                    : $"{spec.Machine.CloudInit.Length} character(s) of caller-supplied cloud-init, base64-encoded and forwarded unchanged.")),
            new(
                "await-public-address",
                Id,
                $"Poll public IP address '{spec.PublicIpName}' until Azure reports an allocated IPv4 address for it. "
                + "No change is made to any resource by this stage."),
            new(
                "handoff-ssh-target",
                Id,
                $"Hand back an 'ssh://{_sshUsername}@<address>:{SshPort}' target descriptor for the new host. "
                + "Provisioning stops here: installing a game server onto this host is a separate stage run by the "
                + "SSH host-install provisioner, identically to any bare-metal SSH box."),
        };

        if (spec.Machine.Ingress.Count > 0)
        {
            // Stated as a stage rather than silently skipped: this provisioner does not advertise
            // ProvisioningCapabilities.FirewallRules, and a caller who believed a port had been opened when
            // nothing had would expose a server it thought was firewalled.
            stages.Add(new ProvisioningStage(
                "ingress-not-applied",
                Id,
                $"NOT APPLIED: {spec.Machine.Ingress.Count} inbound rule(s) were requested "
                + $"({string.Join(", ", spec.Machine.Ingress.Select(r => $"{r.Protocol}/{r.Port} from {r.SourceCidr ?? "any"}"))}), "
                + "but this provisioner does not implement FirewallRules and will not create an Azure network "
                + "security group. Note that a VM created without one is governed by ARM's default rules, which "
                + "deny inbound internet traffic - so these ports are closed, not merely unconfigured. Apply them "
                + "separately."));
        }

        var planHash = ComputePlanHash(spec, tags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.VmName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: string.IsNullOrWhiteSpace(spec.Machine.SizeRef)
                ? CostEstimate.Unknown(PlanCostUnavailable + " " + AzureVirtualMachinePricing.Source)
                : AzureVirtualMachinePricing.For(spec.Machine.SizeRef),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the VM back from ARM by resource id — the registry-backed shape, so the answer reflects the VM as
    /// Azure currently describes it. A VM Azure no longer has (HTTP 404), a handle that does not name a virtual
    /// machine, or a VM whose tags no longer identify it as Servyx-managed all yield <see langword="null"/>.
    /// </para>
    /// <para>
    /// <strong>Costs three round trips where DigitalOcean costs one</strong>, and the reason is the shape: a
    /// droplet object carries its own addresses, whereas a VM carries only a reference to a NIC, which carries
    /// the private address and a reference to a public IP resource, which carries the public address. The walk
    /// is done from the VM rather than from the recorded sibling-name tags on purpose, so a refresh still works
    /// against a VM whose Servyx bookkeeping tags were edited away at the provider.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The VM exists but no IPv4 address can be resolved for it yet, so no SSH target can be described. This is
    /// a transient state, not a missing resource, and is deliberately distinguished from <see langword="null"/>
    /// — treating "still allocating" as "gone" would let a caller conclude a billing VM had disappeared.
    /// </exception>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return null;
        }

        var vm = await _api.GetResourceAsync<ArmVirtualMachine>(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (vm is null)
        {
            return null;
        }

        var tags = ServyxAzureTags.FromArmTags(vm.Tags);
        var identity = ServyxAzureTags.FromTags(tags);
        if (identity is null)
        {
            return null;
        }

        var (publicAddress, privateAddress) = await ResolveAddressesAsync(vm, ct).ConfigureAwait(false);

        return new ProvisionedResource(
            Handle: HandleFor(vm.Id ?? handle.ProviderResourceId, vm.Location, tags),
            ConnectorId: identity.ConnectorId,
            Target: BuildTargetDescriptor(RequireSshAddress(vm, publicAddress, privateAddress)),
            Facts: BuildFacts(vm, publicAddress, privateAddress));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive, and the reason this adapter may be trusted with billable resources at all.
    /// Asks ARM for every resource in the subscription carrying <c>servyx.managed=true</c>, independent of any
    /// Servyx-local record, so a resource created but never acknowledged can still be found.
    /// </para>
    /// <para>
    /// The tag filter is sent to ARM <em>and</em> re-applied to every resource in the response, the same
    /// two-step the Docker and DigitalOcean sweeps perform for the same reason: the filter is the provider's
    /// promise, the second check is this process's own guarantee that nothing untagged is ever reported as
    /// Servyx-owned and subsequently destroyed. A sweep acting on a false positive deletes someone else's
    /// virtual machine.
    /// </para>
    /// <para>
    /// <strong>Returns more than virtual machines, deliberately.</strong> A DigitalOcean sweep returns droplets
    /// because a droplet is the whole host. Here the same filter returns VMs, NICs, public IP addresses and
    /// virtual networks, because all four were tagged by the create sequence — and returning only the VMs would
    /// hide precisely the orphans this adapter is most likely to produce. The result is ordered so that
    /// dependents come before their dependencies (VM, then NIC, then public IP, then virtual network), because
    /// ARM refuses to delete a resource another resource still references, so a caller deleting in list order
    /// succeeds where one deleting in ARM's arbitrary order would not. Read the type remarks for the three
    /// object kinds this sweep cannot see at all.
    /// </para>
    /// <para>
    /// <strong>Only <see cref="OrphanScope.ProviderWide"/> is served.</strong> The provider's own inventory is
    /// the search space, so a scope describing some other search space — an
    /// <see cref="OrphanScope.MarkerDirectory"/>, say — is declined exactly as a scope naming another
    /// provisioner is: no handles, and no API call, not even a token exchange. Quietly widening a narrower
    /// request into "every managed resource in the subscription" would hand a caller more than it asked to
    /// sweep, and a sweep's output is a delete list.
    /// </para>
    /// <para>
    /// Azure is genuinely region-scoped, so <see cref="OrphanScope.Region"/> is honoured when set: a sweep
    /// restricted to <c>eastus</c> reports only <c>eastus</c> resources. Note the direction of the risk — a
    /// region filter can only ever narrow the delete list, never widen it.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope is not OrphanScope.ProviderWide || !string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        var resources = await _api
            .ListResourcesByTagAsync(ServyxAzureTags.ManagedTag, ServyxAzureTags.ManagedTagValue, ct)
            .ConfigureAwait(false);

        var handles = new List<(int Rank, ResourceHandle Handle)>();

        foreach (var resource in resources)
        {
            if (resource.Id is null)
            {
                continue;
            }

            var tags = ServyxAzureTags.FromArmTags(resource.Tags);
            if (!ServyxAzureTags.IsManaged(tags))
            {
                continue;
            }

            if (scope.Region is not null
                && !string.Equals(resource.Location, scope.Region, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            handles.Add((TeardownRank(resource.Type), HandleFor(resource.Id, resource.Location, tags)));
        }

        return handles
            .OrderBy(h => h.Rank)
            .ThenBy(h => h.Handle.ProviderResourceId, StringComparer.Ordinal)
            .Select(h => h.Handle)
            .ToList();
    }

    /// <summary>
    /// Returns the mutating operation that creates the host described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is driven by
    /// <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering. Calling this
    /// method creates nothing on its own — and, critically for a provider that bills by the hour, makes no
    /// billable API call and no token exchange.
    /// </remarks>
    public IProvisioningOperation CreateOperation(AzureVirtualMachineSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new VirtualMachineCreateOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above: builds the VM spec the same way <see cref="PlanAsync"/>
    /// does, via <see cref="BuildSpec"/>, so a plan preview and the operation that later realises it are always
    /// derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys a host this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A teardown here is a sequence, not a call, and the order is enforced by ARM.</strong> The VM
    /// goes first (its OS disk goes with it, by the <c>deleteOption</c> declared at create time), then the
    /// network interface — which ARM refuses to delete while the VM references it — then the public IP address,
    /// then the virtual network with its subnet. Each step waits for the previous one to actually complete, not
    /// merely to be accepted.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does not delete.</strong> The resource group, always, even when Servyx
    /// created it: deleting a resource group is recursive, and if the group was pre-existing or is shared it
    /// would take resources Servyx never created with it. An empty group left behind is free; a wrongly deleted
    /// one is not recoverable.
    /// </para>
    /// <para>
    /// <strong>Which siblings it can find.</strong> The sibling names are read from
    /// <see cref="ResourceHandle.Tags"/>, which is where the create operation recorded them. A handle whose tags
    /// were lost or edited names only the VM, in which case only the VM is destroyed here and the NIC, address
    /// and network are left for <see cref="ReconcileAsync"/> to find by tag — which it can, since each of them
    /// carries the canonical tags in its own right. A handle naming any other resource type destroys exactly
    /// that one resource.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> if the resource the handle names was destroyed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId))
        {
            return false;
        }

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return await _api.DeleteResourceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        }

        var destroyed = await _api.DeleteResourceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);

        foreach (var siblingId in SiblingIdsInTeardownOrder(handle.Tags))
        {
            await _api.DeleteResourceAsync(siblingId, ct).ConfigureAwait(false);
        }

        return destroyed;
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into a VM spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the VM's name at the provider and the stem its siblings are named from.</description></item>
    /// <item><description><c>resourceGroup</c> — required. Azure has no unscoped resources; every resource lives in a group, so unlike DigitalOcean there is no way to omit this.</description></item>
    /// <item><description><c>image</c> — required, a four-part <c>publisher:offer:sku:version</c> URN, e.g. <c>Canonical:ubuntu-24_04-lts:server:latest</c>.</description></item>
    /// <item><description><c>size</c>, <c>region</c> — required; e.g. <c>Standard_B2s</c>, <c>eastus</c>.</description></item>
    /// <item><description><c>sshPublicKey</c> — <strong>required</strong>, unlike the DigitalOcean adapter where it is optional and unused on the wire. ARM consumes the raw key, and this adapter disables password authentication, so a VM created without one would be unreachable.</description></item>
    /// <item><description><c>cloudInit</c> — user-data, base64-encoded and forwarded. Nothing here authors one.</description></item>
    /// <item><description><c>virtualNetwork</c>, <c>subnet</c>, <c>publicIp</c>, <c>networkInterface</c> — override the derived sibling names.</description></item>
    /// <item><description><c>vnetAddressPrefix</c>, <c>subnetAddressPrefix</c>, <c>osDiskType</c> — override the network and disk defaults.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. Recorded and reported as NOT applied; see the type remarks.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string, matching every existing adapter, so no
    /// separator can collide with a value.
    /// <para>
    /// There is deliberately no <c>credentialUrn</c>, <c>sshUsername</c> or <c>endpoint</c> key: all three are
    /// fixed at construction, because <see cref="RefreshAsync"/> must be able to rebuild an identical descriptor
    /// from resources that record none of them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or a value is not expressible as an Azure tag or image reference.</exception>
    public static AzureVirtualMachineSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxAzureTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var ingress = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                ingress.Add(ParseIngress(pair.Key["ingress:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var vmName = Required(parameters, "name");
        var imageRef = Required(parameters, "image");

        // Parsed here, before anything exists, precisely so a malformed URN is not discovered on the last write
        // of a five-resource sequence. The result is discarded; the throw is the point.
        _ = AzureVirtualMachineSpec.ParseImageUrn(imageRef);

        var machine = new MachineSpec(
            ImageRef: imageRef,
            SizeRef: Required(parameters, "size"),
            Region: Required(parameters, "region"),
            SshPublicKey: Required(parameters, "sshPublicKey"),
            CloudInit: parameters.TryGetValue("cloudInit", out var cloudInit) && !string.IsNullOrEmpty(cloudInit)
                ? cloudInit
                : null,
            Ingress: ingress
                .OrderBy(r => r.Port)
                .ThenBy(r => r.Protocol, StringComparer.Ordinal)
                .ToList(),
            Tags: tags.ToTags(extraTags));

        var spec = new AzureVirtualMachineSpec(vmName, Required(parameters, "resourceGroup"), machine, tags)
        {
            AdditionalTags = extraTags,
        };

        return spec with
        {
            VirtualNetworkName = Optional(parameters, "virtualNetwork") ?? spec.VirtualNetworkName,
            SubnetName = Optional(parameters, "subnet") ?? spec.SubnetName,
            PublicIpName = Optional(parameters, "publicIp") ?? spec.PublicIpName,
            NetworkInterfaceName = Optional(parameters, "networkInterface") ?? spec.NetworkInterfaceName,
            VirtualNetworkAddressPrefix = Optional(parameters, "vnetAddressPrefix") ?? spec.VirtualNetworkAddressPrefix,
            SubnetAddressPrefix = Optional(parameters, "subnetAddressPrefix") ?? spec.SubnetAddressPrefix,
            OsDiskStorageAccountType = Optional(parameters, "osDiskType") ?? spec.OsDiskStorageAccountType,
        };
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for a host. This is the whole hand-off, and the whole shape
    /// claim: the value returned here names the <em>existing</em> SSH transport, carries an ordinary
    /// <c>ssh://user@host:port</c> endpoint, and contains not one Azure-specific field. Nothing downstream needs
    /// to know a cloud API — let alone a five-resource ARM sequence — was ever involved.
    /// </summary>
    internal TargetDescriptor BuildTargetDescriptor(string address)
    {
        // Caller-supplied options first, Servyx-owned keys last, so an option can never shadow one - the same
        // ordering rule ServyxTagKeys.Build applies to tags, and the same one every sibling adapter applies to
        // its own descriptor options.
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            ["rootPath"] = HostRootPath,
        };

        return new TargetDescriptor(
            TransportId: SshTransportId,
            Endpoint: string.Create(CultureInfo.InvariantCulture, $"ssh://{_sshUsername}@{address}:{SshPort}"),
            CredentialUrn: _sshCredentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>
    /// The full Servyx tag dictionary for a spec: caller extras and the adapter's own bookkeeping keys first,
    /// canonical keys last.
    /// </summary>
    /// <remarks>
    /// The bookkeeping keys (<see cref="ServyxAzureTags.ResourceGroupTag"/> and the sibling names) go in as
    /// <em>extras</em>, which means <see cref="ServyxTagKeys.Build"/>'s ordering rule guarantees they can never
    /// shadow <c>servyx.managed</c> or an identity key. That matters: the sweep selects on
    /// <c>servyx.managed</c>, so a bookkeeping key that could overwrite it would be a way to hide a billing
    /// resource from reconciliation.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> TagsFor(AzureVirtualMachineSpec spec)
    {
        var extras = new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
        {
            [ServyxAzureTags.RoleTag] = ServyxAzureTags.RoleVirtualMachine,
            [ServyxAzureTags.ResourceGroupTag] = spec.ResourceGroup,
            [ServyxAzureTags.VirtualNetworkTag] = spec.VirtualNetworkName,
            [ServyxAzureTags.SubnetTag] = spec.SubnetName,
            [ServyxAzureTags.PublicIpTag] = spec.PublicIpName,
            [ServyxAzureTags.NetworkInterfaceTag] = spec.NetworkInterfaceName,
        };

        return spec.Tags.ToTags(extras);
    }

    /// <summary>The tag set stamped on one of the VM's subsidiary resources.</summary>
    /// <remarks>
    /// Every resource carries the full canonical identity, not a back-reference to the VM. That is what lets
    /// <see cref="ReconcileAsync"/> attribute a stranded public IP to a Servyx instance without the VM — which
    /// is the whole point, since the address exists two writes before the VM does and may outlive a failed
    /// create.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> SiblingTagsFor(AzureVirtualMachineSpec spec, string role)
    {
        var extras = new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
        {
            [ServyxAzureTags.RoleTag] = role,
            [ServyxAzureTags.ResourceGroupTag] = spec.ResourceGroup,
        };

        return ServyxAzureTags.Validate(spec.Tags.ToTags(extras));
    }

    private ResourceHandle HandleFor(string resourceId, string? location, IReadOnlyDictionary<string, string> tags) =>
        new(Id, resourceId, location, tags);

    private static ResourceFacts BuildFacts(ArmVirtualMachine vm, string? publicAddress, string? privateAddress) =>
        new(
            PublicAddress: publicAddress,
            PrivateAddress: privateAddress,
            Cost: AzureVirtualMachinePricing.For(vm.Properties?.HardwareProfile?.VmSize),
            CreatedAt: vm.Properties?.TimeCreated ?? DateTimeOffset.UnixEpoch);

    /// <summary>The address a descriptor names: the public IPv4 if there is one, otherwise the private one.</summary>
    private static string RequireSshAddress(ArmVirtualMachine vm, string? publicAddress, string? privateAddress) =>
        publicAddress
        ?? privateAddress
        ?? throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Virtual machine '{vm.Id}' exists (provisioningState '{vm.Properties?.ProvisioningState}') but Azure reports no IPv4 address reachable through its network interface yet, so no SSH target can be described. This is a transient state, not a missing machine - the machine is billing and must not be treated as gone."));

    /// <summary>
    /// Walks VM → network interface → public IP address to find the machine's addresses.
    /// </summary>
    /// <remarks>
    /// Three requests where a droplet needs one. Stated as a cost rather than hidden, because it is one of the
    /// concrete ways a "mechanical repeat" is not mechanical: the domain's <c>ResourceFacts.PublicAddress</c>
    /// is a single field on both providers, but on one it is read and on the other it is dereferenced twice.
    /// </remarks>
    private async Task<(string? PublicAddress, string? PrivateAddress)> ResolveAddressesAsync(
        ArmVirtualMachine vm,
        CancellationToken ct)
    {
        var nicId = vm.Properties?.NetworkProfile?.NetworkInterfaces?
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n.Id))?.Id;

        if (nicId is null)
        {
            return (null, null);
        }

        var nic = await _api.GetResourceAsync<ArmNetworkInterface>(nicId, ct).ConfigureAwait(false);
        var ipConfiguration = nic?.Properties?.IpConfigurations?.FirstOrDefault();

        var privateAddress = ipConfiguration?.Properties?.PrivateIpAddress;
        var publicIpId = ipConfiguration?.Properties?.PublicIpAddress?.Id;

        if (string.IsNullOrWhiteSpace(publicIpId))
        {
            return (null, privateAddress);
        }

        var publicIp = await _api.GetResourceAsync<ArmPublicIpAddress>(publicIpId, ct).ConfigureAwait(false);

        return (publicIp?.Properties?.IpAddress, privateAddress);
    }

    /// <summary>
    /// The ARM ids of a VM's subsidiary resources, in the order ARM will accept their deletion.
    /// </summary>
    /// <remarks>
    /// Reconstructed from the bookkeeping tags rather than stored as ids, and skipped entirely when a tag is
    /// absent — a missing tag means "this adapter does not know of such a resource", never "delete something
    /// with a guessed name".
    /// </remarks>
    private IEnumerable<string> SiblingIdsInTeardownOrder(IReadOnlyDictionary<string, string> tags)
    {
        if (!tags.TryGetValue(ServyxAzureTags.ResourceGroupTag, out var resourceGroup)
            || string.IsNullOrWhiteSpace(resourceGroup))
        {
            yield break;
        }

        if (tags.TryGetValue(ServyxAzureTags.NetworkInterfaceTag, out var nic) && !string.IsNullOrWhiteSpace(nic))
        {
            yield return _api.ResourceId(resourceGroup, NetworkProvider, "networkInterfaces", nic);
        }

        if (tags.TryGetValue(ServyxAzureTags.PublicIpTag, out var publicIp) && !string.IsNullOrWhiteSpace(publicIp))
        {
            yield return _api.ResourceId(resourceGroup, NetworkProvider, "publicIPAddresses", publicIp);
        }

        if (tags.TryGetValue(ServyxAzureTags.VirtualNetworkTag, out var vnet) && !string.IsNullOrWhiteSpace(vnet))
        {
            yield return _api.ResourceId(resourceGroup, NetworkProvider, "virtualNetworks", vnet);
        }

        // The resource group is deliberately absent from this list. See DestroyAsync's remarks.
    }

    /// <summary>Whether <paramref name="resourceId"/> names a <c>Microsoft.Compute/virtualMachines</c> resource.</summary>
    private static bool IsVirtualMachineId(string? resourceId) =>
        resourceId is not null
        && resourceId.Contains("/providers/Microsoft.Compute/virtualMachines/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sorts a swept resource into the order a teardown must delete it in: dependents before dependencies.
    /// </summary>
    private static int TeardownRank(string? resourceType) => resourceType switch
    {
        not null when resourceType.EndsWith("/virtualMachines", StringComparison.OrdinalIgnoreCase) => 0,
        not null when resourceType.EndsWith("/networkInterfaces", StringComparison.OrdinalIgnoreCase) => 1,
        not null when resourceType.EndsWith("/publicIPAddresses", StringComparison.OrdinalIgnoreCase) => 2,
        not null when resourceType.EndsWith("/virtualNetworks", StringComparison.OrdinalIgnoreCase) => 3,
        _ => 4,
    };

    private string ComputePlanHash(AzureVirtualMachineSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.VmName).Append('\n');
        builder.Append(spec.ResourceGroup).Append('\n');
        builder.Append(spec.Machine.ImageRef).Append('\n');
        builder.Append(spec.Machine.SizeRef).Append('\n');
        builder.Append(spec.Machine.Region).Append('\n');
        builder.Append(spec.Machine.SshPublicKey).Append('\n');
        builder.Append(spec.Machine.CloudInit ?? string.Empty).Append('\n');
        builder.Append(_sshUsername).Append('\n');
        builder.Append(HostRootPath).Append('\n');
        builder.Append(spec.VirtualNetworkName).Append('\n');
        builder.Append(spec.SubnetName).Append('\n');
        builder.Append(spec.PublicIpName).Append('\n');
        builder.Append(spec.NetworkInterfaceName).Append('\n');
        builder.Append(spec.VirtualNetworkAddressPrefix).Append('\n');
        builder.Append(spec.SubnetAddressPrefix).Append('\n');
        builder.Append(spec.OsDiskStorageAccountType).Append('\n');

        foreach (var rule in spec.Machine.Ingress)
        {
            builder.Append(CultureInfo.InvariantCulture, $"ingress {rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}\n");
        }

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        foreach (var option in _transportOptions.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"option {option.Key}={option.Value}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static FirewallRule ParseIngress(string portKey, string sourceCidr)
    {
        var slash = portKey.IndexOf('/', StringComparison.Ordinal);
        var portText = slash < 0 ? portKey : portKey[..slash];
        var protocol = slash < 0 ? "tcp" : portKey[(slash + 1)..];

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException(
                $"'{portKey}' is not a valid 'ingress:<number>[/<protocol>]' provisioning parameter key.",
                nameof(portKey));
        }

        return new FirewallRule(port, protocol.ToLowerInvariant(), string.IsNullOrWhiteSpace(sourceCidr) ? null : sourceCidr);
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Provisioning parameter '{key}' is required by the '{Id}' provisioner.", nameof(parameters));
        }

        return value;
    }

    private static string? Optional(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside the
    /// provisioner so it — and only it — can reach the API client the provisioner is configured with.
    /// </summary>
    private sealed class VirtualMachineCreateOperation : IProvisioningOperation
    {
        private readonly AzureVirtualMachineProvisioner _owner;
        private readonly AzureVirtualMachineSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;

        internal VirtualMachineCreateOperation(AzureVirtualMachineProvisioner owner, AzureVirtualMachineSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in order
            // to commit them to the write-ahead ledger - so they must be the same values that later reach the
            // provider, not a set recomputed later. Validated here too, so a tag ARM would reject fails before
            // the ledger row is written rather than five resources into the sequence.
            _tags = ServyxAzureTags.Validate(TagsFor(spec));
        }

        public string ProvisionerId => Id;

        public string? Region => _spec.Machine.Region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Creates the five ARM resources that make up one host, in dependency order, and hands back an SSH
        /// target for it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every resource carries the canonical Servyx tags in the same write that creates it, so — unlike the
        /// marker-file shape — there is no window in which a billing resource exists that a sweep could not
        /// find. But note the honest difference from a droplet: there are five such windows-that-are-closed
        /// rather than one, and between each pair of writes there is a real interval in which some resources
        /// exist and others do not. That state is not an error condition, it is the normal middle of this
        /// method, and it is why every resource is tagged rather than only the VM.
        /// </para>
        /// <para>
        /// Note what this method does <em>not</em> do: it runs no command on the machine, uploads nothing to
        /// it, and never opens an SSH connection at all. It does not know what game is going to be installed.
        /// </para>
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;
            var rg = _spec.ResourceGroup;

            // The resource-group PUT is an upsert, and its status code is the only signal of which it did:
            // 201 means Servyx created the group, 200 means it already existed. That signal is deliberately
            // read and then NOT acted on, because neither CompensateAsync nor DestroyAsync deletes a resource
            // group under any circumstance - the delete is recursive, and a group that already existed may
            // hold resources Servyx never created. The cost of that refusal is stated plainly on this type:
            // an empty, tagged, free resource group can be left behind, and no sweep will report it.
            await api
                .PutResourceAsync<ArmProvisioningProbe>(
                    api.ResourceGroupId(rg),
                    new ArmResourceGroupRequest
                    {
                        Location = _spec.Machine.Region,
                        Tags = SiblingTagsFor(_spec, ServyxAzureTags.RoleResourceGroup),
                    },
                    ct)
                .ConfigureAwait(false);

            var vnetId = api.ResourceId(rg, NetworkProvider, "virtualNetworks", _spec.VirtualNetworkName);
            await api
                .PutResourceAsync<ArmProvisioningProbe>(
                    vnetId,
                    new ArmVirtualNetworkRequest
                    {
                        Location = _spec.Machine.Region,
                        Tags = SiblingTagsFor(_spec, ServyxAzureTags.RoleVirtualNetwork),
                        Properties = new ArmVirtualNetworkRequestProperties
                        {
                            AddressSpace = new ArmAddressSpace { AddressPrefixes = [_spec.VirtualNetworkAddressPrefix] },
                            Subnets =
                            [
                                new ArmSubnetRequest
                                {
                                    Name = _spec.SubnetName,
                                    Properties = new ArmSubnetRequestProperties { AddressPrefix = _spec.SubnetAddressPrefix },
                                },
                            ],
                        },
                    },
                    ct)
                .ConfigureAwait(false);

            var publicIpId = api.ResourceId(rg, NetworkProvider, "publicIPAddresses", _spec.PublicIpName);
            await api
                .PutResourceAsync<ArmProvisioningProbe>(
                    publicIpId,
                    new ArmPublicIpRequest
                    {
                        Location = _spec.Machine.Region,
                        Tags = SiblingTagsFor(_spec, ServyxAzureTags.RolePublicIp),
                        Sku = new ArmSku { Name = "Standard" },
                        Properties = new ArmPublicIpRequestProperties
                        {
                            PublicIpAllocationMethod = "Static",
                            PublicIpAddressVersion = "IPv4",
                        },
                    },
                    ct)
                .ConfigureAwait(false);

            var nicId = api.ResourceId(rg, NetworkProvider, "networkInterfaces", _spec.NetworkInterfaceName);
            var (nic, _) = await api
                .PutResourceAsync<ArmNetworkInterface>(
                    nicId,
                    new ArmNetworkInterfaceRequest
                    {
                        Location = _spec.Machine.Region,
                        Tags = SiblingTagsFor(_spec, ServyxAzureTags.RoleNetworkInterface),
                        Properties = new ArmNetworkInterfaceRequestProperties
                        {
                            IpConfigurations =
                            [
                                new ArmIpConfigurationRequest
                                {
                                    Name = "ipconfig1",
                                    Properties = new ArmIpConfigurationRequestProperties
                                    {
                                        PrivateIpAllocationMethod = "Dynamic",
                                        Subnet = new ArmSubResource { Id = vnetId + "/subnets/" + _spec.SubnetName },
                                        PublicIpAddress = new ArmSubResource { Id = publicIpId },
                                    },
                                },
                            ],
                        },
                    },
                    ct)
                .ConfigureAwait(false);

            var vmId = api.ResourceId(rg, ComputeProvider, "virtualMachines", _spec.VmName);
            var (vm, _) = await api
                .PutResourceAsync<ArmVirtualMachine>(vmId, BuildVirtualMachineRequest(nicId), ct)
                .ConfigureAwait(false);

            var publicAddress = await WaitForPublicAddressAsync(publicIpId, ct).ConfigureAwait(false);
            var privateAddress = nic.Properties?.IpConfigurations?.FirstOrDefault()?.Properties?.PrivateIpAddress;

            return new ProvisionedResource(
                Handle: _owner.HandleFor(vm.Id ?? vmId, vm.Location ?? _spec.Machine.Region, ServyxAzureTags.FromArmTags(vm.Tags)),
                ConnectorId: _spec.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(RequireSshAddress(vm, publicAddress, privateAddress)),
                Facts: BuildFacts(vm, publicAddress, privateAddress));
        }

        /// <summary>
        /// Attempts to undo a failed <see cref="CreateAsync"/>, walking the five resources back down in the
        /// reverse of the order they were created.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Asks before it deletes.</strong> Each candidate is read back first and destroyed only if it
        /// exists <em>and</em> carries this operation's own <c>servyx.instance-id</c>. That check is not
        /// defensive padding: the ARM writes above are upserts, so a name collision with a pre-existing virtual
        /// network or address would have <em>updated</em> someone else's resource rather than created a new one,
        /// and blindly deleting the names in the spec would then destroy infrastructure Servyx never made. This
        /// is the same "ask the provider, do not assume" principle the DigitalOcean operation applies when its
        /// create returned no id.
        /// </para>
        /// <para>
        /// <strong>Never deletes the resource group</strong>, even when this operation created it, for the
        /// reason given on <see cref="DestroyAsync"/>: the delete is recursive. The consequence is explicit —
        /// a failed create can leave an empty, tagged, free resource group behind, and no sweep will report it.
        /// </para>
        /// <para>
        /// <strong>What it still cannot reach.</strong> If the VM write failed after ARM had materialised the
        /// managed OS disk, that disk is untagged and unattributable, so nothing here — and nothing in
        /// <see cref="ReconcileAsync"/> — can find it.
        /// </para>
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;
            var rg = _spec.ResourceGroup;

            string[] candidates =
            [
                api.ResourceId(rg, ComputeProvider, "virtualMachines", _spec.VmName),
                api.ResourceId(rg, NetworkProvider, "networkInterfaces", _spec.NetworkInterfaceName),
                api.ResourceId(rg, NetworkProvider, "publicIPAddresses", _spec.PublicIpName),
                api.ResourceId(rg, NetworkProvider, "virtualNetworks", _spec.VirtualNetworkName),
            ];

            foreach (var candidate in candidates)
            {
                var existing = await api.GetResourceAsync<ArmResourceSummary>(candidate, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    continue;
                }

                var tags = ServyxAzureTags.FromArmTags(existing.Tags);
                if (!ServyxAzureTags.IsManaged(tags)
                    || !tags.TryGetValue(ServyxTagKeys.InstanceId, out var instanceId)
                    || !string.Equals(instanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
                {
                    continue;
                }

                await api.DeleteResourceAsync(candidate, ct).ConfigureAwait(false);
            }
        }

        private ArmVirtualMachineRequest BuildVirtualMachineRequest(string nicId)
        {
            var image = AzureVirtualMachineSpec.ParseImageUrn(_spec.Machine.ImageRef);

            return new ArmVirtualMachineRequest
            {
                Location = _spec.Machine.Region,
                Tags = _tags,
                Properties = new ArmVirtualMachineRequestProperties
                {
                    HardwareProfile = new ArmHardwareProfileRequest { VmSize = _spec.Machine.SizeRef },
                    StorageProfile = new ArmStorageProfileRequest
                    {
                        ImageReference = new ArmImageReference
                        {
                            Publisher = image.Publisher,
                            Offer = image.Offer,
                            Sku = image.Sku,
                            Version = image.Version,
                        },
                        OsDisk = new ArmOsDiskRequest
                        {
                            CreateOption = "FromImage",
                            DeleteOption = "Delete",
                            ManagedDisk = new ArmManagedDiskRequest
                            {
                                StorageAccountType = _spec.OsDiskStorageAccountType,
                            },
                        },
                    },
                    OsProfile = new ArmOsProfileRequest
                    {
                        ComputerName = _spec.VmName,
                        AdminUsername = _owner._sshUsername,
                        CustomData = string.IsNullOrEmpty(_spec.Machine.CloudInit)
                            ? null
                            : Convert.ToBase64String(Encoding.UTF8.GetBytes(_spec.Machine.CloudInit)),
                        LinuxConfiguration = new ArmLinuxConfigurationRequest
                        {
                            DisablePasswordAuthentication = true,
                            Ssh = new ArmSshConfigurationRequest
                            {
                                PublicKeys =
                                [
                                    new ArmSshPublicKeyRequest
                                    {
                                        Path = $"/home/{_owner._sshUsername}/.ssh/authorized_keys",
                                        KeyData = _spec.Machine.SshPublicKey,
                                    },
                                ],
                            },
                        },
                    },
                    NetworkProfile = new ArmNetworkProfileRequest
                    {
                        NetworkInterfaces =
                        [
                            new ArmNetworkInterfaceReference
                            {
                                Id = nicId,
                                Properties = new ArmNetworkInterfaceReferenceProperties { Primary = true },
                            },
                        ],
                    },
                },
            };
        }

        private async Task<string?> WaitForPublicAddressAsync(string publicIpId, CancellationToken ct)
        {
            var api = _owner._api;

            for (var attempt = 0; attempt < _owner._pollAttempts; attempt++)
            {
                var publicIp = await api.GetResourceAsync<ArmPublicIpAddress>(publicIpId, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(publicIp?.Properties?.IpAddress))
                {
                    return publicIp.Properties.IpAddress;
                }

                await Task.Delay(_owner._pollInterval, _owner._timeProvider, ct).ConfigureAwait(false);
            }

            throw new AzureApiException(
                System.Net.HttpStatusCode.Accepted,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Public IP address '{publicIpId}' did not report an allocated IPv4 address within {_owner._pollAttempts} poll(s). The address and the virtual machine exist and are billing; compensation will destroy them."));
        }
    }
}
