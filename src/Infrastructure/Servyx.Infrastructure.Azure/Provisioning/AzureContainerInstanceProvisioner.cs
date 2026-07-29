using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates Azure Container Instances container groups — the first
/// implementation of "shape M" (a managed container service) in this codebase, and the first adapter in it
/// that hands back a resource <em>no transport can reach</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read <c>docs/provisioning.md</c> §11 before changing anything here.</strong> That section records
/// an earlier investigation which concluded this shape could not be adapted honestly, and it was right about
/// the contracts as they stood. The blocker was never <see cref="IProvisioner"/>'s verbs — plan, refresh,
/// reconcile and create are all perfectly implementable against ARM — it was its <em>return type</em>:
/// <c>ProvisionedResource.Target</c> was a non-nullable <see cref="TargetDescriptor"/>, and a container group
/// has no truthful value for it. The two ways out available at the time were both forbidden. Fabricating a
/// transport id does not fail here; it fails later and elsewhere, as "no transport for id", after a billable
/// resource exists. Throwing for a capability reason from <c>CreateAsync</c> is ruled out by
/// <see cref="IProvisioner"/>'s own remarks. The domain now has a third answer,
/// <see cref="ResourceReachability.NoTransport"/>, and this adapter is what it exists for.
/// </para>
/// <para>
/// <strong>What this adapter therefore is, stated plainly so nobody has to infer it.</strong> It creates,
/// reads, sweeps and destroys a container group correctly and completely. It hands back a resource that
/// Servyx's control plane <em>cannot connect to</em>: no <c>IExecutionTarget</c>, no file read, no command
/// execution, no health probe over a transport. The workload is reachable only through a game-specific
/// control channel — RCON over the group's published port — which satisfies
/// <c>ControlCapability.ControlChannelWrite</c> and therefore the <c>Operate</c> tier, and nothing above it.
/// <c>Provision</c> tier requires <c>WriteComposeFile</c> and ACI has no compose file, so that ceiling is
/// permanent and is not an implementation gap. This is a deliberately degraded target, and the degradation
/// is expressed in the type system rather than in a comment.
/// </para>
/// <para>
/// <strong>Why the exec path was not attempted, since it is the obvious next question.</strong> ACI does
/// expose <c>POST .../containers/{c}/exec</c>, and it returns a WebSocket URI and a password — but no exit
/// code exists anywhere in that surface, the socket carries one TTY stream so there is no way to separate
/// stderr, and the API takes a single command <em>string</em> handed to a shell where <c>CommandSpec</c>
/// carries verbatim argv specifically as the defence against injection by definition authors. An
/// <c>IExecutionTarget</c> over it would have to fabricate <c>CommandResult.ExitCode</c> on every call and
/// re-quote argv into a shell string. Mounting Azure Files fixes the storage half of the problem and moves
/// the exec half not at all; the two are independent axes. See §11.2.
/// </para>
/// <para>
/// <strong>Persistent storage is mandatory and is enforced by the type, not by validation.</strong>
/// <see cref="AzureContainerGroupSpec"/> takes an <see cref="AzureFileShareMount"/> as a required
/// constructor argument, so a spec describing an unmounted container group cannot be built. A container
/// group's writable layer dies with the group and ACI restarts groups on its own schedule, so an unmounted
/// game server loses its saves without any Servyx operation having occurred. The storage account key this
/// requires is discussed on <see cref="AzureFileShareMount"/>: it is held as a
/// <see cref="SecretUrn"/> and resolved through <see cref="ISecretStore"/> at the moment the ARM body is
/// built, exactly as <c>AzureArmApiClient</c> already does with the service principal's client secret.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request at all — not the ARM
/// call, not the OAuth2 token exchange — and resolves no secret, neither the client secret nor the storage
/// account key. A plan is pure computation over the request, including its cost figure, which is why
/// <see cref="AzureContainerInstancePricing"/> is a static snapshot.
/// </para>
/// <para>
/// <strong>What <see cref="ReconcileAsync"/> finds, and what it structurally cannot.</strong> The sweep asks
/// ARM for every resource in the subscription carrying <c>servyx.managed=true</c> and keeps the container
/// groups. Because a container group is <em>one</em> ARM resource rather than five, and because it is
/// written with its tags in the same call that creates it, there is no window in which a billing container
/// group exists that this sweep could not find — the strongest form of the guarantee described on
/// <see cref="ProvisioningCapabilities.TagQuery"/>.
/// </para>
/// <para>
/// It cannot find two things, and no tagging discipline in this adapter can make it:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>The storage account.</strong> This is §11.4's finding and it is worse in kind than the VM
/// adapter's blind spots, which are either free (a resource group) or die with their tagged parent (a
/// subnet). The account is separately billable, holds the customer's save data, and <em>must</em> outlive
/// the container group by design. Servyx does not create it, so Servyx does not tag it, so a sweep — which
/// enumerates resources <em>by</em> tag — cannot see it. The partial mitigation is real but bounded: the
/// container group carries <see cref="ServyxAzureTags.StorageAccountTag"/> and
/// <see cref="ServyxAzureTags.FileShareTag"/>, so while the group exists a sweep can name the account it
/// depends on. Once the group is destroyed that pointer is destroyed with it, and the account carries on
/// billing with nothing in Azure or in Servyx attributing the charge. That is the honest end state and it is
/// not fixable from inside this adapter.
/// </description></item>
/// <item><description>
/// <strong>The file share.</strong> A sub-resource of the storage account with no tags collection, exactly
/// as an ARM subnet is of a virtual network — and unlike a subnet, it does not die with anything Servyx
/// created.
/// </description></item>
/// </list>
/// <para>
/// <strong>The sweep is narrowed to container groups, unlike the VM adapter's.</strong> That adapter returns
/// every managed resource type because it created every one of them — the NIC and the public IP address are
/// its own orphans. This adapter creates exactly one resource, so anything else carrying
/// <c>servyx.managed=true</c> in the same subscription belongs to a different provisioner. Returning it here
/// would produce a <see cref="ResourceHandle"/> naming the wrong <see cref="ResourceHandle.ProvisionerId"/>,
/// and a sweep's output is a delete list.
/// </para>
/// <para>
/// <strong>No resource group is ever created, and that is a real improvement over the VM adapter rather than
/// an omission.</strong> The VM adapter upserts its resource group and then refuses to delete it, leaving a
/// documented tidiness leak. Here a resource group is already a precondition: the mandatory Azure Files
/// share lives in a storage account, which lives in a resource group, and the operator had to create all
/// three before this adapter could be configured at all. If the named group does not exist the single ARM
/// PUT fails before anything billable has been created — the cheapest possible failure.
/// </para>
/// </remarks>
public sealed class AzureContainerInstanceProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "azure-container-instance";

    /// <summary>The ARM resource provider for container instances.</summary>
    internal const string ContainerInstanceProvider = "Microsoft.ContainerInstance";

    /// <summary>The ARM resource type this adapter creates.</summary>
    internal const string ContainerGroupType = "containerGroups";

    /// <summary>
    /// Why no transport can reach a container group. Stamped onto every
    /// <see cref="ResourceReachability.NoTransport"/> this adapter produces.
    /// </summary>
    /// <remarks>
    /// Written for the operator who is looking at a resource Servyx created and will not connect to, and
    /// whose first conclusion will otherwise be that something is broken. Nothing is broken. The text names
    /// the three transports that exist, says why each is inapplicable, and names the one thing that
    /// <em>does</em> work, because a reason that leaves the reader with no next step is barely better than a
    /// null.
    /// </remarks>
    public const string UnreachableReason =
        "An Azure Container Instances container group exposes no Docker Engine endpoint, runs no sshd, and is "
        + "not the Servyx host, so none of Servyx's transports ('docker', 'ssh', 'local') can address it. ACI's "
        + "own exec API cannot close the gap either: it returns only a WebSocket URI and a password, reports no "
        + "exit code anywhere, carries a single TTY stream so stdout and stderr are indistinguishable, and takes "
        + "a shell command string rather than verbatim argv. Reach the workload through its game control channel "
        + "(RCON on a published port) instead. That satisfies ControlChannelWrite and therefore the Operate "
        + "tier; the Provision tier needs a compose file, which ACI does not have, so it is permanently out of "
        + "reach for this shape.";

    private const string PlanCostUnavailable =
        "No positive vCPU/memory allocation was named in the provisioning request, so no list price could be "
        + "computed.";

    private readonly AzureArmApiClient _api;
    private readonly ISecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;

    /// <summary>
    /// Creates a provisioner acting on <paramref name="subscriptionId"/> as
    /// <paramref name="servicePrincipal"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every call, to both <c>login.microsoftonline.com</c> and
    /// <c>management.azure.com</c>. Tests substitute its <see cref="HttpMessageHandler"/>, so no network
    /// access is required or attempted.
    /// </param>
    /// <param name="secretStore">
    /// Where the Azure client secret <em>and</em> the Azure Files storage account key are resolved from,
    /// freshly, at the point of use. Held here as well as inside <see cref="AzureArmApiClient"/> because this
    /// adapter has a second credential of its own — the storage account key — which the ARM client knows
    /// nothing about.
    /// </param>
    /// <param name="servicePrincipal">The tenant, client id, and client-secret URN to authenticate with. Only the URN is held.</param>
    /// <param name="subscriptionId">The Azure subscription every container group is created in.</param>
    /// <param name="timeProvider">Clock used for plan expiry, token expiry, and provisioning polls.</param>
    /// <param name="pollInterval">How long to wait between provisioning/address/deletion polls. Defaults to five seconds.</param>
    /// <param name="pollAttempts">How many polls to make before giving up. Defaults to 60.</param>
    /// <param name="armBaseAddress">Override for the ARM root. Defaults to <c>https://management.azure.com/</c>.</param>
    /// <param name="loginBaseAddress">Override for the token-service root. Defaults to <c>https://login.microsoftonline.com/</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="secretStore"/> is null.</exception>
    public AzureContainerInstanceProvisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        AzureServicePrincipal servicePrincipal,
        string subscriptionId,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        int pollAttempts = 60,
        Uri? armBaseAddress = null,
        Uri? loginBaseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentOutOfRangeException.ThrowIfLessThan(pollAttempts, 1);

        _secretStore = secretStore;
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
    /// <para>
    /// <strong>Four bits, and the absences are the interesting part.</strong>
    /// <see cref="ProvisioningCapabilities.Create"/> and <see cref="ProvisioningCapabilities.Destroy"/> are
    /// implemented outright. <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: a
    /// container group bills for every second it runs and the default restart policy keeps it running, so an
    /// orphan that cannot be found by tag bills indefinitely. It is the registry-backed form — the group's
    /// tags are written by the same call that creates it — but read it with the type remarks, which name the
    /// storage account the sweep can never attribute. <see cref="ProvisioningCapabilities.EstimatesCost"/> is
    /// present because ACI's two per-second meters can be priced exactly from a published rate; the figure
    /// says in its own <see cref="CostEstimate.Source"/> that it is compute-only.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> is absent and the absence is specifically
    /// important here: ACI's own documentation warns that a container group's public IP may change when the
    /// group restarts. The address this adapter reports is the address the group has right now, and a caller
    /// pinning an RCON client to it must expect it to move. A <c>dnsNameLabel</c> mitigates that and is
    /// offered, but a DNS name is not a static address and claiming the bit for it would be a lie about
    /// something an operator would build automation on.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/> and
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> are absent because none is implemented. The last
    /// deserves a word, because a container group publishing ports could be mistaken for having them: ACI
    /// attaches no network security group to a public-IP container group and offers no source-address filter
    /// at all, so a requested source CIDR is described in the plan as NOT APPLIED rather than quietly
    /// dropped. A caller who believed a port was restricted to their own address when it was open to the
    /// internet would expose a server it thought was firewalled.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/>, <see cref="ProvisioningCapabilities.RecreateToUpdate"/>
    /// and <see cref="ProvisioningCapabilities.DetectDrift"/> are absent because this type does not implement
    /// <see cref="IMaintainer"/> at all. That is honest rather than lazy: the two update bits say update
    /// <em>planning</em> exists, and it does not here.
    /// </para>
    /// <para>
    /// <strong>None of these bits says anything about reachability, and none of them should be read as
    /// doing so.</strong> That question is answered by the
    /// <see cref="ResourceReachability.NoTransport"/> this adapter returns from every create and refresh.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the container-group spec from <paramref name="request"/>'s parameters and
    /// describes the stages needed to realise it. Issues no HTTP request whatsoever — including no OAuth2
    /// token exchange — and resolves no secret, neither the Azure client secret nor the Azure Files storage
    /// account key. It therefore cannot create, change, or bill for anything.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(BuildPlan(BuildSpec(request)));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the
    /// spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    /// <remarks>
    /// The stage list is where this shape's real character becomes visible to whoever approves the plan, and
    /// two of the stages exist purely to say something uncomfortable out loud: that the storage account is
    /// billed separately and is not Servyx's to clean up, and that what comes back at the end is a resource
    /// nothing in Servyx can connect to. Compressing either into "create the container" would make this look
    /// like the Docker adapter on screen while hiding exactly the difference a reviewer needs.
    /// </remarks>
    public ProvisioningPlan BuildPlan(AzureContainerGroupSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var tags = ServyxAzureTags.Validate(TagsFor(spec));

        // Formatted invariantly up front so the stage descriptions below are plain string concatenation. An
        // interpolated string built by concatenation is no longer an interpolation handler expression, so it
        // cannot be handed to string.Create(IFormatProvider, ...) - the culture has to be applied here.
        var cpuText = spec.Cpu.ToString(CultureInfo.InvariantCulture);
        var memoryText = spec.MemoryInGb.ToString(CultureInfo.InvariantCulture);
        var tagCountText = tags.Count.ToString(CultureInfo.InvariantCulture);
        var portCountText = spec.Ports.Count.ToString(CultureInfo.InvariantCulture);

        var stages = new List<ProvisioningStage>
        {
            new(
                "require-azure-files-share",
                Id,
                $"REQUIRES (does not create): Azure Files share '{spec.Mount.ShareName}' in storage account "
                + $"'{spec.Mount.StorageAccountName}', mounted at '{spec.Mount.MountPath}'. The account key is "
                + $"read from the secret store at '{spec.Mount.StorageAccountKeyUrn.Value}' when the container "
                + "group is created; no key is held in this plan. THE STORAGE ACCOUNT IS BILLED SEPARATELY, "
                + "has a lifetime independent of the container group, holds the save data, and is NEVER "
                + "created, modified or destroyed by Servyx - including when the container group is "
                + "destroyed."),
            new(
                "require-resource-group",
                Id,
                $"REQUIRES (does not create): resource group '{spec.ResourceGroup}' in location "
                + $"'{spec.Region}'. Unlike the Azure VM provisioner, this adapter creates no resource group, so "
                + "it can leave none behind; if the group is absent the single ARM write below fails before "
                + "anything billable exists."),
            new(
                "create-container-group",
                Id,
                $"Create container group '{spec.ContainerGroupName}' ({cpuText} vCPU, {memoryText} GB) "
                + $"running image '{spec.Image}' with restart policy '{spec.RestartPolicy}', tagged with "
                + $"{tagCountText} Servyx tag(s), publishing {portCountText} port(s) "
                + $"({DescribePorts(spec)}) on a public address, and mounting the Azure Files share above. "
                + "BILLABLE per second on vCPU and memory for as long as the group runs, which the restart "
                + "policy is designed to make indefinite."),
            new(
                "await-public-address",
                Id,
                $"Poll container group '{spec.ContainerGroupName}' until Azure reports an allocated public IP "
                + "address for it. No change is made to any resource by this stage. Note that ACI does not "
                + "guarantee this address is stable: it may change when the group restarts."),
            new(
                "handoff-unreachable",
                Id,
                "Hand back the container group WITH NO TRANSPORT TARGET. " + UnreachableReason),
        };

        var restricted = spec.Ports.Where(p => !string.IsNullOrWhiteSpace(p.SourceCidr)).ToList();
        if (restricted.Count > 0)
        {
            // Stated as a stage rather than silently dropped, exactly as the VM adapter states its unapplied
            // ingress. The direction of the error matters: here the port IS published and the restriction is
            // NOT, so a caller who assumed otherwise has an open port they believe is closed.
            stages.Add(new ProvisioningStage(
                "ingress-source-not-applied",
                Id,
                $"PARTIALLY APPLIED: {restricted.Count.ToString(CultureInfo.InvariantCulture)} port(s) named a "
                + $"source CIDR ({DescribeRestrictions(restricted)}). "
                + "The port IS published; the source restriction is NOT. Azure Container Instances attaches no "
                + "network security group to a public-IP container group and offers no source-address filter, "
                + "so these ports are open to the internet. Restrict them outside Servyx or do not publish "
                + "them."));
        }

        var planHash = ComputePlanHash(spec, tags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.ContainerGroupName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: spec.Cpu <= 0m || spec.MemoryInGb <= 0m
                ? CostEstimate.Unknown(PlanCostUnavailable + " " + AzureContainerInstancePricing.Source)
                : AzureContainerInstancePricing.For(spec.Cpu, spec.MemoryInGb),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the container group back from ARM by resource id — the registry-backed shape, so the answer
    /// reflects the group as Azure currently describes it. A group Azure no longer has (HTTP 404), a handle
    /// that does not name a container group, or a group whose tags no longer identify it as Servyx-managed
    /// all yield <see langword="null"/>.
    /// </para>
    /// <para>
    /// <strong>One round trip, where the VM adapter needs three</strong>, because a container group carries
    /// its own address rather than a reference to a NIC that references a public IP resource.
    /// </para>
    /// <para>
    /// <strong>A group with no address yet is not an error here, and that is a real divergence from the VM
    /// adapter.</strong> There, a machine with no address means no SSH endpoint can be described, so the
    /// method throws rather than let a caller read "still allocating" as "gone". Here there is no descriptor
    /// to describe either way: the resource is unreachable by construction, so a missing address is simply a
    /// <see cref="ResourceFacts.PublicAddress"/> of <see langword="null"/>. It does still matter — it is the
    /// address an RCON client would use — but it is a fact, not a contract.
    /// </para>
    /// </remarks>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!IsContainerGroupId(handle.ProviderResourceId))
        {
            return null;
        }

        var group = await _api.GetResourceAsync<ArmContainerGroup>(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        var tags = ServyxAzureTags.FromArmTags(group.Tags);
        var identity = ServyxAzureTags.FromTags(tags);
        if (identity is null)
        {
            return null;
        }

        return new ProvisionedResource(
            Handle: HandleFor(group.Id ?? handle.ProviderResourceId, group.Location, tags),
            ConnectorId: identity.ConnectorId,
            Reachability: new ResourceReachability.NoTransport(UnreachableReason),
            Facts: BuildFacts(group));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive. Asks ARM for every resource in the subscription carrying
    /// <c>servyx.managed=true</c>, independent of any Servyx-local record, then keeps the container groups.
    /// The tag filter is sent to ARM <em>and</em> re-applied to every resource in the response — the same
    /// two-step every other adapter performs, for the same reason: the filter is the provider's promise, the
    /// second check is this process's own guarantee, and a sweep acting on a false positive destroys someone
    /// else's workload.
    /// </para>
    /// <para>
    /// <strong>Returns only container groups.</strong> See the type remarks: this adapter creates exactly one
    /// resource, so a managed resource of any other type in the same subscription belongs to a different
    /// provisioner, and reporting it here would hand back a handle claiming the wrong provisioner id.
    /// </para>
    /// <para>
    /// <strong>Only <see cref="OrphanScope.ProviderWide"/> is served</strong>, and a scope naming another
    /// provisioner or another search-space shape is declined with no handles and no API call — not even a
    /// token exchange.
    /// </para>
    /// <para>
    /// <strong>What it cannot find is stated on the type and is not repeated as a caveat here</strong>, but
    /// the short version is: the storage account and its file share, permanently.
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

        var handles = new List<ResourceHandle>();

        foreach (var resource in resources)
        {
            if (resource.Id is null || !IsContainerGroupType(resource.Type))
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

            handles.Add(HandleFor(resource.Id, resource.Location, tags));
        }

        return handles
            .OrderBy(h => h.ProviderResourceId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns the mutating operation that creates the container group described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Calling this creates nothing on its own, makes no billable API call, performs no token exchange, and
    /// resolves no secret — including the storage account key, which is read only inside
    /// <c>CreateAsync</c>.
    /// </remarks>
    public IProvisioningOperation CreateOperation(AzureContainerGroupSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ContainerGroupCreateOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above, via the same <see cref="BuildSpec"/>
    /// <see cref="PlanAsync"/> uses, so a plan preview and the operation that later realises it are always
    /// derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys a container group this provisioner created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One ARM delete, polled until the resource actually 404s. There is no sequence to walk: a container
    /// group is a single resource, so the multi-step teardown the VM adapter needs — and every partial-failure
    /// state that comes with it — does not exist here.
    /// </para>
    /// <para>
    /// <strong>The mounted Azure Files share and its storage account are never touched.</strong> Not as a
    /// safety margin — as the point. They hold the customer's save data, they were not created by Servyx, and
    /// destroying a workload must never destroy the data it wrote (see the remarks on
    /// <see cref="ProvisioningCapabilities.Destroy"/>). The cost of that refusal is stated plainly on this
    /// type: the storage account carries on billing afterwards and no sweep will attribute it.
    /// </para>
    /// <para>
    /// <strong>A handle naming anything other than a container group deletes nothing.</strong> The VM adapter
    /// deletes whatever resource type its handle names, because its own sweep legitimately returns four
    /// types. This adapter's sweep returns exactly one, so a handle naming something else did not come from
    /// here, and deleting it would be this adapter destroying a resource it could not have created.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if the container group was destroyed; <see langword="false"/> if it was already
    /// gone, or if the handle does not name a container group.
    /// </returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!IsContainerGroupId(handle.ProviderResourceId))
        {
            return false;
        }

        return await _api.DeleteResourceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into a container-group spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the container group's name at the provider.</description></item>
    /// <item><description><c>resourceGroup</c>, <c>region</c> — required. Both must already exist; neither is created.</description></item>
    /// <item><description><c>image</c> — required, an OCI image reference, e.g. <c>docker.io/thijsvanloef/palworld-server-docker:latest</c>. Not a four-part Azure image URN: a container group runs a container, not a machine image.</description></item>
    /// <item><description><c>storageAccount</c>, <c>fileShare</c>, <c>storageAccountKeyUrn</c>, <c>mountPath</c> — <strong>all required</strong>. There is no way to ask this adapter for an unmounted container group, because a container group's writable layer does not survive a restart.</description></item>
    /// <item><description><c>cpu</c>, <c>memory</c> — the two per-second billing meters. Default to <see cref="AzureContainerGroupSpec.DefaultCpu"/> and <see cref="AzureContainerGroupSpec.DefaultMemoryInGb"/>.</description></item>
    /// <item><description><c>containerName</c>, <c>restartPolicy</c>, <c>dnsNameLabel</c>, <c>mountReadOnly</c> — override the defaults.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. The port is published; a CIDR is reported as NOT applied.</description></item>
    /// <item><description><c>env:&lt;name&gt;</c> — a plain environment variable. Never a credential; see <see cref="AzureContainerGroupSpec.Environment"/>.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// <para>
    /// There is deliberately no <c>storageAccountKey</c> key, and there never will be: a caller able to pass
    /// the key inline would be a caller able to put it in a configuration file, which is the whole thing the
    /// URN rule exists to prevent. Only the locator is accepted.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or a value is not usable.</exception>
    public static AzureContainerGroupSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxAzureTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var ports = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                ports.Add(ParseIngress(pair.Key["ingress:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("env:", StringComparison.Ordinal))
            {
                environment[pair.Key["env:".Length..]] = pair.Value;
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var groupName = Required(parameters, "name");

        var mount = new AzureFileShareMount(
            Required(parameters, "storageAccount"),
            Required(parameters, "fileShare"),
            ParseUrn(parameters, "storageAccountKeyUrn"),
            Required(parameters, "mountPath"));

        var spec = new AzureContainerGroupSpec(
            groupName,
            Required(parameters, "resourceGroup"),
            Required(parameters, "region"),
            Required(parameters, "image"),
            mount,
            tags);

        return spec with
        {
            ContainerName = Optional(parameters, "containerName") ?? spec.ContainerName,
            RestartPolicy = Optional(parameters, "restartPolicy") ?? spec.RestartPolicy,
            DnsNameLabel = Optional(parameters, "dnsNameLabel"),
            MountReadOnly = ParseBool(parameters, "mountReadOnly"),
            Cpu = ParseDecimal(parameters, "cpu") ?? spec.Cpu,
            MemoryInGb = ParseDecimal(parameters, "memory") ?? spec.MemoryInGb,
            Ports = ports
                .OrderBy(p => p.Port)
                .ThenBy(p => p.Protocol, StringComparer.Ordinal)
                .ToList(),
            Environment = environment,
            AdditionalTags = extraTags,
        };
    }

    /// <summary>
    /// The full Servyx tag dictionary for a spec: caller extras and this adapter's bookkeeping keys first,
    /// canonical keys last, so an extra can never shadow <c>servyx.managed</c> or an identity key.
    /// </summary>
    /// <remarks>
    /// The storage account and share names are recorded here for the reason given on
    /// <see cref="ServyxAzureTags.StorageAccountTag"/>: while the container group exists, a sweep that finds
    /// it can name the separately-billed account it depends on. This does not make the account sweepable and
    /// is not claimed to.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> TagsFor(AzureContainerGroupSpec spec)
    {
        var extras = new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
        {
            [ServyxAzureTags.RoleTag] = ServyxAzureTags.RoleContainerGroup,
            [ServyxAzureTags.ResourceGroupTag] = spec.ResourceGroup,
            [ServyxAzureTags.StorageAccountTag] = spec.Mount.StorageAccountName,
            [ServyxAzureTags.FileShareTag] = spec.Mount.ShareName,
        };

        return spec.Tags.ToTags(extras);
    }

    private ResourceHandle HandleFor(string resourceId, string? location, IReadOnlyDictionary<string, string> tags) =>
        new(Id, resourceId, location, tags);

    /// <summary>
    /// The facts a container group reports about itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ResourceFacts.PrivateAddress"/> is always <see langword="null"/>. A public-IP container
    /// group is not on a virtual network Servyx can reach, so there is no private address that would mean
    /// anything to a caller — and inventing one would be exactly the class of fabrication this whole adapter
    /// exists to avoid.
    /// </para>
    /// <para>
    /// <see cref="ResourceFacts.CreatedAt"/> is <see cref="DateTimeOffset.UnixEpoch"/> because ACI reports no
    /// creation timestamp for a container group. A container's <c>instanceView.currentState.startTime</c>
    /// looks like one and is not: it moves every time the group restarts, so reading it as a creation time
    /// would mean a group reporting that it was created this morning when it has been billing for a month.
    /// The same sentinel the VM adapter uses when ARM omits <c>timeCreated</c> is used here rather than a
    /// value derived from something that is not the fact being asked for.
    /// </para>
    /// </remarks>
    private static ResourceFacts BuildFacts(ArmContainerGroup group)
    {
        var requests = group.Properties?.Containers?
            .FirstOrDefault()?.Properties?.Resources?.Requests;

        return new ResourceFacts(
            PublicAddress: group.Properties?.IpAddress?.Ip,
            PrivateAddress: null,
            Cost: AzureContainerInstancePricing.For(
                (decimal)(requests?.Cpu ?? 0d),
                (decimal)(requests?.MemoryInGb ?? 0d)),
            CreatedAt: DateTimeOffset.UnixEpoch);
    }

    /// <summary>Whether <paramref name="resourceId"/> names a <c>Microsoft.ContainerInstance/containerGroups</c> resource.</summary>
    private static bool IsContainerGroupId(string? resourceId) =>
        resourceId is not null
        && resourceId.Contains(
            "/providers/" + ContainerInstanceProvider + "/" + ContainerGroupType + "/",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an ARM listing row's <c>type</c> is a container group.</summary>
    private static bool IsContainerGroupType(string? resourceType) =>
        string.Equals(
            resourceType,
            ContainerInstanceProvider + "/" + ContainerGroupType,
            StringComparison.OrdinalIgnoreCase);

    private static string DescribePorts(AzureContainerGroupSpec spec) =>
        spec.Ports.Count == 0
            ? "none"
            : string.Join(", ", spec.Ports.Select(p => Describe(p, includeSource: false)));

    private static string DescribeRestrictions(IEnumerable<FirewallRule> rules) =>
        string.Join(", ", rules.Select(r => Describe(r, includeSource: true)));

    private static string Describe(FirewallRule rule, bool includeSource) =>
        includeSource
            ? string.Create(CultureInfo.InvariantCulture, $"{rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}")
            : string.Create(CultureInfo.InvariantCulture, $"{rule.Protocol}/{rule.Port}");

    /// <summary>
    /// The plan hash. Covers the storage account key's <em>locator</em> and never its value — there is no
    /// value to cover, since nothing resolves the key before <c>CreateAsync</c>.
    /// </summary>
    private string ComputePlanHash(AzureContainerGroupSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.ContainerGroupName).Append('\n');
        builder.Append(spec.ContainerName).Append('\n');
        builder.Append(spec.ResourceGroup).Append('\n');
        builder.Append(spec.Region).Append('\n');
        builder.Append(spec.Image).Append('\n');
        builder.Append(spec.RestartPolicy).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{spec.Cpu}\n");
        builder.Append(CultureInfo.InvariantCulture, $"{spec.MemoryInGb}\n");
        builder.Append(spec.DnsNameLabel ?? string.Empty).Append('\n');
        builder.Append(spec.Mount.StorageAccountName).Append('\n');
        builder.Append(spec.Mount.ShareName).Append('\n');
        builder.Append(spec.Mount.StorageAccountKeyUrn.Value).Append('\n');
        builder.Append(spec.Mount.MountPath).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{spec.MountReadOnly}\n");

        foreach (var port in spec.Ports)
        {
            builder.Append(CultureInfo.InvariantCulture, $"port {port.Protocol}/{port.Port} from {port.SourceCidr ?? "any"}\n");
        }

        foreach (var variable in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {variable.Key}={variable.Value}\n");
        }

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
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

    private static SecretUrn ParseUrn(IReadOnlyDictionary<string, string> parameters, string key)
    {
        var configured = Required(parameters, key);

        if (!SecretUrn.TryParse(configured, out var urn))
        {
            throw new ArgumentException(
                $"Provisioning parameter '{key}' is not a well-formed secret URN. It must be a locator of the "
                + "form secret://{scope}/{scopeId}/{category}/{name} - never the storage account key itself. "
                + "Servyx resolves it through ISecretStore at the moment the ARM body is built.",
                nameof(parameters));
        }

        return urn;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> parameters, string key) =>
        Optional(parameters, key) is { } raw && bool.TryParse(raw, out var value) && value;

    private static decimal? ParseDecimal(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (Optional(parameters, key) is not { } raw)
        {
            return null;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException(
                $"Provisioning parameter '{key}' must be a decimal number (e.g. '1.5'), but was '{raw}'.",
                nameof(parameters));
        }

        return value;
    }

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside the
    /// provisioner so it — and only it — can reach the API client and secret store the provisioner is
    /// configured with.
    /// </summary>
    private sealed class ContainerGroupCreateOperation : IProvisioningOperation
    {
        private readonly AzureContainerInstanceProvisioner _owner;
        private readonly AzureContainerGroupSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;

        internal ContainerGroupCreateOperation(AzureContainerInstanceProvisioner owner, AzureContainerGroupSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in
            // order to commit them to the write-ahead ledger - so they must be the same values that later
            // reach the provider. Validated here too, so a tag ARM would reject fails before the ledger row
            // is written.
            _tags = ServyxAzureTags.Validate(TagsFor(spec));
        }

        public string ProvisionerId => Id;

        public string? Region => _spec.Region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Creates the container group and hands it back as a resource no transport can reach.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>One write.</strong> The container, its published ports, its public address and its
        /// persistent Azure Files mount are all created by a single ARM PUT, so there is no interval in which
        /// some of the deployment exists and some does not. This is the structural half of the reason this
        /// adapter has so much less orphan surface than the VM one.
        /// </para>
        /// <para>
        /// <strong>The storage account key is resolved here and only here.</strong> The lease is opened
        /// immediately before the request body is built and disposed — and therefore zeroed — before the
        /// request is sent. The key reaches the ARM body and nothing else: not the tags, not the handle, not
        /// the facts, not the plan hash, and not a log, since this assembly references no logging package.
        /// This is the same discipline <c>AzureArmApiClient.ExchangeTokenAsync</c> applies to the service
        /// principal's client secret one layer up.
        /// </para>
        /// <para>
        /// <strong>What this method does not do</strong>: it creates no resource group, no storage account
        /// and no file share; it runs no command in the container and opens no connection to it. It does not
        /// know what game is inside the image.
        /// </para>
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;
            var groupId = api.ResourceId(
                _spec.ResourceGroup,
                ContainerInstanceProvider,
                ContainerGroupType,
                _spec.ContainerGroupName);

            var body = await BuildRequestAsync(ct).ConfigureAwait(false);

            var (group, _) = await api
                .PutResourceAsync<ArmContainerGroup>(groupId, body, ct)
                .ConfigureAwait(false);

            var address = await WaitForPublicAddressAsync(groupId, group, ct).ConfigureAwait(false);

            return new ProvisionedResource(
                Handle: _owner.HandleFor(
                    group.Id ?? groupId,
                    group.Location ?? _spec.Region,
                    ServyxAzureTags.FromArmTags(group.Tags)),
                ConnectorId: _spec.Tags.ConnectorId,
                // The whole point of this adapter. There is no transport id that would be true here, so none
                // is named - and the reason travels with the resource so whoever sees it knows nothing broke.
                Reachability: new ResourceReachability.NoTransport(UnreachableReason),
                Facts: new ResourceFacts(
                    PublicAddress: address,
                    PrivateAddress: null,
                    Cost: AzureContainerInstancePricing.For(_spec.Cpu, _spec.MemoryInGb),
                    CreatedAt: DateTimeOffset.UnixEpoch));
        }

        /// <summary>
        /// Attempts to undo a failed <see cref="CreateAsync"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Asks before it deletes.</strong> The group is read back first and destroyed only if it
        /// exists <em>and</em> carries this operation's own <c>servyx.instance-id</c>. That is not defensive
        /// padding: an ARM PUT is an upsert, so a name collision with a pre-existing container group would
        /// have <em>updated</em> someone else's workload rather than created a new one, and blindly deleting
        /// the name in the spec would then destroy infrastructure Servyx never made.
        /// </para>
        /// <para>
        /// <strong>Never touches the storage account or the file share</strong>, under any circumstance,
        /// including when the create failed after the mount was attached. They hold the customer's data and
        /// Servyx did not create them.
        /// </para>
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;
            var groupId = api.ResourceId(
                _spec.ResourceGroup,
                ContainerInstanceProvider,
                ContainerGroupType,
                _spec.ContainerGroupName);

            var existing = await api.GetResourceAsync<ArmResourceSummary>(groupId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return;
            }

            var tags = ServyxAzureTags.FromArmTags(existing.Tags);
            if (!ServyxAzureTags.IsManaged(tags)
                || !tags.TryGetValue(ServyxTagKeys.InstanceId, out var instanceId)
                || !string.Equals(instanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
            {
                return;
            }

            await api.DeleteResourceAsync(groupId, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the ARM body, resolving the storage account key at the last possible moment.
        /// </summary>
        private async Task<ArmContainerGroupRequest> BuildRequestAsync(CancellationToken ct)
        {
            var ports = _spec.Ports
                .Select(p => new ArmContainerPort { Port = p.Port, Protocol = p.Protocol.ToUpperInvariant() })
                .ToList();

            using var lease = await _owner._secretStore
                .GetAsync(_spec.Mount.StorageAccountKeyUrn, ct)
                .ConfigureAwait(false);

            if (lease is null)
            {
                throw new InvalidOperationException(
                    $"No Azure Files storage account key is stored at '{_spec.Mount.StorageAccountKeyUrn.Value}'. "
                    + "Store the key for storage account "
                    + $"'{_spec.Mount.StorageAccountName}' there before provisioning; the key is never read from "
                    + "configuration or the environment, and this adapter will not create a container group "
                    + "without the persistent mount it authenticates.");
            }

            return new ArmContainerGroupRequest
            {
                Location = _spec.Region,
                Tags = _tags,
                Properties = new ArmContainerGroupRequestProperties
                {
                    OsType = AzureContainerGroupSpec.LinuxOsType,
                    RestartPolicy = _spec.RestartPolicy,
                    Containers =
                    [
                        new ArmContainerRequest
                        {
                            Name = _spec.ContainerName,
                            Properties = new ArmContainerRequestProperties
                            {
                                Image = _spec.Image,
                                Resources = new ArmContainerResourcesRequest
                                {
                                    Requests = new ArmResourceRequests
                                    {
                                        Cpu = (double)_spec.Cpu,
                                        MemoryInGb = (double)_spec.MemoryInGb,
                                    },
                                },
                                Ports = ports,
                                EnvironmentVariables = _spec.Environment
                                    .OrderBy(e => e.Key, StringComparer.Ordinal)
                                    .Select(e => new ArmEnvironmentVariable { Name = e.Key, Value = e.Value })
                                    .ToList(),
                                VolumeMounts =
                                [
                                    new ArmVolumeMount
                                    {
                                        Name = AzureFileShareMount.VolumeName,
                                        MountPath = _spec.Mount.MountPath,
                                        ReadOnly = _spec.MountReadOnly,
                                    },
                                ],
                            },
                        },
                    ],
                    Volumes =
                    [
                        new ArmVolumeRequest
                        {
                            Name = AzureFileShareMount.VolumeName,
                            AzureFile = new ArmAzureFileVolumeRequest
                            {
                                ShareName = _spec.Mount.ShareName,
                                StorageAccountName = _spec.Mount.StorageAccountName,
                                // The one materialisation, taken as late as SecretLease's own remarks require.
                                StorageAccountKey = lease.ToUtf8String(),
                                ReadOnly = _spec.MountReadOnly,
                            },
                        },
                    ],
                    IpAddress = new ArmContainerGroupIpAddressRequest
                    {
                        Type = "Public",
                        Ports = ports,
                        DnsNameLabel = _spec.DnsNameLabel,
                    },
                },
            };
        }

        /// <summary>
        /// Returns the group's public address, polling until ACI has allocated one.
        /// </summary>
        /// <remarks>
        /// Unlike the VM adapter's equivalent this is not needed to build a descriptor — there is no
        /// descriptor. It is needed because the address is the <em>only</em> way to reach the workload at
        /// all: an RCON client has nowhere to connect without it. A group that never reports one is therefore
        /// surfaced as a failure so the executor compensates, rather than handing back a running, billing,
        /// completely unaddressable deployment.
        /// </remarks>
        private async Task<string> WaitForPublicAddressAsync(
            string groupId,
            ArmContainerGroup created,
            CancellationToken ct)
        {
            if (created.Properties?.IpAddress?.Ip is { Length: > 0 } immediate)
            {
                return immediate;
            }

            var api = _owner._api;

            for (var attempt = 0; attempt < _owner._pollAttempts; attempt++)
            {
                await Task.Delay(_owner._pollInterval, _owner._timeProvider, ct).ConfigureAwait(false);

                var group = await api.GetResourceAsync<ArmContainerGroup>(groupId, ct).ConfigureAwait(false);
                if (group?.Properties?.IpAddress?.Ip is { Length: > 0 } address)
                {
                    return address;
                }
            }

            throw new AzureApiException(
                System.Net.HttpStatusCode.Accepted,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Container group '{groupId}' did not report an allocated public IP address within {_owner._pollAttempts} poll(s). The group exists and is billing per second, and without an address nothing - not even RCON - can reach the workload; compensation will destroy it."));
        }
    }
}
