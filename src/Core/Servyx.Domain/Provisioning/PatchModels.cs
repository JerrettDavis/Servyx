using System.Globalization;

namespace Servyx.Domain.Provisioning;

/// <summary>
/// The answer to "is a newer build of this workload's image available?".
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is deliberately no zero member,</strong> for exactly the reason
/// <see cref="DataImpact"/> has none: <c>default(PatchStatus)</c> must not be a status, so
/// <see cref="UpToDate"/> can never arrive by omission. A forgotten assignment produces an
/// <see cref="ArgumentOutOfRangeException"/>, not a reassuring answer.
/// </para>
/// <para>
/// <strong>Patching is not updating and is not drift.</strong> An update applies a desired spec the caller
/// supplies; drift compares the live resource against what Servyx recorded. Patch detection compares the
/// live resource against what is available <em>elsewhere</em>, and is the only one of the three whose
/// answer depends on something outside the deployment. That dependency is why
/// <see cref="PatchAvailabilitySource"/> is carried on every result: "available" means different things
/// depending on what the adapter was able to ask, and a caller that cannot see which question was answered
/// cannot judge the answer.
/// </para>
/// </remarks>
public enum PatchStatus
{
    /// <summary>
    /// The workload is running the image its configured reference resolves to, as resolved against the
    /// <see cref="PatchCheckResult.Source"/> the check was able to consult.
    /// </summary>
    /// <remarks>
    /// This is a scoped claim, never an absolute one. With
    /// <see cref="PatchAvailabilitySource.LocalImageStore"/> it means "nothing newer has been fetched to
    /// this host", which is a weaker statement than "nothing newer has been published" — read
    /// <see cref="PatchCheckResult.Source"/> before repeating this status to an operator.
    /// </remarks>
    UpToDate = 1,

    /// <summary>
    /// The configured image reference resolves to a different image than the one the workload is running,
    /// so a patch is available. Both digests are non-null and differ on a result carrying this status.
    /// </summary>
    PatchAvailable = 2,

    /// <summary>
    /// The check could not establish an answer, and says so rather than defaulting to
    /// <see cref="UpToDate"/>.
    /// </summary>
    /// <remarks>
    /// The same discipline as <c>CostConfidence.Unknown</c>: an unknown answer renders as "unknown" rather
    /// than as a fabricated one. Every result carrying this status also carries a non-blank
    /// <see cref="PatchCheckResult.Reason"/> naming what could not be resolved.
    /// </remarks>
    Unknown = 3,
}

/// <summary>
/// What a <see cref="PatchCheckResult"/> resolved its "available" digest against — i.e. how much the
/// result's <see cref="PatchStatus.UpToDate"/> is actually worth.
/// </summary>
/// <remarks>
/// As with <see cref="PatchStatus"/> there is no zero member, so a source is always something the adapter
/// asserted about a call it made.
/// </remarks>
public enum PatchAvailabilitySource
{
    /// <summary>
    /// The host's own image store — what a pull previously fetched, and nothing more recent. This source
    /// detects a patch that has been fetched to the host but not yet rolled into the running workload; it
    /// cannot detect one that has only been published upstream, because re-resolving a tag against a
    /// registry is not a read the Docker Engine API offers (see <c>DockerContainerProvisioner</c>'s patch
    /// remarks).
    /// </summary>
    LocalImageStore = 1,

    /// <summary>
    /// The upstream registry, re-resolved at check time. No adapter in Servyx reports this today; the value
    /// exists so an adapter that genuinely makes a registry call has a way to say so rather than being
    /// forced to overstate a <see cref="LocalImageStore"/> answer.
    /// </summary>
    Registry = 2,
}

/// <summary>
/// The answer to "is there a newer version of this workload's image available?", together with the
/// evidence it was decided on.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Status"/> is computed, never asserted.</strong> There is no constructor that takes a
/// status. <see cref="Resolved"/> derives it by comparing two digests it was given, and
/// <see cref="Indeterminate"/> is the only way to produce <see cref="PatchStatus.Unknown"/> — which it
/// requires a reason for. An adapter therefore cannot report <see cref="PatchStatus.UpToDate"/> without
/// having produced both digests, which is the single failure this type exists to design out: a check that
/// could not reach anything looking identical to a check that found nothing newer.
/// </para>
/// <para>
/// <strong>Detecting a patch is a read, and applying one is not this type's business.</strong> Nothing here
/// describes what would happen to the workload's data, and that is deliberate rather than an omission:
/// applying a patch is the existing update path with a new image reference, and an
/// <see cref="UpdatePlan"/> is where the <see cref="DataImpact"/> for that recreate is stated — asserted
/// from the live container's mounts by the adapter that would do the work. A data-impact field here could
/// only ever be a guess made without having planned anything, so there is not one.
/// </para>
/// </remarks>
public sealed record PatchCheckResult
{
    private PatchCheckResult(
        ResourceHandle handle,
        PatchStatus status,
        PatchAvailabilitySource source,
        string imageReference,
        string? runningDigest,
        string? availableDigest,
        string reason,
        DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "A patch status must be one the check established. The zero value is not one of them.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source,
                "A patch result must say what its 'available' digest was resolved against.");
        }

        if (status != PatchStatus.Unknown && (runningDigest is null || availableDigest is null))
        {
            throw new ArgumentException(
                "A patch result that claims a definite answer must carry both digests it compared; report Unknown instead.",
                nameof(status));
        }

        Handle = handle;
        Status = status;
        Source = source;
        ImageReference = imageReference;
        RunningDigest = runningDigest;
        AvailableDigest = availableDigest;
        Reason = reason;
        CheckedAt = checkedAt;
    }

    /// <summary>
    /// Builds a result from two digests the adapter actually resolved, deriving the status by comparing
    /// them. There is no way to reach <see cref="PatchStatus.UpToDate"/> or
    /// <see cref="PatchStatus.PatchAvailable"/> except through here.
    /// </summary>
    /// <param name="handle">The resource the check was run against.</param>
    /// <param name="source">What <paramref name="availableDigest"/> was resolved against.</param>
    /// <param name="imageReference">The configured image reference the deployment tracks, e.g. <c>owner/image:latest</c>.</param>
    /// <param name="runningDigest">The digest of the image the workload is actually running. Never blank.</param>
    /// <param name="availableDigest">The digest <paramref name="imageReference"/> currently resolves to. Never blank.</param>
    /// <param name="checkedAt">When the check read the live state.</param>
    /// <exception cref="ArgumentException">A digest is blank — an unresolved digest is <see cref="Indeterminate"/>, not a comparison.</exception>
    public static PatchCheckResult Resolved(
        ResourceHandle handle,
        PatchAvailabilitySource source,
        string imageReference,
        string runningDigest,
        string availableDigest,
        DateTimeOffset checkedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(availableDigest);

        var matches = string.Equals(runningDigest, availableDigest, StringComparison.Ordinal);

        return new PatchCheckResult(
            handle,
            matches ? PatchStatus.UpToDate : PatchStatus.PatchAvailable,
            source,
            imageReference,
            runningDigest,
            availableDigest,
            matches
                ? $"The running image is the one '{imageReference}' resolves to."
                : $"'{imageReference}' resolves to an image the workload is not running.",
            checkedAt);
    }

    /// <summary>
    /// Builds a result that says the check could not establish an answer, naming what it could not resolve.
    /// The only route to <see cref="PatchStatus.Unknown"/>.
    /// </summary>
    /// <param name="handle">The resource the check was run against.</param>
    /// <param name="source">What the check would have resolved against, had it got that far.</param>
    /// <param name="imageReference">
    /// The configured image reference, or a placeholder describing why there isn't one. Never blank — a
    /// result that cannot even name what it was checking still has to say that out loud.
    /// </param>
    /// <param name="reason">Why no answer could be established. Never blank.</param>
    /// <param name="checkedAt">When the check ran.</param>
    /// <param name="runningDigest">The running digest, if the check got that far.</param>
    /// <param name="availableDigest">The available digest, if the check got that far.</param>
    public static PatchCheckResult Indeterminate(
        ResourceHandle handle,
        PatchAvailabilitySource source,
        string imageReference,
        string reason,
        DateTimeOffset checkedAt,
        string? runningDigest = null,
        string? availableDigest = null) =>
        new(handle, PatchStatus.Unknown, source, imageReference, runningDigest, availableDigest, reason, checkedAt);

    /// <summary>The resource the check was run against.</summary>
    public ResourceHandle Handle { get; }

    /// <summary>Whether a patch is available. Always computed from evidence, never asserted.</summary>
    public PatchStatus Status { get; }

    /// <summary>What <see cref="AvailableDigest"/> was resolved against, and therefore what this result is worth.</summary>
    public PatchAvailabilitySource Source { get; }

    /// <summary>The configured image reference the deployment tracks.</summary>
    public string ImageReference { get; }

    /// <summary>The digest of the image the workload is running, or <see langword="null"/> if it could not be read.</summary>
    public string? RunningDigest { get; }

    /// <summary>The digest <see cref="ImageReference"/> resolves to, or <see langword="null"/> if it could not be resolved.</summary>
    public string? AvailableDigest { get; }

    /// <summary>Why the result says what it says. Never blank, including on a definite answer.</summary>
    public string Reason { get; }

    /// <summary>When the check read the live state. A patch answer is only as fresh as this.</summary>
    public DateTimeOffset CheckedAt { get; }

    /// <summary>
    /// A one-line rendering. An unknown status renders the word "unknown" rather than a digest it does not
    /// have.
    /// </summary>
    public string Summary => Status switch
    {
        PatchStatus.UpToDate => string.Create(
            CultureInfo.InvariantCulture,
            $"{Handle.ProviderResourceId} is up to date with {ImageReference} ({Render(RunningDigest)}), as resolved against {Source}."),
        PatchStatus.PatchAvailable => string.Create(
            CultureInfo.InvariantCulture,
            $"{Handle.ProviderResourceId} has a patch available for {ImageReference}: running {Render(RunningDigest)}, available {Render(AvailableDigest)}, as resolved against {Source}."),
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"{Handle.ProviderResourceId} patch status is unknown: {Reason}"),
    };

    private static string Render(string? digest) => digest ?? "unknown";
}

/// <summary>
/// Answers "is a newer build of this resource's image available?" for an already-provisioned resource,
/// without changing anything.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IMaintainer"/> for the same reason <see cref="IMaintainer"/> is
/// separate from <see cref="IProvisioner"/>.</strong> Patch detection needs something the other two do not:
/// a way to re-resolve an image reference. A provisioner that hosts a workload it did not build the image
/// for — a process on a machine, say — has nothing to re-resolve and could only implement this by
/// returning <see cref="PatchStatus.Unknown"/> forever, which is a stub wearing a capability's clothes.
/// Making it a separate interface keeps "this provider can answer the patch question" a fact a caller
/// establishes by a type test.
/// </para>
/// <para>
/// <strong>Detect only; nothing here applies a patch.</strong> There is no <c>ApplyPatchAsync</c>, and that
/// is not a gap to be filled: applying a patch is an update to a new image reference, which the existing
/// <see cref="IMaintainer.PlanUpdateAsync"/> path already plans — with the <see cref="DataImpact"/> that a
/// container recreate carries, asserted from the live mounts. Adding an apply verb here would route around
/// that machinery and lose the data-impact answer with it.
/// </para>
/// <para>
/// <strong>Implementations must be read-only.</strong> Re-resolving a reference by pulling it is a mutation
/// of the host's image store, and this interface's contract forbids it: an implementation that cannot
/// resolve a digest without pulling must report <see cref="PatchStatus.Unknown"/> and name that as the
/// reason. The Docker adapter's test suite asserts no mutating engine call is issued on this path.
/// </para>
/// </remarks>
public interface IPatchDetector
{
    /// <summary>
    /// Stable identifier of the provisioner whose resources this detector understands, matching
    /// <see cref="IProvisioner.ProvisionerId"/>.
    /// </summary>
    string ProvisionerId { get; }

    /// <summary>
    /// Reports whether a newer image is available for the resource behind <paramref name="handle"/>.
    /// Changes nothing.
    /// </summary>
    /// <remarks>
    /// Always returns a result. A resource that has vanished, one belonging to another provisioner, and one
    /// whose digest cannot be resolved are all reported as <see cref="PatchStatus.Unknown"/> with a reason
    /// — never as an absent answer and never as <see cref="PatchStatus.UpToDate"/>.
    /// </remarks>
    /// <param name="handle">The resource to check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PatchCheckResult> DetectPatchAsync(ResourceHandle handle, CancellationToken ct = default);
}
