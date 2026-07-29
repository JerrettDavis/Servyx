using System.Globalization;

namespace Servyx.Infrastructure.Aws;

// The four EBS snapshot actions the backup adapter reaches, kept in their own file so the provisioning client
// in Ec2QueryApiClient.cs stays exactly the five actions it was. (A plain comment rather than an XML one: the
// type's documentation lives on the other part, and a partial type carries its doc comment once.)
//
// CreateSnapshots, PLURAL, is not a convenience over CreateSnapshot. An EC2 instance may have several EBS
// volumes, and CreateSnapshot takes one volume id - so snapshotting an instance with it means N independent
// calls at N different instants, which is N copies that were never a single point in time. CreateSnapshots
// takes an InstanceSpecification and captures every EBS volume attached to that instance as one
// CRASH-CONSISTENT set. That is the only atomicity AWS offers here, this file uses it, and
// EbsSnapshotBackupProvider states its exact limit rather than rounding it up to "consistent".
//
// Tags travel in the create call, and that is a real difference from the DigitalOcean adapter. CreateSnapshots
// accepts TagSpecification entries exactly as RunInstances does, so Servyx's ownership marks are applied by
// the same call that creates the snapshots. There is no window in which a billing snapshot exists untagged and
// therefore unprunable - the window the DigitalOcean adapter cannot close, because that API's snapshot action
// takes a name but no tags. As in the provisioning client, there is deliberately no CreateTags here: the
// atomic path is the only path.
internal sealed partial class Ec2QueryApiClient
{
    /// <summary>The largest page <c>DescribeSnapshots</c> accepts.</summary>
    internal const int SnapshotPageSize = 1000;

    /// <summary>
    /// The <c>Owner</c> value that restricts a snapshot listing to the calling account's own snapshots.
    /// </summary>
    /// <remarks>
    /// Load-bearing, not defensive. An unfiltered <c>DescribeSnapshots</c> returns every <em>public</em>
    /// snapshot in the region — tens of thousands of them, owned by Amazon and by strangers. Every listing this
    /// client makes carries <c>Owner.1=self</c> so nothing outside the account can ever enter a listing that
    /// feeds a retention decision.
    /// </remarks>
    internal const string SelfOwner = "self";

    /// <summary>
    /// Captures every EBS volume attached to one instance as a single crash-consistent snapshot set, applying
    /// Servyx's ownership tags in the same call.
    /// </summary>
    /// <remarks>
    /// Returns the snapshots as EC2 first reports them, which is <c>pending</c> — this call is a submission and
    /// nothing more. Every returned snapshot already exists and is already accruing storage charges, so a
    /// caller that abandons the result leaves billing resources behind.
    /// </remarks>
    internal async Task<IReadOnlyList<Ec2Snapshot>> CreateSnapshotsAsync(
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        CancellationToken ct)
    {
        var response = await PostAsync("CreateSnapshots", parameters, "snapshot an instance's volumes", ct)
            .ConfigureAwait(false);

        return Ec2Xml.Items(response, "snapshotSet")
            .Select(Ec2Snapshot.From)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();
    }

    /// <summary>Reads a specific set of snapshots by id, following pagination to the end.</summary>
    /// <remarks>
    /// The read a create polls on. An id EC2 no longer knows raises <c>InvalidSnapshot.NotFound</c>, which is
    /// surfaced rather than swallowed: a snapshot that vanished mid-poll is a fact the caller has to see.
    /// </remarks>
    internal Task<IReadOnlyList<Ec2Snapshot>> DescribeSnapshotsByIdsAsync(
        IReadOnlyList<string> snapshotIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshotIds);

        if (snapshotIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Ec2Snapshot>>([]);
        }

        var parameters = new List<KeyValuePair<string, string>>(OwnedBySelf());
        for (var i = 0; i < snapshotIds.Count; i++)
        {
            parameters.Add(new KeyValuePair<string, string>(
                "SnapshotId." + (i + 1).ToString(CultureInfo.InvariantCulture),
                snapshotIds[i]));
        }

        return DescribeSnapshotsAsync(parameters, "read snapshots by id", ct);
    }

    /// <summary>Lists this account's snapshots carrying one exact tag, following pagination to the end.</summary>
    internal Task<IReadOnlyList<Ec2Snapshot>> DescribeSnapshotsByTagAsync(
        string tagKey,
        string tagValue,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagValue);

        var parameters = new List<KeyValuePair<string, string>>(OwnedBySelf())
        {
            new("Filter.1.Name", "tag:" + tagKey),
            new("Filter.1.Value.1", tagValue),
        };

        return DescribeSnapshotsAsync(parameters, "list snapshots by tag", ct);
    }

    /// <summary>Lists this account's snapshots taken from any of the given volumes.</summary>
    /// <remarks>
    /// The listing that finds snapshots Servyx did <em>not</em> take. A tag filter can only ever return
    /// Servyx's own work, so a prune driven by tags alone would report <c>SkippedForeign: 0</c> for an account
    /// full of hand-taken snapshots — technically true of what it looked at, and a lie about the account.
    /// </remarks>
    internal Task<IReadOnlyList<Ec2Snapshot>> DescribeSnapshotsByVolumeAsync(
        IReadOnlyList<string> volumeIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(volumeIds);

        if (volumeIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Ec2Snapshot>>([]);
        }

        var parameters = new List<KeyValuePair<string, string>>(OwnedBySelf())
        {
            new("Filter.1.Name", "volume-id"),
        };

        for (var i = 0; i < volumeIds.Count; i++)
        {
            parameters.Add(new KeyValuePair<string, string>(
                "Filter.1.Value." + (i + 1).ToString(CultureInfo.InvariantCulture),
                volumeIds[i]));
        }

        return DescribeSnapshotsAsync(parameters, "list snapshots by volume", ct);
    }

    /// <summary>
    /// Deletes one EBS snapshot. Returns <see langword="false"/> if EC2 no longer knows it.
    /// </summary>
    /// <remarks>
    /// The only deleting call in this file, and it is irreversible. A snapshot that has already gone answers
    /// <c>InvalidSnapshot.NotFound</c>; that is reported as "already absent" rather than as a failure, because
    /// gone is the outcome the caller asked for.
    /// </remarks>
    internal async Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        try
        {
            await PostAsync(
                    "DeleteSnapshot",
                    [new KeyValuePair<string, string>("SnapshotId", snapshotId)],
                    "delete a snapshot",
                    ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (AwsApiException e) when (string.Equals(e.ErrorCode, "InvalidSnapshot.NotFound", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static KeyValuePair<string, string>[] OwnedBySelf() =>
        [new KeyValuePair<string, string>("Owner.1", SelfOwner)];

    private Task<IReadOnlyList<Ec2Snapshot>> DescribeSnapshotsAsync(
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        string attempted,
        CancellationToken ct) =>
        PaginateAsync(
            "DescribeSnapshots",
            parameters,
            SnapshotPageSize,
            attempted,
            response => Ec2Xml.Items(response, "snapshotSet").Select(Ec2Snapshot.From),
            ct);
}
