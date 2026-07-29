using System.Net;
using Docker.DotNet;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// The patch-detection half of the Docker adapter: the answer to "is there a newer build of this server's
/// image available?", and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What "available" can honestly mean here, and what it cannot.</strong> The Docker Engine API this
/// adapter speaks has exactly two ways to learn what an image reference points at. <c>InspectImageAsync</c>
/// reads the host's <em>local</em> image store and is a read. <c>CreateImageAsync</c> — a pull — is the only
/// call that re-resolves a reference against the registry, and it writes: it fetches layers and moves the
/// local tag. There is no third call; the engine exposes no read-only "what does this tag resolve to
/// upstream?" endpoint (<c>docker manifest inspect</c> is a client-side registry call, not an engine one,
/// and would need registry credentials this adapter is not given). So this detector resolves against
/// <see cref="PatchAvailabilitySource.LocalImageStore"/>, and every result says so.
/// </para>
/// <para>
/// <strong>What that therefore cannot detect.</strong> It detects the case an operator actually hits on a
/// host that pulls: the image for the configured tag has been fetched and the container is still running
/// the older one. It does <em>not</em> detect a patch that exists only upstream — a <c>:latest</c> whose
/// local copy is stale is, to this check, indistinguishable from one that is current, and
/// <see cref="PatchStatus.UpToDate"/> here means "nothing newer has reached this host", never "nothing newer
/// has been published". That is stated on <see cref="PatchStatus.UpToDate"/> and carried on every result via
/// <see cref="PatchCheckResult.Source"/> rather than left for a caller to infer.
/// </para>
/// <para>
/// <strong>Detection does not pull, so there is nothing here to gate.</strong> A pull would make the answer
/// stronger and would also be a mutation of the host — new layers on disk, a moved local tag — which would
/// have to sit behind the same explicit opt-in every other mutating call does, and would make a read a
/// caller expects to be free into one that can consume gigabytes. This adapter takes the other branch: it
/// stays read-only and reports what it consequently cannot know. The only engine calls on this path are
/// <c>InspectContainerAsync</c> and <c>InspectImageAsync</c>, and the test suite asserts the whole call log.
/// </para>
/// <para>
/// <strong>No <see cref="DataImpact"/> is stated, deliberately.</strong> Detecting a patch touches no data
/// and produces no plan. Applying one is an update to a new image reference, which
/// <see cref="PlanUpdateAsync"/> already plans and already states a data impact for, derived from the live
/// container's mounts. A data-impact field on a detection result could only be a guess made without having
/// planned the recreate, so <see cref="PatchCheckResult"/> has none.
/// </para>
/// </remarks>
public sealed partial class DockerContainerProvisioner : IPatchDetector
{
    /// <summary>
    /// Stands in for the image reference on a result that could not establish one — a handle for a
    /// container Servyx did not provision, or one recorded before <see cref="ServyxResourceTags.ImageLabel"/>
    /// existed. <see cref="PatchCheckResult"/> requires a non-blank reference precisely so this case has to
    /// be named rather than left empty.
    /// </summary>
    private const string UnrecordedImageReference = "(unrecorded)";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The comparison is between the image id the live container is running
    /// (<c>ContainerInspectResponse.Image</c>, the digest the container was started from and which does not
    /// move when a tag does) and the image id the recorded reference resolves to in the host's image store
    /// right now. Equal means the running workload is on the newest build this host has; different means a
    /// newer build is sitting locally, unused.
    /// </para>
    /// <para>
    /// <strong>The reference comes from the handle, not from the live container.</strong> What the
    /// deployment tracks is what Servyx recorded at create time
    /// (<see cref="ServyxResourceTags.ImageLabel"/>); reading it off the live container would resolve
    /// whatever the container happens to be running now, so a container someone recreated from a different
    /// tag would always report itself up to date with its own tag. A handle with no recorded reference is
    /// refused without touching the engine and reported <see cref="PatchStatus.Unknown"/> — Servyx cannot
    /// say what a container it did not provision is supposed to track, and guessing would turn "I do not
    /// know" into an answer.
    /// </para>
    /// <para>
    /// <strong>Drift is not reported as a patch.</strong> If the live container is running a different
    /// reference than the one recorded, this returns <see cref="PatchStatus.Unknown"/> and says so. The two
    /// digests would still compare, but the difference would be drift wearing a patch's label, and
    /// <see cref="DetectDriftAsync"/> is the check that answers that question properly.
    /// </para>
    /// </remarks>
    public async Task<PatchCheckResult> DetectPatchAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var checkedAt = _timeProvider.GetUtcNow();

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            // Mirrors DetectDriftAsync's refusal to act on another provisioner's scope — but reported as
            // Unknown rather than as UpToDate, since "this is not my resource" is not evidence it is current.
            return Indeterminate(
                handle,
                UnrecordedImageReference,
                $"Handle belongs to provisioner '{handle.ProvisionerId}', not '{Id}'.",
                checkedAt);
        }

        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var reference = recorded.TryGetValue(ServyxResourceTags.ImageLabel, out var fromTags)
            ? NullIfBlank(fromTags)
            : null;

        if (reference is null)
        {
            return Indeterminate(
                handle,
                UnrecordedImageReference,
                "Servyx recorded no image reference for this resource, so there is no reference to re-resolve. A container Servyx did not provision has no tracked tag to compare against.",
                checkedAt);
        }

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return Indeterminate(handle, reference, "The engine no longer knows about this container.", checkedAt);
        }

        var liveReference = NullIfBlank(inspect.Config?.Image);
        if (!string.Equals(liveReference, reference, StringComparison.Ordinal))
        {
            return Indeterminate(
                handle,
                reference,
                $"The container is running image reference '{liveReference ?? "(none)"}', not the recorded '{reference}'. That is drift, not a patch; run drift detection.",
                checkedAt);
        }

        var runningDigest = NullIfBlank(inspect.Image);
        if (runningDigest is null)
        {
            return Indeterminate(handle, reference, "The engine reported no image id for the running container.", checkedAt);
        }

        var availableDigest = await ResolveLocalDigestOrNullAsync(reference, ct).ConfigureAwait(false);
        if (availableDigest is null)
        {
            return Indeterminate(
                handle,
                reference,
                $"'{reference}' is not present in this host's image store, so it cannot be resolved without pulling it. Detection is read-only and does not pull.",
                checkedAt,
                runningDigest);
        }

        return PatchCheckResult.Resolved(
            handle,
            PatchAvailabilitySource.LocalImageStore,
            reference,
            runningDigest,
            availableDigest,
            checkedAt);
    }

    /// <summary>
    /// Every unknown answer on this path goes through here, so all of them carry the same
    /// <see cref="PatchAvailabilitySource"/> and none can be produced without a reason.
    /// </summary>
    private static PatchCheckResult Indeterminate(
        ResourceHandle handle,
        string reference,
        string reason,
        DateTimeOffset checkedAt,
        string? runningDigest = null) =>
        PatchCheckResult.Indeterminate(
            handle,
            PatchAvailabilitySource.LocalImageStore,
            reference,
            reason,
            checkedAt,
            runningDigest);

    /// <summary>
    /// Reads the image id an image reference currently resolves to in the host's image store, translating
    /// "this host has never seen that reference" into <see langword="null"/> rather than into an exception
    /// or, worse, a match.
    /// </summary>
    /// <remarks>
    /// <c>InspectImageAsync</c> is a read: it consults local storage and never contacts a registry. It is
    /// therefore also the ceiling on what this adapter can know — see the type remarks.
    /// </remarks>
    private async Task<string?> ResolveLocalDigestOrNullAsync(string reference, CancellationToken ct)
    {
        try
        {
            var image = await _client.Images.InspectImageAsync(reference, ct).ConfigureAwait(false);
            return NullIfBlank(image?.ID);
        }
        catch (DockerImageNotFoundException)
        {
            return null;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
