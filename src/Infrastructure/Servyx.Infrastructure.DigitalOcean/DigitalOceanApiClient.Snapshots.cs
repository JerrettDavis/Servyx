using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Servyx.Infrastructure.DigitalOcean;

/// <summary>
/// The snapshot half of the DigitalOcean client: taking a droplet snapshot, listing snapshots, tagging one,
/// restoring a droplet from one, and deleting one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three of these five calls cost or destroy something, and each says so on its own member.</strong>
/// A snapshot bills per GB-month for as long as it exists, so <see cref="SnapshotDropletAsync"/> starts a
/// recurring charge rather than a one-off one. <see cref="RestoreDropletFromSnapshotAsync"/> erases the
/// droplet's boot disk. <see cref="DeleteSnapshotAsync"/> is irreversible and may be removing the only copy
/// of somebody's save files.
/// </para>
/// <para>
/// <strong>Both action submissions are receipts, not outcomes</strong>, exactly as
/// <see cref="ResizeDropletAsync"/> and <see cref="RebuildDropletAsync"/> are: DigitalOcean answers the POST
/// while the work is still queued, and a snapshot of a large disk takes minutes. Nothing may treat a
/// successful return from either as a finished operation; that is what <see cref="PollActionAsync"/> is for.
/// </para>
/// </remarks>
internal sealed partial class DigitalOceanApiClient
{
    /// <summary>The <c>resource_type</c> a droplet snapshot carries in <c>GET /v2/snapshots</c>.</summary>
    internal const string DropletSnapshotResourceType = "droplet";

    /// <summary>
    /// Submits a <em>snapshot</em> of one droplet and returns the action DigitalOcean created to track it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This starts a recurring charge.</strong> DigitalOcean bills snapshots per gigabyte per month
    /// for as long as the snapshot exists — it is not a one-off cost, and nothing expires it. A snapshot
    /// taken and forgotten bills forever.
    /// </para>
    /// <para>
    /// <strong>The returned action is a receipt, not an outcome.</strong> The snapshot does not exist yet
    /// when this returns; the copy is queued and takes minutes on a large disk. Poll the action before
    /// telling anybody a backup was taken.
    /// </para>
    /// </remarks>
    internal async Task<DropletActionResource> SnapshotDropletAsync(long dropletId, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}/actions"))
        {
            Content = JsonContent.Create(
                new SnapshotDropletActionRequest { Name = name },
                options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "snapshot a droplet", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletActionEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Action
            ?? throw new DigitalOceanApiException(
                response.StatusCode,
                "DigitalOcean accepted the droplet snapshot request but returned no action object, so Servyx has no "
                + "action id to poll and cannot tell whether the snapshot ran. Do NOT resubmit blindly: a snapshot "
                + "that was accepted is being taken and will bill per GB-month once it exists, so a second request "
                + "leaves two copies billing. List the droplet's snapshots at DigitalOcean before doing anything "
                + "else.");
    }

    /// <summary>
    /// Submits a <em>restore</em> of one droplet from one of its snapshots — the action that replaces its boot
    /// disk — and returns the action DigitalOcean created to track it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This destroys everything currently on the droplet.</strong> The boot disk's contents are
    /// replaced by the snapshot's: every file written since the snapshot was taken is gone and cannot be
    /// recovered from the droplet afterwards. It is the same class of operation as
    /// <see cref="RebuildDropletAsync"/> and is gated at least as strictly — see
    /// <c>DigitalOceanSnapshotBackupProvider.RestoreAsync</c>, which requires a previewed, unexpired,
    /// single-use plan <em>and</em> a separately-supplied acknowledgement naming
    /// <c>DataImpact.Destroyed</c> exactly.
    /// </para>
    /// <para>
    /// <strong>The returned action is a receipt, not an outcome</strong>, and a restore takes minutes.
    /// </para>
    /// </remarks>
    internal async Task<DropletActionResource> RestoreDropletFromSnapshotAsync(
        long dropletId,
        long snapshotImageId,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"v2/droplets/{dropletId}/actions"))
        {
            Content = JsonContent.Create(
                new RestoreDropletActionRequest { Image = snapshotImageId },
                options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "restore a droplet from a snapshot", ct).ConfigureAwait(false);

        var envelope = await ReadAsync<DropletActionEnvelope>(response, ct).ConfigureAwait(false);
        return envelope?.Action
            ?? throw new DigitalOceanApiException(
                response.StatusCode,
                "DigitalOcean accepted the droplet restore request but returned no action object, so Servyx has no "
                + "action id to poll and cannot tell whether the restore ran. Do NOT resubmit: a restore that was "
                + "accepted is already overwriting the boot disk, and a second one overwrites it again. Read the "
                + "droplet and the account's actions at DigitalOcean before doing anything else.");
    }

    /// <summary>
    /// Lists every droplet snapshot in the account, following DigitalOcean's pagination to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole account, then filtered per droplet by the caller — deliberately, and it is the safe
    /// direction. The alternative endpoint (<c>GET /v2/droplets/{id}/snapshots</c>) reports image objects
    /// with no <c>tags</c> member, and without tags Servyx cannot tell its own snapshots from ones a human
    /// or another tool took. Under-listing here would hide a foreign snapshot; it could never invent one.
    /// </para>
    /// <para>
    /// Pagination is followed rather than truncated for the same reason the droplet sweep follows it:
    /// stopping at page one would report "you have no other snapshots" to somebody deciding what their
    /// account is costing, and would hide from a prune the very artifacts it must not touch.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<SnapshotResource>> ListDropletSnapshotsAsync(CancellationToken ct)
    {
        var snapshots = new List<SnapshotResource>();
        var next = string.Create(
            CultureInfo.InvariantCulture,
            $"v2/snapshots?per_page={SweepPageSize}&resource_type={DropletSnapshotResourceType}");

        for (var page = 0; page < MaxSweepPages && next is not null; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "list snapshots", ct).ConfigureAwait(false);

            var envelope = await ReadAsync<SnapshotListEnvelope>(response, ct).ConfigureAwait(false);
            snapshots.AddRange(envelope?.Snapshots ?? []);

            next = string.IsNullOrWhiteSpace(envelope?.Links?.Pages?.Next) ? null : envelope.Links.Pages.Next;
        }

        return snapshots;
    }

    /// <summary>
    /// Deletes one snapshot. Returns <see langword="false"/> if DigitalOcean no longer has it.
    /// </summary>
    /// <remarks>
    /// <strong>Irreversible, and possibly the only copy.</strong> There is no recycle bin: a deleted snapshot
    /// is gone, and with it whatever save files it held. The single caller
    /// (<c>DigitalOceanSnapshotBackupProvider.DeleteServyxOwnedAsync</c>) re-asserts ownership immediately
    /// before reaching this method; this method exists at exactly one call site so that assertion cannot be
    /// bypassed by reaching the delete some other way.
    /// </remarks>
    internal async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "v2/snapshots/" + Uri.EscapeDataString(snapshotId));

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "delete a snapshot", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Creates a tag in the account if it does not already exist, so that a resource can be tagged with it.
    /// </summary>
    /// <remarks>
    /// DigitalOcean's tag API refuses to apply a tag that has never been created, and answers a duplicate
    /// create with 409 or 422 depending on the endpoint's mood. Both are treated as success here, because
    /// "the tag already exists" is the outcome this call is trying to bring about. Any other non-success
    /// status is surfaced: a tag that could not be created means the snapshot about to be taken cannot be
    /// marked as Servyx's, and a snapshot Servyx cannot recognise is one it can never prune.
    /// </remarks>
    internal async Task EnsureTagExistsAsync(string tagName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/tags")
        {
            Content = JsonContent.Create(new CreateTagRequest { Name = tagName }, options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            return;
        }

        await EnsureSuccessAsync(response, "create a tag", ct).ConfigureAwait(false);
    }

    /// <summary>Applies an existing tag to one snapshot.</summary>
    /// <remarks>
    /// The body's <c>resource_type</c> is a get-only <c>image</c> (see <see cref="TagResourceRef"/>), so this
    /// method cannot be made to tag a droplet, a volume or a database by any argument it takes.
    /// </remarks>
    internal async Task TagSnapshotAsync(string tagName, string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v2/tags/" + Uri.EscapeDataString(tagName) + "/resources")
        {
            Content = JsonContent.Create(
                new TagResourcesRequest { Resources = [new TagResourceRef { ResourceId = snapshotId }] },
                options: SerializerOptions),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "tag a snapshot", ct).ConfigureAwait(false);
    }
}
