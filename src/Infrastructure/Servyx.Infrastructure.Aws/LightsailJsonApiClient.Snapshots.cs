using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

// The four Lightsail snapshot actions the backup adapter can reach, kept in their own file so the provisioning
// client in LightsailJsonApiClient.cs stays exactly the four actions it was and the retag half stays exactly the
// one it was. (A plain comment rather than an XML one: the type's documentation lives on the first part, and a
// partial type carries its doc comment once.)
//
// WHAT IS AND IS NOT HERE. CreateInstanceSnapshot, GetInstanceSnapshots, GetInstanceSnapshot and
// DeleteInstanceSnapshot. There is deliberately NO CreateInstancesFromSnapshot method: restoring from a
// Lightsail instance snapshot produces a NEW instance rather than overwriting the existing one, so it is a
// provisioning operation wearing a backup's clothes, and LightsailSnapshotBackupProvider refuses to perform it
// rather than half-performing it. A method that existed here would be a method something could call; not having
// one is the structural half of that refusal, exactly as the absent UntagResource is the structural half of the
// ownership guarantee in LightsailJsonApiClient.Tags.cs.
//
// THE LISTING HAS NO SERVER-SIDE FILTER, WHICH IS THE SAME LIGHTSAIL DIVERGENCE GetInstances HAS. AWS's
// published GetInstanceSnapshots request syntax accepts pageToken and nothing else - no tag filter, no
// instance-name filter, nothing. EC2's DescribeSnapshots accepts both an owner filter and arbitrary tag filters.
// So every instance snapshot in the region crosses the wire on every listing and the narrowing to one instance
// is entirely this process's own work, done by the caller against the snapshot's own fromInstanceName field. The
// consequence is a larger response on a busy account, not a correctness problem - but it is worth knowing before
// anybody wonders why this client reads snapshots it then discards.
//
// SUBMISSION IS NOT SUCCESS. CreateInstanceSnapshot and DeleteInstanceSnapshot both answer 200 OK with an
// `operations` array of pending Operation records, not with the snapshot. Both methods below hand those
// operations back rather than swallowing them, and the caller polls GetInstanceSnapshot for the snapshot's
// observed state. AWS documents InstanceSnapshot.progress as "populated only for disk snapshots, and null for
// instance snapshots", so `state` (pending | error | available) is the ONLY progress signal an instance snapshot
// has, and an observed `available` is the only thing this adapter will call a backup.
internal sealed partial class LightsailJsonApiClient
{
    /// <summary>The Lightsail action that creates an instance snapshot. Billable, and the only creating one here.</summary>
    internal const string CreateInstanceSnapshotAction = "CreateInstanceSnapshot";

    /// <summary>The Lightsail action that deletes an instance snapshot. Irreversible.</summary>
    internal const string DeleteInstanceSnapshotAction = "DeleteInstanceSnapshot";

    /// <summary>
    /// Creates one instance snapshot, applying every Servyx ownership tag in the same call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tags travel with the create, which is a real improvement over the DigitalOcean adapter and
    /// matches the EC2 one.</strong> AWS's published <c>CreateInstanceSnapshot</c> request syntax carries a
    /// <c>tags</c> array — "the tag keys and optional values to add to the resource during create" — so there is
    /// no window in which a billing snapshot exists untagged and therefore unrecognisable as Servyx's. The
    /// DigitalOcean snapshot action takes a name but no tags and has to tag afterwards; this one does not.
    /// </para>
    /// <para>
    /// Answers with pending <c>Operation</c> records, never the snapshot. The caller polls
    /// <see cref="GetInstanceSnapshotAsync"/> by the name it already chose — the snapshot's identity, like an
    /// instance's, is caller-supplied and known before the request is sent.
    /// </para>
    /// </remarks>
    /// <param name="body">The request body, built by <c>LightsailSnapshotBackupProvider</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<IReadOnlyList<LightsailOperation>> CreateInstanceSnapshotAsync(
        JsonObject body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = await SendAsync(CreateInstanceSnapshotAction, body, "snapshot an instance", ct)
            .ConfigureAwait(false);

        return LightsailOperation.AllFrom(response?["operations"] as JsonArray);
    }

    /// <summary>Reads one instance snapshot by name, or <see langword="null"/> if Lightsail does not know it.</summary>
    /// <remarks>
    /// A snapshot that is absent is reported as absent rather than as an error, because the create path has to
    /// be able to tell "not there yet" from "the call failed": Lightsail answers the same generic
    /// <see cref="LightsailErrorCodes.NotFound"/> for a snapshot that never existed and for one that has not
    /// materialised into a readable object yet.
    /// </remarks>
    internal async Task<LightsailInstanceSnapshot?> GetInstanceSnapshotAsync(
        string instanceSnapshotName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSnapshotName);

        var body = new JsonObject { ["instanceSnapshotName"] = instanceSnapshotName };

        try
        {
            var response = await SendAsync("GetInstanceSnapshot", body, "read an instance snapshot", ct)
                .ConfigureAwait(false);

            return LightsailInstanceSnapshot.From(response?["instanceSnapshot"] as JsonObject);
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, LightsailErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>
    /// Lists every instance snapshot in the region, following <c>nextPageToken</c> pagination to the end.
    /// </summary>
    /// <remarks>
    /// Unfiltered, because the API offers no filter — see the file header. Pagination is followed rather than
    /// truncated for the same reason the instance sweep follows it: stopping at the first page would report "no
    /// snapshots beyond page one" as "no snapshots", and for a backup listing that reads as data loss.
    /// </remarks>
    internal async Task<IReadOnlyList<LightsailInstanceSnapshot>> GetInstanceSnapshotsAsync(CancellationToken ct)
    {
        var results = new List<LightsailInstanceSnapshot>();
        string? pageToken = null;

        for (var page = 0; page < MaxSweepPages; page++)
        {
            var body = new JsonObject();
            if (pageToken is not null)
            {
                body["pageToken"] = pageToken;
            }

            var response = await SendAsync("GetInstanceSnapshots", body, "list instance snapshots", ct)
                .ConfigureAwait(false);

            if (response?["instanceSnapshots"] is JsonArray snapshots)
            {
                foreach (var node in snapshots)
                {
                    var snapshot = LightsailInstanceSnapshot.From(node as JsonObject);
                    if (snapshot is not null)
                    {
                        results.Add(snapshot);
                    }
                }
            }

            pageToken = LightsailJson.Text(response, "nextPageToken");
            if (pageToken is null)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Deletes one instance snapshot by name. Returns <see langword="false"/> if Lightsail no longer knows it.
    /// </summary>
    /// <remarks>
    /// Irreversible, and the only destructive action this client can perform on a backup. A snapshot that has
    /// already vanished answers <see cref="LightsailErrorCodes.NotFound"/> and is reported as absent rather than
    /// as a failure: it is gone, which is the outcome the caller asked for.
    /// </remarks>
    internal async Task<bool> DeleteInstanceSnapshotAsync(string instanceSnapshotName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSnapshotName);

        var body = new JsonObject { ["instanceSnapshotName"] = instanceSnapshotName };

        try
        {
            await SendAsync(DeleteInstanceSnapshotAction, body, "delete an instance snapshot", ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, LightsailErrorCodes.NotFound, StringComparison.Ordinal))
        {
            return false;
        }
    }
}
