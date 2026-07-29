using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Provisioning;

/// <summary>
/// The <see cref="IMaintainer"/> half of the droplet adapter: it reads a live droplet and describes what
/// would have to happen to it. Nothing here changes anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before changing a single string in this file.</strong> For the container adapter an
/// update recreates a container and the volumes survive; for the SSH adapter an update re-runs the install
/// verbs and the data directory survives. Neither adapter has a plan that can delete a user's saves. Here the
/// machine and its disk are the same object, so two of the three differences this adapter can find are
/// unrecoverable and one is not, and telling them apart is the entire value of the type:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Size.</strong> DigitalOcean's resize action takes a <c>disk</c> boolean.
/// <c>disk: false</c> changes the CPU and RAM allocation only; the boot disk is untouched and the operation
/// is reversible. <c>disk: true</c> additionally grows the disk, is irreversible, and permanently pins the
/// droplet to a size class that can never be reduced again. <strong>This adapter plans only the
/// <c>disk: false</c> form</strong>, and every plan it produces says so in the stage text. If the target
/// size needs a bigger disk than the droplet has, DigitalOcean refuses a <c>disk: false</c> resize — and
/// that refusal is the intended outcome, not a gap: an operator who wants the irreversible form should
/// choose it deliberately somewhere that is not an automated planner.
/// </description></item>
/// <item><description>
/// <strong>Image.</strong> There is no DigitalOcean call that swaps a running droplet's image. The operation
/// that changes it is <em>rebuild</em>, which reimages the boot disk: the installed game, its configuration
/// and every save file on the machine are gone, and no part of that is recoverable from the droplet
/// afterwards. That is <see cref="DataImpact.Destroyed"/>, and it is the first plan in this codebase that
/// asserts it.
/// </description></item>
/// <item><description>
/// <strong>Region.</strong> A droplet cannot move. DigitalOcean exposes no operation of any kind that
/// relocates an existing droplet to another region, so this adapter reports the difference and plans
/// <em>nothing</em> — see <see cref="BuildUpdatePlan"/>. Quietly substituting "destroy it and make a new one
/// over there" would be a plan to delete a machine, presented as a plan to move one.
/// </description></item>
/// </list>
/// <para>
/// <strong>Nothing here executes, and there is no path from here to something that does.</strong> This file
/// issues exactly one HTTP request per call — <c>GET /v2/droplets/{id}</c> — and it is a read. There is no
/// call to <c>POST /v2/droplets/{id}/actions</c> anywhere in this assembly, so no resize and no rebuild is
/// reachable from any code Servyx ships, whatever an <see cref="UpdatePlan"/> says. Applying a plan that
/// destroys a cloud machine's disk deserves its own reviewed change; this is the half that can be shipped
/// without one.
/// </para>
/// </remarks>
public sealed partial class DigitalOceanDropletProvisioner : IMaintainer
{
    /// <summary>How long an update plan's observed live state should be trusted for.</summary>
    /// <remarks>The same fifteen minutes <see cref="BuildPlan"/> gives a provisioning plan, for the same reason.</remarks>
    private const int UpdatePlanLifetimeMinutes = 15;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads one droplet, then computes.</strong> The only request on this path is
    /// <c>GET /v2/droplets/{id}</c>. A droplet DigitalOcean no longer has, or a handle whose
    /// <see cref="ResourceHandle.ProviderResourceId"/> is not a droplet id at all, yields
    /// <see langword="null"/> — mirroring <see cref="RefreshAsync"/>, and deliberately not the same answer as
    /// "nothing needs to change".
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided, per difference, from the operation that would
    /// actually be issued.</strong> A plan whose only differences are the size and the droplet's tags is
    /// <see cref="DataImpact.Preserved"/>: the resize it describes is the <c>disk: false</c> form, which
    /// alters the CPU and RAM allocation and does not write to the boot disk, and attaching or detaching a
    /// DigitalOcean tag does not touch the machine at all — so the droplet keeps its id, its address and the
    /// exact disk it had, whose size this method reads off the live droplet and states in the plan. A plan
    /// that changes the image is <see cref="DataImpact.Destroyed"/>, because the only operation that changes
    /// a droplet's image reimages its disk. A plan that changes the region is
    /// <see cref="DataImpact.Destroyed"/> too, and for a blunter reason: there is no operation at all, and
    /// the only route to the desired state would be to destroy this machine — which destroys its disk, as
    /// <see cref="DestroyAsync"/>'s remarks already state — and build another elsewhere. When more than one
    /// difference is present the worst answer wins; a resize planned alongside a rebuild does not soften the
    /// rebuild.
    /// </para>
    /// <para>
    /// <strong>What forces a recreate, and why the region case still produces a plan rather than an
    /// exception.</strong> A size or tag difference sets no <see cref="PlannedChange.RequiresRecreate"/>
    /// flag; an image or region difference sets it, so the plan can only describe itself as
    /// <see cref="UpdateStrategy.Recreate"/>. For the region case that strategy is a statement of what
    /// reaching the desired state would cost, not a promise that this adapter will do it: the plan's single
    /// stage is a <c>NOT SUPPORTED</c> stage, no stage describes an operation, and the change is reported by
    /// name so a caller can see precisely which difference it is that cannot be applied.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        if (!TryReadDropletId(handle.ProviderResourceId, out var dropletId))
        {
            return null;
        }

        var droplet = await _api.GetDropletAsync(dropletId, ct).ConfigureAwait(false);
        return droplet is null ? null : BuildUpdatePlan(droplet, BuildSpec(desired));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>What is compared, and where each expectation comes from.</strong> Four aspects, and they do
    /// not all have equally strong records behind them, so the difference is made visible rather than
    /// smoothed over:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>region</c> — compared against <see cref="ResourceHandle.Region"/>, which this adapter stamps on
    /// every handle it produces. A real recorded expectation.
    /// </description></item>
    /// <item><description>
    /// <c>tag &lt;key&gt;</c> — every entry in <see cref="ResourceHandle.Tags"/> is looked for on the live
    /// droplet, decoded back out of DigitalOcean's flat tag array. Also a real recorded expectation, and the
    /// one that catches a droplet whose Servyx ownership tags were edited away at the provider.
    /// </description></item>
    /// <item><description>
    /// <c>size</c> and <c>image</c> — read from the live droplet directly, but their <em>expectations</em>
    /// can only live in the handle's tags, under <see cref="ServyxTagKeys.Size"/> and
    /// <see cref="ServyxTagKeys.Image"/>, because a <see cref="ResourceHandle"/> has no field for either. A
    /// handle that records neither reports both as divergences with a null
    /// <see cref="DriftDivergence.Expected"/> — "Servyx recorded no expected value, found s-2vcpu-4gb" —
    /// rather than as matches, for the reason <see cref="DriftDivergence.Expected"/> gives: a check that
    /// cannot prove a match must not claim one. This adapter does not stamp those two tags itself at create
    /// time (that would change the create request, which is a write path and out of scope for a change that
    /// adds only reads); a caller that wants the strong answer supplies them as ordinary
    /// <c>tag:servyx.size</c> / <c>tag:servyx.image</c> provisioning parameters, which
    /// <see cref="BuildSpec"/> already carries onto the droplet and hence back onto the handle.
    /// </description></item>
    /// </list>
    /// <para>
    /// A droplet DigitalOcean no longer has is reported as drift under <c>existence</c> and never as an
    /// exception or as a match: a machine that has vanished is the loudest drift there is, and for a
    /// per-hour billed resource its disappearance is exactly what a caller is asking about. A handle
    /// belonging to another provisioner, or one whose id is not a droplet id, is answered without touching
    /// the API — but as a divergence, since "this is not my resource" is not evidence that it is intact.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        if (!TryReadDropletId(handle.ProviderResourceId, out var dropletId))
        {
            return new DriftResult(
                handle,
                [new DriftDivergence("droplet-id", "a numeric DigitalOcean droplet id", NullIfBlank(handle.ProviderResourceId))]);
        }

        var droplet = await _api.GetDropletAsync(dropletId, ct).ConfigureAwait(false);
        if (droplet is null)
        {
            return new DriftResult(handle, [new DriftDivergence("existence", "present", null)]);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var live = ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags);
        var divergences = new List<DriftDivergence>();

        var liveRegion = NullIfBlank(droplet.Region?.Slug);
        if (!string.Equals(NullIfBlank(handle.Region), liveRegion, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("region", NullIfBlank(handle.Region), liveRegion));
        }

        var recordedSize = RecordedExpectation(recorded, ServyxTagKeys.Size);
        var liveSize = NullIfBlank(droplet.SizeSlug);
        if (!string.Equals(recordedSize, liveSize, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("size", recordedSize, liveSize));
        }

        var recordedImage = RecordedExpectation(recorded, ServyxTagKeys.Image);
        var liveImage = LiveImageRef(droplet);
        if (!string.Equals(recordedImage, liveImage, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("image", recordedImage, liveImage));
        }

        // The size and image tags are the *source* of the two expectations above, so re-reporting them here
        // would describe one divergence twice - and would compare a tag against a tag rather than against the
        // droplet, which is the weaker of the two checks.
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
    /// The whole of update planning: pure comparison between an already-fetched droplet and the desired spec.
    /// Touches only <see cref="_timeProvider"/> (for the plan's expiry) and never <see cref="_api"/>, so every
    /// request on the update path is the single read its caller already made.
    /// </summary>
    private UpdatePlan BuildUpdatePlan(DropletResource droplet, DigitalOceanDropletSpec spec)
    {
        var liveSize = NullIfBlank(droplet.SizeSlug);
        var liveImage = LiveImageRef(droplet);
        var liveRegion = NullIfBlank(droplet.Region?.Slug);
        var liveTags = ServyxDropletTags.FromDropletTagsToDictionary(droplet.Tags);
        var desiredTags = TagsFor(spec);

        var sizeChanged = !string.Equals(liveSize, spec.Machine.SizeRef, StringComparison.Ordinal);
        var imageChanged = !string.Equals(liveImage, spec.Machine.ImageRef, StringComparison.Ordinal);
        var regionChanged = !string.Equals(liveRegion, spec.Machine.Region, StringComparison.Ordinal);

        var changes = new List<PlannedChange>();

        if (sizeChanged)
        {
            // A resize acts on the droplet in place: same id, same address, same disk.
            changes.Add(new PlannedChange("size", liveSize, spec.Machine.SizeRef, RequiresRecreate: false));
        }

        if (imageChanged)
        {
            // There is no call that swaps a droplet's image. The one that changes it reimages the disk.
            changes.Add(new PlannedChange("image", liveImage, spec.Machine.ImageRef, RequiresRecreate: true));
        }

        if (regionChanged)
        {
            // Not "requires a recreate this adapter will perform" - requires one it refuses to plan.
            changes.Add(new PlannedChange("region", liveRegion, spec.Machine.Region, RequiresRecreate: true));
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
                // Attaching or detaching a DigitalOcean tag does not touch the machine.
                tagChanges.Add(new PlannedChange($"tag {desired.Key}", current, desired.Value, RequiresRecreate: false));
            }
        }

        changes.AddRange(tagChanges);

        var strategy = changes.Count == 0
            ? UpdateStrategy.NoChangeRequired
            : changes.Any(c => c.RequiresRecreate)
                ? UpdateStrategy.Recreate
                : UpdateStrategy.InPlace;

        var dataImpact = AssertDataImpact(strategy, imageChanged, regionChanged);

        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : regionChanged
                ? BuildUnsupportedRegionStages(droplet, spec, changes, dataImpact)
                : BuildUpdateStages(droplet, spec, liveSize, liveImage, imageChanged, sizeChanged, tagChanges, dataImpact);

        var planHash = ComputeUpdatePlanHash(droplet, liveSize, liveImage, liveRegion, liveTags, spec, desiredTags, strategy, dataImpact);

        return new UpdatePlan(
            planId: string.Create(CultureInfo.InvariantCulture, $"{Id}:update:{droplet.Id}:{planHash[..12]}"),
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(UpdatePlanLifetimeMinutes));
    }

    /// <summary>
    /// The deliberate data-impact assertion, derived from the DigitalOcean operation each difference would
    /// require rather than from a default. Every branch is a claim this adapter can defend from the API's own
    /// semantics — see the remarks on <see cref="PlanUpdateAsync"/>.
    /// </summary>
    private static DataImpact AssertDataImpact(UpdateStrategy strategy, bool imageChanged, bool regionChanged)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the disk.
            return DataImpact.Preserved;
        }

        if (regionChanged)
        {
            // No operation exists. The only route to the desired state destroys this droplet, and destroying a
            // droplet destroys its boot disk - see DestroyAsync's remarks, which say so about the same machine.
            return DataImpact.Destroyed;
        }

        if (imageChanged)
        {
            // A rebuild reimages the boot disk. Everything the machine had written is deleted.
            return DataImpact.Destroyed;
        }

        // Everything left is a disk:false resize and/or a tag attach. Neither writes to the boot disk, and the
        // droplet that exists afterwards is the same droplet, still attached to the same disk. That is what
        // Preserved requires, and it is asserted from the operation being planned rather than from optimism.
        return DataImpact.Preserved;
    }

    /// <summary>
    /// The stages of an update that DigitalOcean can actually perform: a rebuild, a resize, a retag, or some
    /// combination.
    /// </summary>
    private static IReadOnlyList<ProvisioningStage> BuildUpdateStages(
        DropletResource droplet,
        DigitalOceanDropletSpec spec,
        string? liveSize,
        string? liveImage,
        bool imageChanged,
        bool sizeChanged,
        IReadOnlyList<PlannedChange> tagChanges,
        DataImpact dataImpact)
    {
        var stages = new List<ProvisioningStage>();
        var name = NullIfBlank(droplet.Name) ?? spec.DropletName;
        var disk = droplet.Disk is { } gigabytes
            ? string.Create(CultureInfo.InvariantCulture, $"{gigabytes} GB")
            : "an unreported";

        if (imageChanged)
        {
            stages.Add(new ProvisioningStage(
                "rebuild-droplet",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Rebuild droplet '{name}' (id {droplet.Id}) from image '{spec.Machine.ImageRef}', replacing image '{liveImage ?? "(unknown)"}'. ")
                + "THIS ERASES THE DROPLET'S DISK. Everything currently stored on the machine - the installed "
                + "game, its configuration files, and every save file - is deleted and replaced by a fresh copy "
                + "of the image. It cannot be recovered afterwards from the droplet, because there is nothing "
                + "left of it there. The droplet keeps its id and its IP address; nothing else about it "
                + "survives. DigitalOcean has no other operation that changes a droplet's image, so there is no "
                + "gentler version of this step to plan instead."));
        }

        if (sizeChanged)
        {
            stages.Add(new ProvisioningStage(
                "resize-droplet",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Power the droplet off and resize it from '{liveSize ?? "(unknown)"}' to '{spec.Machine.SizeRef}', with the resize action's disk flag set to false. ")
                + string.Create(
                    CultureInfo.InvariantCulture,
                    $"That is the CPU-and-memory-only form: it does not write to the {disk} boot disk, it leaves every file on the machine where it is, and DigitalOcean allows it to be reversed later. ")
                + "The disk-inclusive form is NOT planned here and never will be by this adapter: growing a "
                + "droplet's disk is permanent, cannot be undone, and permanently prevents the droplet from "
                + "ever being resized down again. If the target size requires a larger disk than this droplet "
                + "has, DigitalOcean will refuse this resize - and that refusal is the intended outcome rather "
                + "than a gap, because the only way past it is the irreversible operation."));
        }

        if (tagChanges.Count > 0)
        {
            stages.Add(new ProvisioningStage(
                "retag-droplet",
                Id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Attach or detach {tagChanges.Count} DigitalOcean tag(s) so the droplet carries the Servyx tag set the request describes: ")
                + string.Join("; ", tagChanges.Select(c => c.Description))
                + ". A tag is an account-level label attached to the droplet; changing one does not stop, "
                + "restart, or otherwise touch the machine."));
        }

        stages.Add(new ProvisioningStage(
            "data-impact",
            Id,
            string.Create(CultureInfo.InvariantCulture, $"Data impact of this plan is {dataImpact}: ")
            + (dataImpact == DataImpact.Destroyed
                ? "the rebuild above erases the droplet's boot disk, so approving this plan is approving the "
                  + "deletion of everything stored on the machine. Take a snapshot first if any of it matters; "
                  + "this adapter cannot take one for you and does not claim the Snapshot capability."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"the droplet keeps its id ({droplet.Id}), its address, and the {disk} boot disk it has now. No step above writes to that disk or detaches it."))));

        return stages;
    }

    /// <summary>
    /// The single stage a plan carries when the request asks for a region a droplet cannot be moved to.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>only</em> stage such a plan carries, even when the request also changes the size
    /// or the image. Listing a resize next to a refusal would suggest part of the plan is applicable, and it
    /// is not: the droplet the other stages would act on is not the droplet the request is describing. The
    /// differences themselves are all still reported by name in <see cref="UpdatePlan.Changes"/>, so nothing
    /// is hidden — only the illusion of a partially-executable plan is.
    /// </remarks>
    private static IReadOnlyList<ProvisioningStage> BuildUnsupportedRegionStages(
        DropletResource droplet,
        DigitalOceanDropletSpec spec,
        IReadOnlyList<PlannedChange> changes,
        DataImpact dataImpact) =>
    [
        new(
            "region-change-not-supported",
            Id,
            string.Create(
                CultureInfo.InvariantCulture,
                $"NOT SUPPORTED: the request asks for region '{spec.Machine.Region}' and droplet {droplet.Id} is in '{droplet.Region?.Slug ?? "(unknown)"}'. ")
            + "A droplet cannot be moved between regions. DigitalOcean exposes no resize, rebuild, migrate or "
            + "any other action that relocates an existing droplet, so there is no operation for this adapter "
            + "to plan and none is planned. Reaching the requested state would mean destroying this droplet - "
            + "which destroys its boot disk and everything on it - and creating a different droplet in the "
            + "other region, then reinstalling and restoring onto it. That is a decision for a person, not a "
            + "step this planner will describe on their behalf, so no stage here does it. "
            + string.Create(
                CultureInfo.InvariantCulture,
                $"Data impact of this plan is {dataImpact} for that reason. ")
            + DescribeUnappliedRemainder(changes)),
    ];

    /// <summary>
    /// Names the differences other than the region that the refused plan is also not applying, so a caller
    /// reading the stage sees the whole of what is being declined rather than only the reason.
    /// </summary>
    private static string DescribeUnappliedRemainder(IReadOnlyList<PlannedChange> changes)
    {
        var others = changes
            .Where(c => !string.Equals(c.Aspect, "region", StringComparison.Ordinal))
            .ToList();

        return others.Count == 0
            ? "The region is the only difference the comparison found; nothing else about the droplet needs to change."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"The other {others.Count} difference(s) found are equally not applied, because they describe a machine in a region this one cannot reach: ")
            + string.Join("; ", others.Select(c => c.Description))
            + ".";
    }

    /// <summary>
    /// The image reference a live droplet is running, in whichever of the two forms
    /// <c>POST /v2/droplets</c> accepts: the public image's slug when it has one, otherwise its numeric id.
    /// </summary>
    /// <remarks>
    /// Both forms are produced because both are valid in a request, so a comparison that only understood
    /// slugs would report a permanent, unfixable difference for every custom image and every snapshot — which
    /// have no slug at all.
    /// </remarks>
    private static string? LiveImageRef(DropletResource droplet)
    {
        if (droplet.Image is not { } image)
        {
            return null;
        }

        return NullIfBlank(image.Slug)
            ?? (image.Id == 0 ? null : image.Id.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Reads one recorded expectation out of a handle's tags, treating a blank value as no expectation at
    /// all rather than as an expectation of emptiness.
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
        DropletResource droplet,
        string? liveSize,
        string? liveImage,
        string? liveRegion,
        IReadOnlyDictionary<string, string> liveTags,
        DigitalOceanDropletSpec spec,
        IReadOnlyDictionary<string, string> desiredTags,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(CultureInfo.InvariantCulture, $"{droplet.Id}\n");
        builder.Append(liveSize ?? string.Empty).Append('\n');
        builder.Append(liveImage ?? string.Empty).Append('\n');
        builder.Append(liveRegion ?? string.Empty).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var tag in liveTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-tag {tag.Key}={tag.Value}\n");
        }

        builder.Append(ComputePlanHash(spec, ServyxDropletTags.ToDropletTags(desiredTags))).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
