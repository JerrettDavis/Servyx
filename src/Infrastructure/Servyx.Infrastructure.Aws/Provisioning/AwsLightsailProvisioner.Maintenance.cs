using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the Lightsail adapter: it reads a live instance and describes what
/// would have to happen to it. Nothing here changes anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The finding of this file, stated first because it is the one that surprises.</strong> Lightsail
/// looks like DigitalOcean everywhere else in this adapter, and here it is <em>less</em> capable than
/// DigitalOcean, EC2 and Azure alike: <strong>there is no operation that changes an existing Lightsail
/// instance's bundle.</strong> DigitalOcean has a resize action, EC2 has <c>ModifyInstanceAttribute</c>, ARM
/// has a write to <c>hardwareProfile.vmSize</c> — Lightsail has none of them for an instance. AWS does offer
/// <c>UpdateRelationalDatabase</c>, <c>UpdateBucketBundle</c> and container-service power changes, and their
/// existence is exactly what makes the absence of an instance equivalent worth naming rather than assuming.
/// What AWS actually documents for "resize a Lightsail instance" is a <em>procedure</em>, not an operation:
/// <c>CreateInstanceSnapshot</c>, then <c>CreateInstancesFromSnapshot</c> at the larger bundle, then move the
/// static IP across and delete the original — a sequence that produces a <em>different instance</em>, only ever
/// scales upward (a snapshot cannot be restored onto a smaller bundle), and needs two API calls this adapter
/// does not make and a <see cref="ProvisioningCapabilities.Snapshot"/> capability it does not claim. So a
/// bundle change is reported as <strong>not supported</strong> and nothing is planned for it. Substituting
/// "delete it and create a new one at the bigger bundle" would be a plan to destroy a machine, presented as a
/// plan to resize one — and unlike the snapshot procedure, that substitution really would lose every save file.
/// The procedure is named in the refusal stage so an operator can carry it out deliberately, which is where a
/// decision of that shape belongs.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Blueprint (image) — a replacement, and unambiguously a destructive one.</strong> An instance's
/// blueprint is fixed by <c>CreateInstances</c>; Lightsail exposes nothing that changes it, so the only route is
/// <c>DeleteInstance</c> followed by a fresh <c>CreateInstances</c>. What that costs needs no flag lookup, and
/// this is the sharpest contrast with EC2 in the whole adapter: a Lightsail bundle <em>bakes the SSD storage
/// into the instance</em>. There is no separate disk resource, no <c>DeleteOnTermination</c> equivalent, and
/// nothing that can outlive the instance — which is why this adapter's sweep looks for one kind of object where
/// the EC2 sweep has to look for two. The system disk dies with the instance, always. That is
/// <see cref="DataImpact.Destroyed"/>, asserted with certainty rather than read off a flag.
/// </description></item>
/// <item><description>
/// <strong>Region and availability zone — not possible either.</strong> Lightsail cannot move an instance
/// between regions or between zones, and this adapter cannot reach another region at all: the region is in the
/// endpoint hostname and in the SigV4 credential scope. Both are reported and neither is planned.
/// </description></item>
/// <item><description>
/// <strong>Tags — the only thing that is genuinely in place.</strong> <c>TagResource</c>/<c>UntagResource</c>
/// edit an existing instance's tags without touching the machine. That single operation is the entire backing
/// for this adapter's <see cref="ProvisioningCapabilities.UpdateInPlace"/> claim, which is a materially weaker
/// claim than the EC2 adapter's identically-spelled bit — and the difference is the bundle, as above.
/// </description></item>
/// </list>
/// <para>
/// <strong>Where the desired region and zone come from.</strong> <see cref="BuildSpec"/> reads
/// <c>availabilityZone</c> and deliberately has no <c>region</c> key; <see cref="PlanUpdateAsync"/> reads a
/// <c>region</c> parameter anyway, for the same reason it compares the zone: a placement a caller named must be
/// <em>reported</em> as unreachable rather than silently discarded behind a plan that appears to satisfy the
/// request.
/// </para>
/// <para>
/// <strong>Nothing in <em>this file</em> executes.</strong> Every call on both paths is a <c>GetInstance</c>
/// read. There is no <c>CreateInstances</c>, no <c>DeleteInstance</c>, no <c>TagResource</c> and no snapshot
/// call anywhere in it. What has changed is what happens to a plan afterwards: the tag case above is now
/// executable, by the <see cref="IUpdateApplier"/> implementation in
/// <c>AwsLightsailProvisioner.Tags.cs</c> — which is the whole of this adapter's
/// <see cref="ProvisioningCapabilities.UpdateInPlace"/> backing, because the tag case is the whole of what
/// Lightsail can change in place. Every other plan this file can produce is refused there, without a request,
/// and three of the four are refused permanently rather than pending an implementation: AWS publishes no
/// operation that changes an existing instance's bundle, region or zone.
/// </para>
/// </remarks>
public sealed partial class AwsLightsailProvisioner : IMaintainer
{
    /// <summary>How long an update plan's observed live state should be trusted for.</summary>
    /// <remarks>The same fifteen minutes <see cref="BuildPlan"/> gives a provisioning plan, for the same reason.</remarks>
    private const int UpdatePlanLifetimeMinutes = 15;

    /// <summary>
    /// The provisioning parameter naming the region a request wants, read only here.
    /// </summary>
    /// <remarks>
    /// <see cref="BuildSpec"/> recognises no such key and never will — see its remarks. This planner reads it so
    /// a caller who names a region can be told the instance cannot be moved there, instead of having the
    /// parameter silently discarded.
    /// </remarks>
    internal const string DesiredRegionParameter = "region";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads one instance, then computes.</strong> The only request on this path is a
    /// <c>GetInstance</c> call for the name the handle carries. An instance Lightsail no longer knows
    /// (<c>NotFoundException</c>), or a handle carrying no name at all, yields <see langword="null"/> —
    /// mirroring <see cref="RefreshAsync"/>, and deliberately not the same answer as "nothing needs to change".
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided.</strong> A plan whose only differences are the
    /// instance's tags is <see cref="DataImpact.Preserved"/>: <c>TagResource</c> writes metadata on the resource
    /// and the instance that exists afterwards is this instance, still on its own system disk. Everything else
    /// this adapter can find — a blueprint change, a bundle change, a region or zone change — reaches the
    /// desired state only by deleting this instance, and deleting a Lightsail instance deletes the bundled
    /// system disk with it, because the bundle <em>is</em> the disk. So every one of those is
    /// <see cref="DataImpact.Destroyed"/>. There is no <see cref="DataImpact.AtRisk"/> branch here at all, and
    /// its absence is a fact about Lightsail rather than an omission: the EC2 adapter needs that value because
    /// an EBS volume can outlive its instance, and nothing Lightsail creates can.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId))
        {
            return null;
        }

        var instance = await _api.GetInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        return instance is null ? null : BuildUpdatePlan(instance, BuildSpec(desired), DesiredRegionOf(desired));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>What is compared, and where each expectation comes from.</strong> Four aspects, with the strength
    /// of each made visible rather than smoothed over:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>region</c> — compared against <see cref="ResourceHandle.Region"/>, which this adapter stamps on every
    /// handle it produces. The live value is not a guess: an instance answered for by this provisioner's
    /// endpoint is in this provisioner's region, because the region is in the hostname.
    /// </description></item>
    /// <item><description>
    /// <c>tag &lt;key&gt;</c> — every entry in <see cref="ResourceHandle.Tags"/> is looked for on the live
    /// instance. The check that catches an instance whose Servyx ownership tags were edited away at the
    /// provider, which matters because <see cref="ReconcileAsync"/> finds orphans by exactly those tags.
    /// </description></item>
    /// <item><description>
    /// <c>size</c> and <c>image</c> — the live bundle and blueprint, read off the instance, compared against
    /// expectations that can only live in the handle's tags under <see cref="ServyxTagKeys.Size"/> and
    /// <see cref="ServyxTagKeys.Image"/>, since a <see cref="ResourceHandle"/> has no field for either. A handle
    /// recording neither reports both as divergences with a null <see cref="DriftDivergence.Expected"/> rather
    /// than as matches: a check that cannot prove a match must not claim one. This adapter does not stamp those
    /// tags at create time — that would change a write path — so a caller wanting the strong answer supplies
    /// them as ordinary <c>tag:servyx.size</c> / <c>tag:servyx.image</c> provisioning parameters.
    /// </description></item>
    /// </list>
    /// <para>
    /// An instance Lightsail no longer has is reported as drift under <c>existence</c>, never as an exception
    /// and never as a match. Note the divergence from EC2, where a deleted instance keeps being described for
    /// about an hour and "gone" therefore has to be checked as a state: Lightsail's documented answer for a
    /// deleted instance name is <c>NotFoundException</c>, so there is one spelling of gone here rather than two.
    /// If that is ever found to be wrong, this check needs the same state filter EC2's has.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId))
        {
            return new DriftResult(
                handle,
                [new DriftDivergence("instance-name", "a Lightsail instance name", null)]);
        }

        var instance = await _api.GetInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return new DriftResult(handle, [new DriftDivergence("existence", "present", null)]);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var divergences = new List<DriftDivergence>();

        if (!string.Equals(NullIfBlank(handle.Region), _region, StringComparison.OrdinalIgnoreCase))
        {
            divergences.Add(new DriftDivergence("region", NullIfBlank(handle.Region), _region));
        }

        var recordedSize = RecordedExpectation(recorded, ServyxTagKeys.Size);
        var liveSize = NullIfBlank(instance.BundleId);
        if (!string.Equals(recordedSize, liveSize, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("size", recordedSize, liveSize));
        }

        var recordedImage = RecordedExpectation(recorded, ServyxTagKeys.Image);
        var liveImage = NullIfBlank(instance.BlueprintId);
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
    private UpdatePlan BuildUpdatePlan(LightsailInstance instance, AwsLightsailInstanceSpec spec, string desiredRegion)
    {
        var liveSize = NullIfBlank(instance.BundleId);
        var liveImage = NullIfBlank(instance.BlueprintId);
        var liveZone = NullIfBlank(instance.AvailabilityZone);
        var desiredTags = TagsFor(spec);

        var bundleChanged = !string.Equals(liveSize, spec.Machine.SizeRef, StringComparison.Ordinal);
        var blueprintChanged = !string.Equals(liveImage, spec.Machine.ImageRef, StringComparison.Ordinal);
        var regionChanged = !string.Equals(_region, desiredRegion, StringComparison.OrdinalIgnoreCase);
        var zoneChanged = !string.Equals(liveZone, spec.AvailabilityZone, StringComparison.OrdinalIgnoreCase);

        var changes = new List<PlannedChange>();

        if (bundleChanged)
        {
            // There is no in-place bundle change. Not "requires a recreate this adapter will perform" - requires
            // a snapshot-and-restore procedure it refuses to plan, and which produces a different instance.
            changes.Add(new PlannedChange("size", liveSize, spec.Machine.SizeRef, RequiresRecreate: true));
        }

        if (blueprintChanged)
        {
            // CreateInstances fixes the blueprint. Reaching a different one replaces the instance.
            changes.Add(new PlannedChange("image", liveImage, spec.Machine.ImageRef, RequiresRecreate: true));
        }

        if (regionChanged)
        {
            changes.Add(new PlannedChange("region", _region, desiredRegion, RequiresRecreate: true));
        }

        if (zoneChanged)
        {
            changes.Add(new PlannedChange("availabilityZone", liveZone, spec.AvailabilityZone, RequiresRecreate: true));
        }

        var tagChanges = new List<PlannedChange>();
        foreach (var desired in desiredTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (IsDescriptiveExpectationKey(desired.Key))
            {
                continue;
            }

            var current = instance.Tags.TryGetValue(desired.Key, out var value) ? value : null;
            if (!string.Equals(current, desired.Value, StringComparison.Ordinal))
            {
                // TagResource/UntagResource edit an instance's tags without touching the machine.
                tagChanges.Add(new PlannedChange($"tag {desired.Key}", current, desired.Value, RequiresRecreate: false));
            }
        }

        changes.AddRange(tagChanges);

        var strategy = changes.Count == 0
            ? UpdateStrategy.NoChangeRequired
            : changes.Any(c => c.RequiresRecreate)
                ? UpdateStrategy.Recreate
                : UpdateStrategy.InPlace;

        var unsupported = bundleChanged || regionChanged || zoneChanged;
        var dataImpact = AssertDataImpact(strategy, blueprintChanged, unsupported);

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : unsupported
                ? BuildUnsupportedStages(instance, spec, desiredRegion, liveSize, liveZone, bundleChanged, regionChanged, zoneChanged, changes, dataImpact)
                : BuildUpdateStages(instance, spec, liveImage, blueprintChanged, tagChanges, dataImpact);

        var planHash = ComputeUpdatePlanHash(instance, liveSize, liveImage, liveZone, desiredRegion, spec, desiredTags, strategy, dataImpact);

        return new UpdatePlan(
            planId: string.Create(CultureInfo.InvariantCulture, $"{Id}:update:{instance.Name}:{planHash[..12]}"),
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(UpdatePlanLifetimeMinutes));
    }

    /// <summary>
    /// The deliberate data-impact assertion, derived from the Lightsail operation each difference would require
    /// — never from a default. See the remarks on <see cref="PlanUpdateAsync"/> for why no branch here answers
    /// <see cref="DataImpact.AtRisk"/>.
    /// </summary>
    private static DataImpact AssertDataImpact(UpdateStrategy strategy, bool blueprintChanged, bool unsupported)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the disk.
            return DataImpact.Preserved;
        }

        if (unsupported || blueprintChanged)
        {
            // Every one of these routes ends in DeleteInstance, and a Lightsail instance's system disk is part
            // of the instance - the bundle price includes it, no separate resource exists, and nothing survives
            // the delete. Unlike EC2 there is no flag to look up and no way for the bytes to remain behind, so
            // this is asserted with certainty rather than read off the live resource.
            return DataImpact.Destroyed;
        }

        // Everything left is a TagResource/UntagResource call. It writes metadata on the resource and the
        // instance that exists afterwards is this instance, still on the same system disk.
        return DataImpact.Preserved;
    }

    /// <summary>The stages of an update Lightsail can actually perform: a replacement, a retag, or both.</summary>
    private static IReadOnlyList<ProvisioningStage> BuildUpdateStages(
        LightsailInstance instance,
        AwsLightsailInstanceSpec spec,
        string? liveImage,
        bool blueprintChanged,
        IReadOnlyList<PlannedChange> tagChanges,
        DataImpact dataImpact)
    {
        var stages = new List<ProvisioningStage>();

        if (blueprintChanged)
        {
            stages.Add(new ProvisioningStage(
                "delete-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Delete instance '{instance.Name}'. Lightsail has no operation that changes an existing instance's blueprint: it is fixed by the CreateInstances call that created the instance, so moving from '{liveImage ?? "(unknown)"}' to '{spec.Machine.ImageRef}' means deleting this instance and creating another with the same name. ")
                + "THIS DELETES THE MACHINE'S DISK. A Lightsail bundle bakes the SSD storage into the instance - "
                + "there is no separate disk resource, and no DeleteOnTermination-style flag that could leave it "
                + "behind, as there is on EC2. The installed game, its configuration files, and every save file "
                + "are deleted with the instance and cannot be recovered afterwards. The instance's public IPv4 "
                + "address goes too: it is not static, and this adapter attaches no static IP."));

            stages.Add(new ProvisioningStage(
                "create-replacement-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Create a replacement instance named '{spec.InstanceName}' in availability zone '{spec.AvailabilityZone}' from blueprint '{spec.Machine.ImageRef}' at bundle '{spec.Machine.SizeRef}', carrying the same Servyx tags. ")
                + "The name is reused because a Lightsail instance's name is its identity and the delete above "
                + "frees it, but nothing else carries over: the replacement boots from a fresh copy of the "
                + "blueprint with none of the previous machine's files on it and a different public address, so "
                + "the game would have to be installed and restored again."));
        }

        if (tagChanges.Count > 0)
        {
            stages.Add(new ProvisioningStage(
                "retag-instance",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Apply {tagChanges.Count} tag change(s) with TagResource/UntagResource so the instance carries the Servyx tag set the request describes: ")
                + string.Join("; ", tagChanges.Select(c => c.Description))
                + ". A Lightsail tag is metadata on the resource; changing one does not stop, restart, or "
                + "otherwise touch the machine. It does change what an orphan sweep can find, which is why the "
                + "Servyx ownership tags are worth keeping accurate."));
        }

        stages.Add(new ProvisioningStage(
            "data-impact",
            Id,
            string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact}: ")
            + (dataImpact == DataImpact.Destroyed
                ? "replacing the instance deletes its bundled system disk, so approving this plan is approving "
                  + "the deletion of everything stored on the machine. Take an instance snapshot first if any of "
                  + "it matters; this adapter cannot take one for you and does not claim the Snapshot capability."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"instance '{instance.Name}' keeps its name, its address and the system disk it has now. No step above deletes the instance, and nothing else can reach that disk."))));

        return stages;
    }

    /// <summary>
    /// The single stage a plan carries when the request asks for something no Lightsail operation can do to an
    /// existing instance: a different bundle, a different region, or a different availability zone.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>only</em> stage such a plan carries, even when the request also changes the
    /// blueprint. Listing a replacement next to a refusal would suggest part of the plan is applicable, and it
    /// is not: the instance the other stages would act on is not the instance the request is describing. Every
    /// difference is still reported by name in <see cref="UpdatePlan.Changes"/>, so nothing is hidden — only the
    /// illusion of a partially-executable plan is.
    /// </remarks>
    private static IReadOnlyList<ProvisioningStage> BuildUnsupportedStages(
        LightsailInstance instance,
        AwsLightsailInstanceSpec spec,
        string desiredRegion,
        string? liveSize,
        string? liveZone,
        bool bundleChanged,
        bool regionChanged,
        bool zoneChanged,
        IReadOnlyList<PlannedChange> changes,
        DataImpact dataImpact)
    {
        var reasons = new List<string>();

        if (bundleChanged)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the request asks for bundle '{spec.Machine.SizeRef}' and instance '{instance.Name}' is on '{liveSize ?? "(unknown)"}', and Lightsail has NO operation that changes an existing instance's bundle at all - there is no instance equivalent of UpdateRelationalDatabase or UpdateBucketBundle, so this is an absent operation rather than an unimplemented one"));
        }

        if (regionChanged)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the request asks for region '{desiredRegion}' and this provisioner acts on '{instance.Name}' in '{liveZone ?? "(unknown zone)"}', and a Lightsail instance cannot be moved between regions - nor could this provisioner reach another one, since the region is in the endpoint hostname and in the SigV4 credential scope"));
        }

        if (zoneChanged)
        {
            reasons.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"the request asks for availability zone '{spec.AvailabilityZone}' and the instance is in '{liveZone ?? "(unknown)"}', and Lightsail exposes no operation that moves an existing instance between zones"));
        }

        var others = changes
            .Where(c => !string.Equals(c.Aspect, "size", StringComparison.Ordinal)
                && !string.Equals(c.Aspect, "region", StringComparison.Ordinal)
                && !string.Equals(c.Aspect, "availabilityZone", StringComparison.Ordinal))
            .ToList();

        return
        [
            new(
                "change-not-supported",
                Id,
                "NOT SUPPORTED: " + string.Join("; and ", reasons) + ". "
                + "No operation is planned here, and none will be. "
                + (bundleChanged
                    ? "What AWS documents for changing a Lightsail instance's bundle is a procedure rather than "
                      + "an operation: take an instance snapshot with CreateInstanceSnapshot, create a NEW "
                      + "instance from it with CreateInstancesFromSnapshot at the larger bundle, move the static "
                      + "IP across, and delete the original. That produces a different instance, only ever "
                      + "scales upward - a snapshot cannot be restored onto a smaller bundle - and needs two "
                      + "calls this adapter does not make and the Snapshot capability it does not claim. It is "
                      + "named here so an operator can carry it out deliberately; it is not planned, and the "
                      + "cheaper substitute of deleting the instance and creating a bigger one is not planned "
                      + "either, because that would lose every save file while looking like a resize. "
                    : string.Empty)
                + "Reaching the requested state through this adapter would mean deleting this instance - and its "
                + "bundled system disk with it, since the bundle IS the disk and nothing survives a Lightsail "
                + "delete - and creating a different one, then reinstalling and restoring onto it. That is a "
                + "decision for a person, not a step this planner will describe on their behalf. "
                + string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact} for that reason. ")
                + (others.Count == 0
                    ? "Nothing else about the instance needs to change."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"The other {others.Count} difference(s) found are equally not applied, because they describe an instance this one cannot become: ")
                      + string.Join("; ", others.Select(c => c.Description))
                      + ".")),
        ];
    }

    /// <summary>
    /// The region a request asks for: its <c>region</c> parameter when it names one, otherwise this
    /// provisioner's own region. See <see cref="DesiredRegionParameter"/> for why this is read here.
    /// </summary>
    private string DesiredRegionOf(ProvisioningRequest request) =>
        request.Parameters is not null
        && request.Parameters.TryGetValue(DesiredRegionParameter, out var region)
        && !string.IsNullOrWhiteSpace(region)
            ? region
            : _region;

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
        LightsailInstance instance,
        string? liveSize,
        string? liveImage,
        string? liveZone,
        string desiredRegion,
        AwsLightsailInstanceSpec spec,
        IReadOnlyDictionary<string, string> desiredTags,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(instance.Name).Append('\n');
        builder.Append(liveSize ?? string.Empty).Append('\n');
        builder.Append(liveImage ?? string.Empty).Append('\n');
        builder.Append(liveZone ?? string.Empty).Append('\n');
        builder.Append(_region).Append('\n');
        builder.Append(desiredRegion).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var tag in instance.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-tag {tag.Key}={tag.Value}\n");
        }

        builder.Append(ComputePlanHash(spec, desiredTags)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
