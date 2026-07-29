using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the EC2 adapter: it reads a live instance and describes what would
/// have to happen to it. Nothing here changes anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before changing a single string in this file.</strong> The container adapter's update
/// recreates a container and its volumes survive; the SSH adapter's update re-runs the install verbs and the
/// data directory survives. Neither can delete a user's saves. Here the machine's state lives on an EBS volume
/// whose fate this adapter <em>did not choose</em>, which is the single fact that makes EC2's answers different
/// from every sibling adapter's:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Instance type — in place, and the root volume genuinely survives.</strong> EC2 changes an instance's
/// type with <c>ModifyInstanceAttribute</c>, which requires the instance to be <em>stopped</em> first and
/// started again afterwards. A stop is not a terminate: the instance keeps its id, and every EBS volume in its
/// block device mapping stays attached across the whole cycle — <c>DeleteOnTermination</c> is consulted on
/// termination and on nothing else. The claim that the data survives is therefore not an assumption about what
/// "resize" ought to mean; it is asserted from the volumes this adapter reads off the live instance and names
/// in the plan. Two real costs are stated in the stage text rather than folded into
/// <see cref="DataImpact"/>, which describes persistent data and not availability: the workload is down for the
/// stop/start, and the instance's <em>ephemeral public IPv4 address changes</em>, because this adapter never
/// allocates an Elastic IP (see <see cref="ProvisioningCapabilities.StaticAddress"/>, which it does not claim).
/// The one case where the reassuring answer is withheld is an instance whose block device mapping reports no
/// EBS volume at all: such an instance is instance-store backed, cannot be stopped at all, and has no store
/// this adapter can show being carried across — so it is answered <see cref="DataImpact.AtRisk"/>.
/// </description></item>
/// <item><description>
/// <strong>Image — a replacement, and what it costs is a flag this adapter never set.</strong> An instance's
/// AMI is fixed at <c>RunInstances</c> time; <c>ModifyInstanceAttribute</c> cannot change it and no other EC2
/// operation does either, so the only route to a different image is to terminate this instance and launch
/// another. What that does to the data is decided entirely by the root volume's <c>DeleteOnTermination</c>
/// flag — and, unlike the Azure adapter which writes <c>deleteOption: Delete</c> itself and can therefore argue
/// from its own create-time choice, <strong>this adapter sends no <c>BlockDeviceMapping</c> at all</strong>, so
/// the flag is whatever the AMI's default happens to be. It is read back off the live instance:
/// <c>true</c> means the volume dies with the instance (<see cref="DataImpact.Destroyed"/>), <c>false</c> means
/// the bytes survive on a detached volume nothing points at (<see cref="DataImpact.AtRisk"/>), and
/// <em>reported by neither value</em> means this adapter cannot demonstrate that the data survives attached —
/// which is <see cref="DataImpact.AtRisk"/> and never <see cref="DataImpact.Preserved"/>. The reassuring
/// reading is the one that would need evidence.
/// </description></item>
/// <item><description>
/// <strong>Region — not possible, and doubly so here.</strong> No EC2 operation moves an instance between
/// regions; and this adapter cannot even <em>reach</em> another region, because the region is in the endpoint
/// hostname and in the SigV4 credential scope (see the type remarks on
/// <see cref="AwsEc2Provisioner"/>). The difference is reported and <em>nothing</em> is planned. Quietly
/// substituting "terminate it and launch one over there" would be a plan to delete a machine, presented as a
/// plan to move one.
/// </description></item>
/// </list>
/// <para>
/// <strong>Where the desired region comes from, given <see cref="BuildSpec"/> has no <c>region</c> key.</strong>
/// It reads one anyway — <see cref="PlanUpdateAsync"/> looks at the request's <c>region</c> parameter directly
/// and compares it against the region this provisioner is pinned to. That is the whole point: <em>silently
/// dropping</em> a region a caller named is exactly the failure this file exists to prevent, so the parameter
/// <see cref="BuildSpec"/> deliberately refuses to act on is one the planner deliberately refuses to ignore.
/// </para>
/// <para>
/// <strong>A terminated instance is drift, not an error and not a match.</strong> EC2 keeps a terminated
/// instance visible to <c>DescribeInstances</c> for up to about an hour, complete with its tags, its type and
/// its old addresses. A drift check that treated "the API answered with an instance object" as evidence of
/// existence would therefore report a <em>match</em> for a machine that no longer exists — for up to an hour
/// after somebody deleted it, which is precisely the window in which a caller most needs to be told. So
/// "gone" is consulted as the state it is, through the same <c>Ec2Instance.GoneStates</c> set
/// <see cref="RefreshAsync"/> and <see cref="ReconcileAsync"/> already use, and it is reported under
/// <c>existence</c> with the live state name as the found value. That last detail is deliberate: a 404
/// (<c>InvalidInstanceID.NotFound</c>) reports <c>found nothing</c> and a terminated instance reports
/// <c>found terminated</c>, so a caller can tell "EC2 has never heard of this id" from "EC2 says this machine
/// was deleted" — two situations that call for different responses and that would otherwise be
/// indistinguishable. Both are drift; neither is an exception. Note that the same instance produces the first
/// answer roughly an hour after producing the second, which is why both spellings had to be handled rather
/// than only the one a test happens to hit.
/// </para>
/// <para>
/// <strong>Nothing here executes.</strong> Every call on both paths is a <c>DescribeInstances</c> GET. There is
/// no <c>ModifyInstanceAttribute</c>, no <c>StopInstances</c>, no <c>TerminateInstances</c> and no
/// <c>RunInstances</c> anywhere in this file. Producing a plan here changes nothing, whatever the plan says.
/// </para>
/// <para>
/// <strong>One kind of plan produced here can afterwards be carried out, and it is not the image change.</strong>
/// <c>AwsEc2Provisioner.InstanceType.cs</c> implements <see cref="IUpdateApplier"/> for a lone instance-type
/// change — the <see cref="DataImpact.Preserved"/> case argued above, and the only one whose EC2 route keeps the
/// instance and every volume in its block device mapping. It re-reads that mapping off the live instance before
/// it stops anything, so the preservation claim it acts on is the enumerated one from this file rather than an
/// assumption carried in the plan. Every other plan shape this file can produce — the image change, the region
/// refusal, a bundled retag — is refused there without a mutating request.
/// </para>
/// </remarks>
public sealed partial class AwsEc2Provisioner : IMaintainer
{
    /// <summary>How long an update plan's observed live state should be trusted for.</summary>
    /// <remarks>The same fifteen minutes <see cref="BuildPlan"/> gives a provisioning plan, for the same reason.</remarks>
    private const int UpdatePlanLifetimeMinutes = 15;

    /// <summary>
    /// The provisioning parameter naming the region a request wants, read only here.
    /// </summary>
    /// <remarks>
    /// <see cref="BuildSpec"/> recognises no such key and never will — see its remarks. This planner reads it
    /// so that a caller who names a region can be <em>told</em> the instance cannot be moved there, instead of
    /// having the parameter silently discarded and being shown a plan that appears to satisfy their request.
    /// </remarks>
    internal const string DesiredRegionParameter = "region";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads one instance, then computes.</strong> The only request on this path is a
    /// <c>DescribeInstances</c> GET for the id the handle names. An instance EC2 no longer knows, a handle whose
    /// <see cref="ResourceHandle.ProviderResourceId"/> is not an instance id (a <c>vol-</c> id, say), and an
    /// instance EC2 reports as terminated or shutting down all yield <see langword="null"/> — mirroring
    /// <see cref="RefreshAsync"/> exactly, including its treatment of "gone" as a state, and deliberately not
    /// the same answer as "nothing needs to change".
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided, per difference, from EC2's own semantics.</strong>
    /// A plan whose only differences are the instance type and the instance's tags is
    /// <see cref="DataImpact.Preserved"/>, and the justification is enumerated rather than assumed: the plan
    /// names the EBS volumes it read off the live instance, and neither a stop/start cycle nor a
    /// <c>CreateTags</c> call detaches or deletes one — <c>DeleteOnTermination</c> is consulted on termination
    /// and nowhere else. An instance reporting no EBS volume at all cannot support that claim (it is
    /// instance-store backed and cannot be stopped in the first place), so it is answered
    /// <see cref="DataImpact.AtRisk"/>. A plan that changes the image, or one blocked by a region difference,
    /// ends in a terminate, so its answer comes from the live root volume's <c>DeleteOnTermination</c>:
    /// <c>true</c> is <see cref="DataImpact.Destroyed"/>, <c>false</c> is <see cref="DataImpact.AtRisk"/>, and
    /// an unreported flag is <see cref="DataImpact.AtRisk"/> — never <see cref="DataImpact.Preserved"/>, which
    /// this adapter has no evidence for in any of the three cases.
    /// </para>
    /// <para>
    /// <strong>What forces a recreate, and why the region case still produces a plan rather than an
    /// exception.</strong> An instance-type or tag difference sets no
    /// <see cref="PlannedChange.RequiresRecreate"/> flag; an image or region difference sets it, so the plan can
    /// only describe itself as <see cref="UpdateStrategy.Recreate"/>. For the region case that strategy states
    /// what reaching the desired state would cost, not a promise this adapter will do it: such a plan carries
    /// exactly one stage, marked <c>NOT SUPPORTED</c>, and no stage describing an operation.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        if (!IsInstanceId(handle.ProviderResourceId))
        {
            return null;
        }

        var instance = await _api.DescribeInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);

        // A terminated instance is still described by EC2 for about an hour. Planning an update against one
        // would describe operations on a machine that no longer exists, so it is answered exactly as
        // RefreshAsync answers it.
        return instance is null || instance.IsGone
            ? null
            : BuildUpdatePlan(instance, BuildSpec(desired), DesiredRegionOf(desired));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>What is compared, and where each expectation comes from.</strong> Four aspects, and they do not
    /// all have equally strong records behind them, so the difference is made visible rather than smoothed over:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>region</c> — compared against <see cref="ResourceHandle.Region"/>, which this adapter stamps on every
    /// handle it produces. A real recorded expectation, and the live value is not a guess: an instance answered
    /// for by this provisioner's endpoint is by construction in this provisioner's region, because the region is
    /// in the hostname. Compared case-insensitively, since AWS treats a region code that way.
    /// </description></item>
    /// <item><description>
    /// <c>tag &lt;key&gt;</c> — every entry in <see cref="ResourceHandle.Tags"/> is looked for on the live
    /// instance. Also a real recorded expectation, and the one that catches an instance whose Servyx ownership
    /// tags were edited away at the provider — which is not cosmetic here, since
    /// <see cref="ReconcileAsync"/> finds orphans by exactly those tags and a per-second-billed instance it
    /// cannot see bills forever.
    /// </description></item>
    /// <item><description>
    /// <c>size</c> and <c>image</c> — read from the live instance directly, but their <em>expectations</em> can
    /// only live in the handle's tags, under <see cref="ServyxTagKeys.Size"/> and
    /// <see cref="ServyxTagKeys.Image"/>, because a <see cref="ResourceHandle"/> has no field for either. A
    /// handle recording neither reports both as divergences with a null
    /// <see cref="DriftDivergence.Expected"/> — "Servyx recorded no expected value, found t3.medium" — rather
    /// than as matches, for the reason <see cref="DriftDivergence.Expected"/> gives: a check that cannot prove a
    /// match must not claim one. This adapter does not stamp those two tags itself at launch time (that would
    /// change the <c>RunInstances</c> request, which is a write path and out of scope for a change that adds
    /// only reads); a caller wanting the strong answer supplies them as ordinary <c>tag:servyx.size</c> /
    /// <c>tag:servyx.image</c> provisioning parameters, which <see cref="BuildSpec"/> already carries onto the
    /// instance and hence back onto the handle.
    /// </description></item>
    /// </list>
    /// <para>
    /// An instance EC2 no longer has, and an instance EC2 still describes but reports as terminated or shutting
    /// down, are both reported as drift under <c>existence</c> and never as a match or an exception — see the
    /// type remarks for why the two are distinguished by their <see cref="DriftDivergence.Found"/> value rather
    /// than collapsed. A handle belonging to another provisioner, or one whose id is not an instance id, is
    /// answered without touching the API — but as a divergence, since "this is not my resource" is not evidence
    /// that it is intact.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        if (!IsInstanceId(handle.ProviderResourceId))
        {
            return new DriftResult(
                handle,
                [new DriftDivergence("instance-id", "an EC2 instance id ('i-...')", NullIfBlank(handle.ProviderResourceId))]);
        }

        var instance = await _api.DescribeInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return new DriftResult(handle, [new DriftDivergence("existence", "present", null)]);
        }

        if (instance.IsGone)
        {
            // Not a match, and not an exception. EC2 answered with a complete instance object - tags, type,
            // addresses and all - for a machine that has stopped existing. Reported with the state as the found
            // value so it is distinguishable from the 404 above.
            return new DriftResult(
                handle,
                [new DriftDivergence("existence", "present", NullIfBlank(instance.State) ?? "gone")]);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var divergences = new List<DriftDivergence>();

        if (!string.Equals(NullIfBlank(handle.Region), _region, StringComparison.OrdinalIgnoreCase))
        {
            divergences.Add(new DriftDivergence("region", NullIfBlank(handle.Region), _region));
        }

        var recordedSize = RecordedExpectation(recorded, ServyxTagKeys.Size);
        var liveSize = NullIfBlank(instance.InstanceType);
        if (!string.Equals(recordedSize, liveSize, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("size", recordedSize, liveSize));
        }

        var recordedImage = RecordedExpectation(recorded, ServyxTagKeys.Image);
        var liveImage = NullIfBlank(instance.ImageId);
        if (!string.Equals(recordedImage, liveImage, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("image", recordedImage, liveImage));
        }

        // The size and image tags are the *source* of the two expectations above, so re-reporting them here
        // would describe one divergence twice - and would compare a tag against a tag rather than against the
        // instance, which is the weaker of the two checks.
        foreach (var expected in recorded.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (IsDescriptiveExpectationKey(expected.Key))
            {
                continue;
            }

            var found = instance.Tags.TryGetValue(expected.Key, out var value) ? value : null;
            if (!string.Equals(expected.Value, found, StringComparison.Ordinal))
            {
                divergences.Add(new DriftDivergence($"tag {expected.Key}", expected.Value, found));
            }
        }

        return new DriftResult(handle, divergences);
    }

    /// <summary>
    /// The whole of update planning: pure comparison between an already-fetched instance and the desired spec.
    /// Touches only <see cref="_timeProvider"/> (for the plan's expiry) and never <see cref="_api"/>, so every
    /// request on the update path is the single read its caller already made.
    /// </summary>
    private UpdatePlan BuildUpdatePlan(Ec2Instance instance, AwsEc2InstanceSpec spec, string desiredRegion)
    {
        var liveSize = NullIfBlank(instance.InstanceType);
        var liveImage = NullIfBlank(instance.ImageId);
        var liveTags = instance.Tags;
        var desiredTags = TagsFor(spec, ServyxEc2Tags.RoleInstance);

        var sizeChanged = !string.Equals(liveSize, spec.Machine.SizeRef, StringComparison.Ordinal);
        var imageChanged = !string.Equals(liveImage, spec.Machine.ImageRef, StringComparison.Ordinal);
        var regionChanged = !string.Equals(_region, desiredRegion, StringComparison.OrdinalIgnoreCase);

        var changes = new List<PlannedChange>();

        if (sizeChanged)
        {
            // ModifyInstanceAttribute acts on the instance that already exists: same id, same EBS volumes.
            changes.Add(new PlannedChange("size", liveSize, spec.Machine.SizeRef, RequiresRecreate: false));
        }

        if (imageChanged)
        {
            // No EC2 call swaps a running instance's AMI. The only route replaces the instance.
            changes.Add(new PlannedChange("image", liveImage, spec.Machine.ImageRef, RequiresRecreate: true));
        }

        if (regionChanged)
        {
            // Not "requires a recreate this adapter will perform" - requires one it refuses to plan.
            changes.Add(new PlannedChange("region", _region, desiredRegion, RequiresRecreate: true));
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
                // CreateTags/DeleteTags edit an instance's tags without stopping or otherwise touching it.
                tagChanges.Add(new PlannedChange($"tag {desired.Key}", current, desired.Value, RequiresRecreate: false));
            }
        }

        changes.AddRange(tagChanges);

        var strategy = changes.Count == 0
            ? UpdateStrategy.NoChangeRequired
            : changes.Any(c => c.RequiresRecreate)
                ? UpdateStrategy.Recreate
                : UpdateStrategy.InPlace;

        var rootVolumeFate = RootVolumeFate(instance);
        var dataImpact = AssertDataImpact(strategy, imageChanged, regionChanged, instance, rootVolumeFate);

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : regionChanged
                ? BuildUnsupportedRegionStages(instance, desiredRegion, changes, rootVolumeFate, dataImpact)
                : BuildUpdateStages(instance, spec, liveSize, liveImage, imageChanged, sizeChanged, tagChanges, rootVolumeFate, dataImpact);

        var planHash = ComputeUpdatePlanHash(instance, liveSize, liveImage, desiredRegion, spec, desiredTags, strategy, dataImpact);

        return new UpdatePlan(
            planId: string.Create(CultureInfo.InvariantCulture, $"{Id}:update:{instance.InstanceId}:{planHash[..12]}"),
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(UpdatePlanLifetimeMinutes));
    }

    /// <summary>
    /// What terminating this instance would do to the EBS volumes its data lives on, read off the live block
    /// device mapping rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="true"/> when any attached EBS volume reports <c>deleteOnTermination=true</c> — one deleted
    /// store is enough to make a replacement destructive, so the worst-reporting device decides.
    /// <see langword="false"/> only when every attached volume explicitly reports <c>false</c>.
    /// <see langword="null"/> when the instance reports no EBS volume at all, or when any volume reports no flag:
    /// in both cases this adapter has no evidence about what happens to the data, and "no evidence" is not
    /// "nothing happens".
    /// </para>
    /// <para>
    /// This adapter never sends a <c>BlockDeviceMapping</c>, so the flag is the AMI's default in every case —
    /// there is no create-time choice of Servyx's own to fall back on, which is exactly the difference from the
    /// Azure adapter, whose OS disk carries the <c>deleteOption</c> that adapter itself wrote.
    /// </para>
    /// </remarks>
    private static bool? RootVolumeFate(Ec2Instance instance)
    {
        if (instance.BlockDevices.Count == 0)
        {
            return null;
        }

        if (instance.BlockDevices.Any(d => d.DeleteOnTermination == true))
        {
            return true;
        }

        return instance.BlockDevices.All(d => d.DeleteOnTermination == false) ? false : null;
    }

    /// <summary>
    /// The deliberate data-impact assertion, derived from the EC2 operation each difference would require and
    /// from the live instance's own block device mapping — never from a default. Every branch is a claim this
    /// adapter can defend from the API's semantics; see the remarks on <see cref="PlanUpdateAsync"/>.
    /// </summary>
    private static DataImpact AssertDataImpact(
        UpdateStrategy strategy,
        bool imageChanged,
        bool regionChanged,
        Ec2Instance instance,
        bool? rootVolumeFate)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the volumes.
            return DataImpact.Preserved;
        }

        if (regionChanged || imageChanged)
        {
            // Both routes end in the same place: this instance is terminated and another is launched. What that
            // costs is decided by DeleteOnTermination, read off the live instance rather than assumed.
            //
            // Note the asymmetry with the Azure adapter's equivalent branch: there, an unknown delete option is
            // answered Destroyed, because that adapter writes 'Delete' itself and an ARM machine reporting
            // nothing is overwhelmingly likely to be one of its own. Here the flag was never Servyx's to set, so
            // an unreported one means genuinely unknown - which is AtRisk, the value that says the adapter
            // cannot show the data surviving attached. It is emphatically not Preserved.
            return rootVolumeFate switch
            {
                true => DataImpact.Destroyed,
                false => DataImpact.AtRisk,
                _ => DataImpact.AtRisk,
            };
        }

        // Everything left is a stop/ModifyInstanceAttribute/start cycle and/or a CreateTags call. Neither
        // detaches a volume nor terminates the instance, so DeleteOnTermination is never consulted and the
        // instance that exists afterwards is this instance with the same volumes attached - which is what
        // Preserved requires, and which is asserted from volumes actually enumerated below.
        //
        // Except when there are none to enumerate: an instance whose block device mapping reports no EBS volume
        // is instance-store backed, cannot be stopped at all (so the resize's own precondition fails), and keeps
        // its state on storage that does not survive a stop. Nothing here can be shown to be carried across.
        return instance.BlockDevices.Count == 0 ? DataImpact.AtRisk : DataImpact.Preserved;
    }

    /// <summary>The stages of an update EC2 can actually perform: a replacement, a type change, a retag, or a combination.</summary>
    private static IReadOnlyList<ProvisioningStage> BuildUpdateStages(
        Ec2Instance instance,
        AwsEc2InstanceSpec spec,
        string? liveSize,
        string? liveImage,
        bool imageChanged,
        bool sizeChanged,
        IReadOnlyList<PlannedChange> tagChanges,
        bool? rootVolumeFate,
        DataImpact dataImpact)
    {
        var stages = new List<ProvisioningStage>();
        var volumes = DescribeVolumes(instance);

        if (imageChanged)
        {
            stages.Add(new ProvisioningStage(
                "terminate-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Terminate instance {instance.InstanceId}. EC2 has no operation that changes an existing instance's image: the AMI is fixed by the RunInstances call that created it, and ModifyInstanceAttribute cannot alter it - so moving from '{liveImage ?? "(unknown)"}' to '{spec.Machine.ImageRef}' means terminating this instance and launching another. ")
                + DescribeTerminationFate(rootVolumeFate, volumes)));

            stages.Add(new ProvisioningStage(
                "launch-replacement-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Launch a replacement instance from image '{spec.Machine.ImageRef}' at instance type '{spec.Machine.SizeRef}' in region '{spec.Machine.Region}', carrying the same Servyx tags. ")
                + "The replacement is a different machine with a different instance id and a different ephemeral "
                + "public IPv4 address; nothing about the old one survives onto it. It boots from a fresh copy of "
                + "the image, so the game would have to be installed and restored again. The instance type is "
                + "applied by this launch, which is why no separate type-change step is planned alongside it."));
        }

        if (sizeChanged && !imageChanged)
        {
            stages.Add(new ProvisioningStage(
                "change-instance-type",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stop instance {instance.InstanceId}, call ModifyInstanceAttribute to change its instance type from '{liveSize ?? "(unknown)"}' to '{spec.Machine.SizeRef}', then start it again. EC2 requires the instance to be stopped for that attribute write; there is no live equivalent. ")
                + "A stop is not a terminate: the instance keeps its id, and "
                + volumes
                + " stay attached throughout, because DeleteOnTermination is consulted on termination and on "
                + "nothing else. Two things do change and are not covered by this plan's data impact, which "
                + "describes persistent data rather than availability: the workload is down for the whole "
                + "stop/start, and the instance's public IPv4 address is ephemeral and WILL be a different "
                + "address afterwards, because this adapter allocates no Elastic IP and does not claim the "
                + "StaticAddress capability. Anything pinned to the old address - a DNS record, a server list "
                + "entry, a player's favourites - has to be updated."));
        }

        if (tagChanges.Count > 0)
        {
            stages.Add(new ProvisioningStage(
                "retag-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Apply {tagChanges.Count} tag change(s) with CreateTags/DeleteTags so the instance carries the Servyx tag set the request describes: ")
                + string.Join("; ", tagChanges.Select(c => c.Description))
                + ". An EC2 tag is metadata on the resource; changing one does not stop, restart, or otherwise "
                + "touch the machine. It does change what an orphan sweep can find, which is why the Servyx "
                + "ownership tags are worth keeping accurate."));
        }

        stages.Add(new ProvisioningStage(
            "data-impact",
            Id,
            string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact}: ")
            + dataImpact switch
            {
                DataImpact.Destroyed =>
                    "terminating the instance deletes the EBS volume its data lives on, because the live "
                    + "instance reports DeleteOnTermination=true for it. Approving this plan is approving the "
                    + "deletion of everything stored on the machine - the installed game, its configuration, and "
                    + "every save file. Snapshot the volume first if any of it matters; this adapter cannot do "
                    + "that for you and does not claim the Snapshot capability.",
                DataImpact.AtRisk when instance.BlockDevices.Count == 0 =>
                    "the live instance reports no EBS volume at all, so it is instance-store backed. Such an "
                    + "instance cannot be stopped - which is the precondition ModifyInstanceAttribute needs - and "
                    + "its storage does not survive a stop in any case. This adapter cannot show any store being "
                    + "carried across, so it does not claim one is.",
                DataImpact.AtRisk when rootVolumeFate == false =>
                    "terminating the instance leaves its EBS volume behind rather than deleting it, because the "
                    + "live instance reports DeleteOnTermination=false. The bytes survive, but nothing will be "
                    + "attached to them: the replacement boots from a fresh volume. The old one keeps billing "
                    + "per GB-month, and is findable only because this adapter tags volumes at launch - see "
                    + "ReconcileAsync, which sweeps volumes separately for exactly this case.",
                DataImpact.AtRisk =>
                    "the live instance reports no DeleteOnTermination value for its EBS volume(s), and this "
                    + "adapter never set one - it sends no BlockDeviceMapping, so the flag is the AMI's own "
                    + "default. That means it cannot be shown that the data survives OR that it is deleted. "
                    + "AtRisk is not reassurance: check the flag on the volume before approving anything here.",
                _ =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"instance {instance.InstanceId} keeps its id and stays attached to {volumes}. No step above terminates the instance, detaches a volume, or writes to one."),
            }));

        return stages;
    }

    /// <summary>
    /// The single stage a plan carries when the request asks for a region an instance cannot be moved to.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>only</em> stage such a plan carries, even when the request also changes the type or
    /// the image. Listing a type change next to a refusal would suggest part of the plan is applicable, and it
    /// is not: the instance the other stages would act on is not the instance the request is describing. Every
    /// difference is still reported by name in <see cref="UpdatePlan.Changes"/>, so nothing is hidden — only the
    /// illusion of a partially-executable plan is.
    /// </remarks>
    private static IReadOnlyList<ProvisioningStage> BuildUnsupportedRegionStages(
        Ec2Instance instance,
        string desiredRegion,
        IReadOnlyList<PlannedChange> changes,
        bool? rootVolumeFate,
        DataImpact dataImpact)
    {
        var others = changes
            .Where(c => !string.Equals(c.Aspect, "region", StringComparison.Ordinal))
            .ToList();

        return
        [
            new(
                "region-change-not-supported",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"NOT SUPPORTED: the request asks for region '{desiredRegion}' and instance {instance.InstanceId} is in '{instance.AvailabilityZone ?? "(unknown zone)"}'. ")
                + "An EC2 instance cannot be moved between regions. No AWS operation relocates one, and this "
                + "provisioner could not reach another region even if one existed: the region is in the endpoint "
                + "hostname and in the SigV4 credential scope, so an instance elsewhere is not merely un-moveable "
                + "but unreachable from here. No operation is planned, and none will be. Reaching the requested "
                + "state would mean terminating this instance and launching a different one in the other region, "
                + "then reinstalling and restoring onto it. "
                + DescribeTerminationFate(rootVolumeFate, DescribeVolumes(instance))
                + " That is a decision for a person, not a step this planner will describe on their behalf. "
                + string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact} for that reason. ")
                + (others.Count == 0
                    ? "The region is the only difference the comparison found; nothing else about the instance needs to change."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"The other {others.Count} difference(s) found are equally not applied, because they describe a machine in a region this one cannot reach: ")
                      + string.Join("; ", others.Select(c => c.Description))
                      + ".")),
        ];
    }

    /// <summary>The EBS volumes a live instance has, named so a plan's claims about them can be checked.</summary>
    private static string DescribeVolumes(Ec2Instance instance) =>
        instance.BlockDevices.Count == 0
            ? "no EBS volume (this instance is instance-store backed)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the {instance.BlockDevices.Count} EBS volume(s) it has now ({string.Join(", ", instance.BlockDevices.Select(d => d.VolumeId))})");

    /// <summary>The plain-language fate of an instance's EBS volumes when the instance is terminated.</summary>
    private static string DescribeTerminationFate(bool? rootVolumeFate, string volumes) =>
        rootVolumeFate switch
        {
            true => "THIS DELETES THE MACHINE'S DISK: " + volumes + " report DeleteOnTermination=true, so EC2 "
                + "deletes them with the instance. The installed game, its configuration files, and every save "
                + "file are gone and cannot be recovered afterwards.",
            false => "The machine's disk survives the terminate: " + volumes + " report "
                + "DeleteOnTermination=false, so EC2 leaves them behind. The bytes remain, but the volumes are "
                + "attached to nothing afterwards and bill per GB-month until somebody deletes or reattaches "
                + "them; this adapter's orphan sweep can find them, because it tags volumes at launch.",
            _ => "What that does to the machine's disk cannot be determined from the live instance: it reports "
                + "no DeleteOnTermination value, and this adapter never sets one - it sends no BlockDeviceMapping "
                + "on RunInstances, so the flag is whatever the AMI defaults to. Treat the data as at risk and "
                + "check the volume before approving anything; the reassuring reading is the one that would need "
                + "evidence.",
        };

    /// <summary>
    /// The region a request asks for: its <c>region</c> parameter when it names one, otherwise this
    /// provisioner's own region. See <see cref="DesiredRegionParameter"/> for why this is read here and nowhere
    /// else.
    /// </summary>
    private string DesiredRegionOf(ProvisioningRequest request) =>
        request.Parameters is not null
        && request.Parameters.TryGetValue(DesiredRegionParameter, out var region)
        && !string.IsNullOrWhiteSpace(region)
            ? region
            : _region;

    /// <summary>Whether an id names an EC2 instance rather than a volume, or nothing at all.</summary>
    private static bool IsInstanceId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.StartsWith("i-", StringComparison.Ordinal);

    /// <summary>
    /// Reads one recorded expectation out of a handle's tags, treating a blank value as no expectation at all
    /// rather than as an expectation of emptiness.
    /// </summary>
    private static string? RecordedExpectation(IReadOnlyDictionary<string, string> recorded, string key) =>
        recorded.TryGetValue(key, out var value) ? NullIfBlank(value) : null;

    /// <summary>
    /// Whether a tag key is one of the two descriptive keys that carry an expectation reported under its own
    /// aspect (<c>size</c>, <c>image</c>) rather than as an ordinary tag.
    /// </summary>
    private static bool IsDescriptiveExpectationKey(string key) =>
        string.Equals(key, ServyxTagKeys.Size, StringComparison.Ordinal)
        || string.Equals(key, ServyxTagKeys.Image, StringComparison.Ordinal);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private string ComputeUpdatePlanHash(
        Ec2Instance instance,
        string? liveSize,
        string? liveImage,
        string desiredRegion,
        AwsEc2InstanceSpec spec,
        IReadOnlyDictionary<string, string> desiredTags,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(instance.InstanceId).Append('\n');
        builder.Append(liveSize ?? string.Empty).Append('\n');
        builder.Append(liveImage ?? string.Empty).Append('\n');
        builder.Append(_region).Append('\n');
        builder.Append(desiredRegion).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var device in instance.BlockDevices)
        {
            builder.Append(CultureInfo.InvariantCulture, $"volume {device.VolumeId} delete-on-termination={device.DeleteOnTermination?.ToString() ?? "unreported"}\n");
        }

        foreach (var tag in instance.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-tag {tag.Key}={tag.Value}\n");
        }

        builder.Append(ComputePlanHash(spec, desiredTags)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
