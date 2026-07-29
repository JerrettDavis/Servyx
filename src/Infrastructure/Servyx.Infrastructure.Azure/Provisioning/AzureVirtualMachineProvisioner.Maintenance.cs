using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the VM adapter: it reads a live virtual machine and describes what
/// would have to happen to it. Nothing here changes anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before changing a single string in this file.</strong> The container adapter's update
/// recreates a container and its volumes survive; the SSH adapter's update re-runs the install verbs and the
/// data directory survives. Neither can delete a user's saves. Here the machine's state lives on a managed
/// disk whose lifetime this adapter deliberately bound to the machine, so what a plan does to the VM is what
/// it does to the data:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Size — in place, and the OS disk is genuinely untouched.</strong> ARM changes a VM's size with an
/// ordinary write to <c>properties.hardwareProfile.vmSize</c> on the existing resource. The claim that the
/// disk survives is not an assumption about what "resize" ought to mean: a managed OS disk is a
/// <em>separate</em> ARM resource that the VM references by id through
/// <c>properties.storageProfile.osDisk</c>, and a write that changes only the hardware profile neither names
/// that resource nor alters the reference to it. The VM keeps its ARM id, its NIC, its address and the same
/// disk object. Azure deallocates and restarts the machine to apply the new size, so the workload is
/// interrupted — but <see cref="DataImpact"/> describes persistent data and not availability, and by that
/// measure this is <see cref="DataImpact.Preserved"/>.
/// </description></item>
/// <item><description>
/// <strong>Image — a replacement, and by this adapter's own create-time choice a destructive one.</strong>
/// <c>properties.storageProfile.imageReference</c> is fixed when the VM is created; ARM has no operation
/// that reimages a VM in place, so the only route to a different image is to delete this machine and create
/// another. What that does to the data is decided by the OS disk's <c>deleteOption</c>, which this adapter
/// sets to <c>Delete</c> at create time precisely because an untagged managed disk can never be found by an
/// orphan sweep. So the machine's disk is deleted with it: <see cref="DataImpact.Destroyed"/>. The value is
/// <em>read back off the live machine</em> rather than assumed, so a VM whose delete option was changed out
/// of band is reported for what it now is.
/// </description></item>
/// <item><description>
/// <strong>Region and resource group — not updatable at all.</strong> An ARM resource's <c>location</c> is
/// immutable, and a resource group is part of a resource's identity: both appear in the VM's ARM id. Neither
/// can be changed by a write to this VM. This adapter reports the difference and plans <em>nothing</em>,
/// because the alternative — quietly describing a delete-and-recreate somewhere else as though it were a
/// move — is a plan to destroy a machine dressed up as a plan to relocate one.
/// </description></item>
/// </list>
/// <para>
/// <strong>What is in scope for a drift check here, and why it is only the VM.</strong> An Azure host is five
/// resources, and this adapter's <see cref="ReconcileAsync"/> deliberately reports four of them as separate
/// <see cref="ResourceHandle"/>s. <see cref="DetectDriftAsync"/> answers about <em>the resource the handle
/// names</em>, and nothing else: a handle naming the VM is compared against the VM. It does not walk to the
/// NIC, the public IP or the virtual network, for two reasons. First, those have handles of their own, and a
/// caller that wants them checked passes them — folding them into the VM's answer would make one
/// <see cref="DriftResult"/> describe resources its <see cref="DriftResult.Handle"/> does not name, so a
/// divergence could not be traced back to the thing it is about. Second, the walk is not free or reliable: it
/// costs two more reads and it starts from the VM's own network profile, so a VM whose NIC reference was
/// changed out of band would be checked against the wrong NIC and report a match. A handle that names one of
/// the siblings rather than the VM is answered as a divergence under <c>resource-kind</c>, not silently
/// treated as a machine.
/// </para>
/// <para>
/// <strong>Nothing here executes.</strong> The only requests this file causes are ARM reads of one resource
/// (plus the token exchange the API client performs for any call). There is no write, no delete, and no
/// executor anywhere in this solution that applies an <see cref="UpdatePlan"/>. Applying a plan that deletes
/// a cloud machine's OS disk deserves its own reviewed change; this is the half that can ship without one.
/// </para>
/// </remarks>
public sealed partial class AzureVirtualMachineProvisioner : IMaintainer
{
    /// <summary>The ARM resource type a handle must name for this maintainer to be able to answer about it.</summary>
    internal const string VirtualMachineResourceType = ComputeProvider + "/virtualMachines";

    /// <summary>The <c>deleteOption</c> that makes a VM's managed OS disk die with the VM.</summary>
    /// <remarks>The value this adapter writes at create time; see <c>ArmOsDiskRequest</c>'s remarks.</remarks>
    internal const string OsDiskDeleteOption = "Delete";

    /// <summary>The <c>deleteOption</c> that leaves the managed OS disk behind when the VM is deleted.</summary>
    internal const string OsDiskDetachOption = "Detach";

    /// <summary>How long an update plan's observed live state should be trusted for.</summary>
    private const int UpdatePlanLifetimeMinutes = 15;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads one VM, then computes.</strong> The only ARM request on this path is a GET of the
    /// resource the handle names. A machine ARM no longer has, or a handle that does not name a virtual
    /// machine, yields <see langword="null"/> — mirroring <see cref="RefreshAsync"/>, and deliberately not
    /// the same answer as "nothing needs to change".
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided, from ARM's semantics rather than from
    /// hope.</strong> A plan whose only differences are the VM size and its tags is
    /// <see cref="DataImpact.Preserved"/>, and the justification is structural: the managed OS disk is a
    /// separate ARM resource attached to the VM by id, the write that changes the size does not name it, and
    /// the machine that exists afterwards is the same machine with the same disk still attached — which is
    /// what <see cref="DataImpact.Preserved"/> requires and is stronger than "the adapter deletes nothing". A
    /// plan that changes the image must replace the VM, so its answer comes from the live machine's
    /// <c>osDisk.deleteOption</c>: <c>Delete</c> (what this adapter writes, and what ARM reports for any
    /// machine it created) means the disk is deleted with the VM and the answer is
    /// <see cref="DataImpact.Destroyed"/>; an explicit <c>Detach</c> means the bytes survive but the
    /// replacement comes up on a fresh disk with nothing pointing at the old one, which is
    /// <see cref="DataImpact.AtRisk"/> exactly as that value describes. A machine that reports no delete
    /// option at all is answered <see cref="DataImpact.Destroyed"/>, not <see cref="DataImpact.AtRisk"/>:
    /// the reassuring reading is the one that would need evidence.
    /// </para>
    /// <para>
    /// <strong>Region and resource group produce a plan, not an exception.</strong> Both set
    /// <see cref="PlannedChange.RequiresRecreate"/>, so the plan can only describe itself as
    /// <see cref="UpdateStrategy.Recreate"/> — but that is a statement of what reaching the desired state
    /// would cost, not a promise this adapter will do it. Such a plan carries exactly one stage, marked
    /// <c>NOT SUPPORTED</c>, and no stage describing an operation.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return null;
        }

        var vm = await _api.GetResourceAsync<ArmVirtualMachine>(handle.ProviderResourceId, ct).ConfigureAwait(false);
        return vm is null ? null : BuildUpdatePlan(vm, handle, BuildSpec(desired));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>What is compared, and where each expectation comes from.</strong> Four aspects, with the
    /// strength of each one made visible rather than smoothed over:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>region</c> — compared against <see cref="ResourceHandle.Region"/>, which this adapter stamps on
    /// every handle it produces from the resource's ARM <c>location</c>. A real recorded expectation.
    /// Compared case-insensitively, because ARM accepts <c>eastus</c> and <c>East US</c> as the same place
    /// and reporting that as drift would be a false alarm.
    /// </description></item>
    /// <item><description>
    /// <c>tag &lt;key&gt;</c> — every entry in <see cref="ResourceHandle.Tags"/> is looked for on the live
    /// machine. Also a real recorded expectation, and the one that catches a VM whose Servyx ownership tags
    /// or sibling-name bookkeeping were edited away at the provider.
    /// </description></item>
    /// <item><description>
    /// <c>size</c> and <c>image</c> — read from the live machine, but their <em>expectations</em> can only
    /// live in the handle's tags, under <see cref="ServyxTagKeys.Size"/> and
    /// <see cref="ServyxTagKeys.Image"/>, because a <see cref="ResourceHandle"/> has no field for either. A
    /// handle recording neither reports both as divergences with a null
    /// <see cref="DriftDivergence.Expected"/> rather than as matches, for the reason
    /// <see cref="DriftDivergence.Expected"/> gives: a check that cannot prove a match must not claim one.
    /// This adapter does not stamp those two tags itself at create time — that would change the create
    /// sequence, which is a write path and out of scope for a change adding only reads — so a caller wanting
    /// the strong answer supplies them as ordinary <c>tag:servyx.size</c> / <c>tag:servyx.image</c>
    /// provisioning parameters, which <see cref="BuildSpec"/> already carries onto every resource and hence
    /// back onto the handle.
    /// </description></item>
    /// </list>
    /// <para>
    /// A machine ARM no longer has is reported as drift under <c>existence</c>, never as an exception and
    /// never as a match: for a per-hour billed resource its disappearance is precisely what a caller is
    /// asking about. A handle belonging to another provisioner, or one naming a NIC, a public address or a
    /// virtual network rather than a machine, is answered without touching ARM — but as a divergence, since
    /// "this is not a machine I can check" is not evidence that anything is intact.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        if (!IsVirtualMachineId(handle.ProviderResourceId))
        {
            return new DriftResult(
                handle,
                [new DriftDivergence("resource-kind", VirtualMachineResourceType, NullIfBlank(handle.ProviderResourceId))]);
        }

        var vm = await _api.GetResourceAsync<ArmVirtualMachine>(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (vm is null)
        {
            return new DriftResult(handle, [new DriftDivergence("existence", "present", null)]);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var live = ServyxAzureTags.FromArmTags(vm.Tags);
        var divergences = new List<DriftDivergence>();

        var liveRegion = NullIfBlank(vm.Location);
        if (!LocationEquals(NullIfBlank(handle.Region), liveRegion))
        {
            divergences.Add(new DriftDivergence("region", NullIfBlank(handle.Region), liveRegion));
        }

        var recordedSize = RecordedExpectation(recorded, ServyxTagKeys.Size);
        var liveSize = NullIfBlank(vm.Properties?.HardwareProfile?.VmSize);
        if (!string.Equals(recordedSize, liveSize, StringComparison.OrdinalIgnoreCase))
        {
            divergences.Add(new DriftDivergence("size", recordedSize, liveSize));
        }

        var recordedImage = RecordedExpectation(recorded, ServyxTagKeys.Image);
        var liveImage = LiveImageUrn(vm);
        if (!string.Equals(recordedImage, liveImage, StringComparison.OrdinalIgnoreCase))
        {
            divergences.Add(new DriftDivergence("image", recordedImage, liveImage));
        }

        // The size and image tags are the *source* of the two expectations above, so re-reporting them here
        // would describe one divergence twice - and would compare a tag against a tag rather than against the
        // machine, which is the weaker of the two checks.
        foreach (var expected in recorded.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (IsDescriptiveExpectationKey(expected.Key))
            {
                continue;
            }

            var found = live.TryGetValue(expected.Key, out var value) ? value : null;
            if (!string.Equals(expected.Value, found, StringComparison.Ordinal))
            {
                divergences.Add(new DriftDivergence($"tag {expected.Key}", expected.Value, found));
            }
        }

        return new DriftResult(handle, divergences);
    }

    /// <summary>
    /// The whole of update planning: pure comparison between an already-fetched VM and the desired spec.
    /// Touches only <see cref="_timeProvider"/> (for the plan's expiry) and never <see cref="_api"/>, so every
    /// request on the update path is the single read its caller already made.
    /// </summary>
    private UpdatePlan BuildUpdatePlan(ArmVirtualMachine vm, ResourceHandle handle, AzureVirtualMachineSpec spec)
    {
        var resourceId = NullIfBlank(vm.Id) ?? handle.ProviderResourceId;
        var liveSize = NullIfBlank(vm.Properties?.HardwareProfile?.VmSize);
        var liveImage = LiveImageUrn(vm);
        var liveRegion = NullIfBlank(vm.Location);
        var liveResourceGroup = ResourceGroupOf(resourceId);
        var liveTags = ServyxAzureTags.FromArmTags(vm.Tags);
        var desiredTags = TagsFor(spec);

        var sizeChanged = !string.Equals(liveSize, spec.Machine.SizeRef, StringComparison.OrdinalIgnoreCase);
        var imageChanged = !string.Equals(liveImage, spec.Machine.ImageRef, StringComparison.OrdinalIgnoreCase);
        var regionChanged = !LocationEquals(liveRegion, spec.Machine.Region);
        var resourceGroupChanged = !string.Equals(liveResourceGroup, spec.ResourceGroup, StringComparison.OrdinalIgnoreCase);

        var changes = new List<PlannedChange>();

        if (sizeChanged)
        {
            // An ARM write to hardwareProfile.vmSize mutates the existing resource; the OS disk is a separate
            // resource the write does not name.
            changes.Add(new PlannedChange("size", liveSize, spec.Machine.SizeRef, RequiresRecreate: false));
        }

        if (imageChanged)
        {
            // storageProfile.imageReference is fixed at creation. Reaching a different image replaces the VM.
            changes.Add(new PlannedChange("image", liveImage, spec.Machine.ImageRef, RequiresRecreate: true));
        }

        if (regionChanged)
        {
            // An ARM resource's location is immutable. Not "requires a recreate this adapter will perform" -
            // requires one it refuses to plan.
            changes.Add(new PlannedChange("region", liveRegion, spec.Machine.Region, RequiresRecreate: true));
        }

        if (resourceGroupChanged)
        {
            // The resource group is part of the VM's ARM id, so a different group names a different resource.
            changes.Add(new PlannedChange("resourceGroup", liveResourceGroup, spec.ResourceGroup, RequiresRecreate: true));
        }

        var tagChanges = new List<PlannedChange>();
        foreach (var desired in desiredTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (IsDescriptiveExpectationKey(desired.Key))
            {
                continue;
            }

            var current = liveTags.TryGetValue(desired.Key, out var value) ? value : null;
            if (!string.Equals(current, desired.Value, StringComparison.Ordinal))
            {
                // ARM edits a resource's tags without touching what the resource is or does.
                tagChanges.Add(new PlannedChange($"tag {desired.Key}", current, desired.Value, RequiresRecreate: false));
            }
        }

        changes.AddRange(tagChanges);

        var strategy = changes.Count == 0
            ? UpdateStrategy.NoChangeRequired
            : changes.Any(c => c.RequiresRecreate)
                ? UpdateStrategy.Recreate
                : UpdateStrategy.InPlace;

        var immovable = regionChanged || resourceGroupChanged;
        var deleteOption = NullIfBlank(vm.Properties?.StorageProfile?.OsDisk?.DeleteOption);
        var dataImpact = AssertDataImpact(strategy, imageChanged, immovable, deleteOption);

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : immovable
                ? BuildUnsupportedMoveStages(spec, liveRegion, liveResourceGroup, regionChanged, resourceGroupChanged, changes, dataImpact)
                : BuildUpdateStages(vm, spec, resourceId, liveSize, liveImage, deleteOption, imageChanged, sizeChanged, tagChanges, dataImpact);

        var planHash = ComputeUpdatePlanHash(resourceId, liveSize, liveImage, liveRegion, liveTags, spec, desiredTags, strategy, dataImpact);

        return new UpdatePlan(
            planId: string.Create(CultureInfo.InvariantCulture, $"{Id}:update:{spec.VmName}:{planHash[..12]}"),
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(UpdatePlanLifetimeMinutes));
    }

    /// <summary>
    /// The deliberate data-impact assertion, derived from the ARM operation each difference would require and
    /// from the live machine's own OS-disk delete option — never from a default. Every branch is a claim this
    /// adapter can defend; see the remarks on <see cref="PlanUpdateAsync"/>.
    /// </summary>
    private static DataImpact AssertDataImpact(
        UpdateStrategy strategy,
        bool imageChanged,
        bool immovable,
        string? osDiskDeleteOption)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the disk.
            return DataImpact.Preserved;
        }

        if (immovable || imageChanged)
        {
            // Both routes end in the same place: this VM is deleted and another is created. What that costs is
            // decided by the disk's delete option, read off the live machine rather than assumed.
            return string.Equals(osDiskDeleteOption, OsDiskDetachOption, StringComparison.OrdinalIgnoreCase)
                ? DataImpact.AtRisk
                : DataImpact.Destroyed;
        }

        // Everything left is a vmSize write and/or a tag write. Neither names the OS disk resource nor changes
        // the VM's reference to it, so the machine that exists afterwards is this machine, still attached to
        // the same disk. That is what Preserved requires, asserted from the write being planned.
        return DataImpact.Preserved;
    }

    /// <summary>The stages of an update ARM can actually perform: a replacement, a resize, a retag, or a combination.</summary>
    private static IReadOnlyList<ProvisioningStage> BuildUpdateStages(
        ArmVirtualMachine vm,
        AzureVirtualMachineSpec spec,
        string resourceId,
        string? liveSize,
        string? liveImage,
        string? osDiskDeleteOption,
        bool imageChanged,
        bool sizeChanged,
        IReadOnlyList<PlannedChange> tagChanges,
        DataImpact dataImpact)
    {
        var stages = new List<ProvisioningStage>();
        var name = NullIfBlank(vm.Name) ?? spec.VmName;
        var diskFate = DescribeOsDiskFate(osDiskDeleteOption);

        if (imageChanged)
        {
            stages.Add(new ProvisioningStage(
                "delete-virtual-machine",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delete virtual machine '{name}' ({resourceId}). ARM has no operation that changes an existing machine's image: properties.storageProfile.imageReference is fixed when the machine is created, so moving from '{liveImage ?? "(unknown)"}' to '{spec.Machine.ImageRef}' means deleting this machine and creating another. ")
                + diskFate));

            stages.Add(new ProvisioningStage(
                "create-replacement-virtual-machine",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Create a replacement machine named '{spec.VmName}' in resource group '{spec.ResourceGroup}' from image '{spec.Machine.ImageRef}' at size '{spec.Machine.SizeRef}', attached to the existing network interface '{spec.NetworkInterfaceName}'. ")
                + "The network interface, the public IP address and the virtual network are separate ARM "
                + "resources that this plan does not delete, so the host keeps the address it had. That is the "
                + "one thing that survives: the replacement boots from a fresh copy of the image with none of "
                + "the previous machine's files on it, and the game would have to be installed and restored "
                + "again."));
        }

        if (sizeChanged)
        {
            stages.Add(new ProvisioningStage(
                "resize-virtual-machine",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Write properties.hardwareProfile.vmSize on '{name}' from '{liveSize ?? "(unknown)"}' to '{spec.Machine.SizeRef}'. ")
                + "This is a property update on the existing resource: the machine keeps its ARM id, its "
                + "network interface, its address, and its managed OS disk - which is a separate ARM resource "
                + "attached by id, and which this write neither names nor re-references. Azure deallocates and "
                + "restarts the machine to apply a new size, so the workload is interrupted, but no step "
                + "detaches, reimages or reformats the disk at any point."));
        }

        if (tagChanges.Count > 0)
        {
            stages.Add(new ProvisioningStage(
                "retag-virtual-machine",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Write {tagChanges.Count} tag(s) on the machine so it carries the Servyx tag set the request describes: ")
                + string.Join("; ", tagChanges.Select(c => c.Description))
                + ". ARM tags are metadata on the resource; changing one does not stop, restart, or otherwise "
                + "touch the machine."));
        }

        stages.Add(new ProvisioningStage(
            "data-impact",
            Id,
            string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact}: ")
            + dataImpact switch
            {
                DataImpact.Destroyed =>
                    "replacing the machine deletes its managed OS disk, so approving this plan is approving the "
                    + "deletion of everything stored on it - the installed game, its configuration, and every "
                    + "save file. Snapshot the disk first if any of it matters; this adapter cannot do that for "
                    + "you and does not claim the Snapshot capability.",
                DataImpact.AtRisk =>
                    "replacing the machine leaves its managed OS disk behind rather than deleting it, because "
                    + "the live machine reports deleteOption 'Detach'. The bytes survive, but nothing will be "
                    + "attached to them: the replacement boots from a fresh disk, and the old one becomes an "
                    + "untagged, per-GB-billing resource that this adapter's orphan sweep cannot find.",
                _ =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the machine keeps its ARM id ({resourceId}) and stays attached to the same managed OS disk. No step above names that disk, detaches it, or replaces the machine that references it."),
            }));

        return stages;
    }

    /// <summary>
    /// The single stage a plan carries when the request asks for a location or a resource group an existing
    /// machine cannot be given.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>only</em> stage such a plan carries, even when the request also changes the size
    /// or the image. Listing a resize next to a refusal would suggest part of the plan is applicable, and it
    /// is not: the machine the other stages would act on is not the machine the request is describing. Every
    /// difference is still reported by name in <see cref="UpdatePlan.Changes"/>, so nothing is hidden — only
    /// the illusion of a partially-executable plan is.
    /// </remarks>
    private static IReadOnlyList<ProvisioningStage> BuildUnsupportedMoveStages(
        AzureVirtualMachineSpec spec,
        string? liveRegion,
        string? liveResourceGroup,
        bool regionChanged,
        bool resourceGroupChanged,
        IReadOnlyList<PlannedChange> changes,
        DataImpact dataImpact)
    {
        var reasons = new List<string>();

        if (regionChanged)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the request asks for region '{spec.Machine.Region}' and the machine is in '{liveRegion ?? "(unknown)"}', and an ARM resource's location is immutable - there is no write, and no Azure operation of any kind, that moves an existing virtual machine to another region"));
        }

        if (resourceGroupChanged)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the request asks for resource group '{spec.ResourceGroup}' and the machine is in '{liveResourceGroup ?? "(unknown)"}', and the group is part of the machine's ARM id, so a different group names a different resource rather than a changed one"));
        }

        var others = changes
            .Where(c => !string.Equals(c.Aspect, "region", StringComparison.Ordinal)
                && !string.Equals(c.Aspect, "resourceGroup", StringComparison.Ordinal))
            .ToList();

        return
        [
            new(
                "move-not-supported",
                Id,
                "NOT SUPPORTED: " + string.Join("; and ", reasons) + ". "
                + "No operation is planned here, and none will be. Reaching the requested state would mean "
                + "deleting this machine - and its managed OS disk with it, so everything stored on it goes - "
                + "and creating a different machine elsewhere, then reinstalling and restoring onto it. That is "
                + "a decision for a person, not a step this planner will describe on their behalf. "
                + string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact} for that reason. ")
                + (others.Count == 0
                    ? "Nothing else about the machine needs to change."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"The other {others.Count} difference(s) found are equally not applied, because they describe a machine this one cannot become: ")
                      + string.Join("; ", others.Select(c => c.Description))
                      + ".")),
        ];
    }

    /// <summary>The plain-language fate of the managed OS disk when the VM it belongs to is deleted.</summary>
    private static string DescribeOsDiskFate(string? osDiskDeleteOption) =>
        string.Equals(osDiskDeleteOption, OsDiskDetachOption, StringComparison.OrdinalIgnoreCase)
            ? "The machine's managed OS disk reports deleteOption 'Detach', so it is left behind rather than "
              + "deleted - its contents survive, but nothing is attached to them afterwards and the disk carries "
              + "no Servyx tags, so no orphan sweep can find it again."
            : osDiskDeleteOption is null
                ? "ARM reports no deleteOption for the machine's managed OS disk. This adapter writes 'Delete' "
                  + "when it creates a machine, so the destructive reading is the one stated here rather than the "
                  + "reassuring one: assume the disk is deleted with the machine and everything stored on it - the "
                  + "installed game, its configuration, and every save file - is gone."
                : "The machine's managed OS disk is deleted with it, by the deleteOption 'Delete' this adapter "
                  + "declares when it creates a machine. Everything stored on the machine - the installed game, "
                  + "its configuration, and every save file - is deleted and cannot be recovered.";

    /// <summary>
    /// The four-part image URN a live machine is running, in the same <c>publisher:offer:sku:version</c> form
    /// <see cref="AzureVirtualMachineSpec.ParseImageUrn"/> consumes, or <see langword="null"/> when ARM does
    /// not report a complete marketplace image reference for it.
    /// </summary>
    /// <remarks>
    /// Null rather than a partial string when any field is missing — a machine created from a custom image or
    /// a gallery version has no marketplace URN, and inventing a half-formed one would produce a difference
    /// the plan would then propose to "fix" by replacing the machine.
    /// </remarks>
    private static string? LiveImageUrn(ArmVirtualMachine vm)
    {
        var image = vm.Properties?.StorageProfile?.ImageReference;
        if (image is null)
        {
            return null;
        }

        var publisher = NullIfBlank(image.Publisher);
        var offer = NullIfBlank(image.Offer);
        var sku = NullIfBlank(image.Sku);
        var version = NullIfBlank(image.Version);

        return publisher is null || offer is null || sku is null || version is null
            ? null
            : string.Join(':', publisher, offer, sku, version);
    }

    /// <summary>
    /// The resource group named by an ARM resource id, or <see langword="null"/> when the id does not name
    /// one.
    /// </summary>
    /// <remarks>
    /// Read from the id rather than from the <see cref="ServyxAzureTags.ResourceGroupTag"/> bookkeeping tag on
    /// purpose: the id is ARM's own statement of where the resource is, and the tag is Servyx's record of
    /// where it meant to put it. Comparing the desired group against the tag would compare a request against
    /// a record and miss a machine that is not where its own tag says it is.
    /// </remarks>
    private static string? ResourceGroupOf(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        var segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return NullIfBlank(segments[i + 1]);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether two ARM locations name the same place.
    /// </summary>
    /// <remarks>
    /// ARM accepts and returns a location in two spellings — the slug <c>eastus</c> and the display name
    /// <c>East US</c> — and treats them as one value. Whitespace is stripped and the comparison is
    /// case-insensitive for that reason, and for no other: a drift check that reported "expected eastus,
    /// found East US" would be raising an alarm about a machine that has not moved, and an operator who
    /// learns to ignore this check's output has lost the check entirely. Nothing else about a location is
    /// normalised, so two genuinely different regions still compare unequal.
    /// </remarks>
    private static bool LocationEquals(string? left, string? right) =>
        string.Equals(NormalizeLocation(left), NormalizeLocation(right), StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeLocation(string? location) =>
        location is null ? null : string.Concat(location.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// Reads one recorded expectation out of a handle's tags, treating a blank value as no expectation at all
    /// rather than as an expectation of emptiness.
    /// </summary>
    private static string? RecordedExpectation(IReadOnlyDictionary<string, string> recorded, string key) =>
        recorded.TryGetValue(key, out var value) ? NullIfBlank(value) : null;

    /// <summary>
    /// Whether a tag key is one of the two descriptive keys reported under their own aspect (<c>size</c>,
    /// <c>image</c>) rather than as an ordinary tag.
    /// </summary>
    private static bool IsDescriptiveExpectationKey(string key) =>
        string.Equals(key, ServyxTagKeys.Size, StringComparison.Ordinal)
        || string.Equals(key, ServyxTagKeys.Image, StringComparison.Ordinal);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string ComputeUpdatePlanHash(
        string resourceId,
        string? liveSize,
        string? liveImage,
        string? liveRegion,
        IReadOnlyDictionary<string, string> liveTags,
        AzureVirtualMachineSpec spec,
        IReadOnlyDictionary<string, string> desiredTags,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(resourceId).Append('\n');
        builder.Append(liveSize ?? string.Empty).Append('\n');
        builder.Append(liveImage ?? string.Empty).Append('\n');
        builder.Append(liveRegion ?? string.Empty).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var tag in liveTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-tag {tag.Key}={tag.Value}\n");
        }

        builder.Append(ComputePlanHash(spec, desiredTags)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
