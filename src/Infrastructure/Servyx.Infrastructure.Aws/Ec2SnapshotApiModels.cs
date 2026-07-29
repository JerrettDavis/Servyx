using System.Globalization;
using System.Xml.Linq;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// One EBS snapshot, as the EC2 Query API describes it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two element names for one field, and both are real.</strong> <c>DescribeSnapshots</c> reports the
/// lifecycle state in <c>&lt;status&gt;</c> — the same spelling <c>DescribeVolumes</c> uses — while
/// <c>CreateSnapshots</c> reports it in <c>&lt;state&gt;</c>. That is not a schema guess: the two actions
/// genuinely differ, and a projection that read only one of them would report every freshly-created snapshot's
/// state as unknown and so could never tell "submitted" from "completed". <see cref="State"/> therefore reads
/// <c>status</c> and falls back to <c>state</c>.
/// </para>
/// <para>
/// <strong><see cref="VolumeSizeGib"/> is the SOURCE VOLUME's allocated size, not the snapshot's billed
/// size.</strong> This is the single most misread field on the EBS API and the reason
/// <see cref="Backups.EbsSnapshotPricing"/> exists in the shape it does. EBS snapshots are incremental: after
/// the first snapshot of a volume, only blocks changed since the previous one are stored and charged. AWS
/// exposes no billed size on this API at all, so a cost computed from this number is a <em>ceiling</em> and is
/// labelled as one everywhere it is used, rather than being presented as what the account will be charged.
/// </para>
/// </remarks>
/// <param name="SnapshotId">The snapshot's provider id, e.g. <c>snap-0123456789abcdef0</c>.</param>
/// <param name="VolumeId">The EBS volume the snapshot was taken from.</param>
/// <param name="State">The lifecycle state: <c>pending</c>, <c>completed</c>, <c>error</c>, <c>recoverable</c>, <c>recovering</c>.</param>
/// <param name="StateMessage">EC2's explanation of an <c>error</c> state, when it supplies one.</param>
/// <param name="Description">The snapshot's description. Servyx writes the backup set name here.</param>
/// <param name="Progress">EC2's copy-progress string, e.g. <c>60%</c>.</param>
/// <param name="VolumeSizeGib">The <em>source volume's</em> allocated size in GiB. NOT the billed size.</param>
/// <param name="StartTime">When EC2 reports the snapshot was started.</param>
/// <param name="OwnerId">The AWS account that owns the snapshot.</param>
/// <param name="Tags">The snapshot's tags, decoded from its <c>tagSet</c>.</param>
internal sealed record Ec2Snapshot(
    string SnapshotId,
    string? VolumeId,
    string? State,
    string? StateMessage,
    string? Description,
    string? Progress,
    int? VolumeSizeGib,
    DateTimeOffset? StartTime,
    string? OwnerId,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>The state in which a snapshot is a usable, restorable point-in-time copy.</summary>
    internal const string CompletedState = "completed";

    /// <summary>The state in which EC2 has given up on the snapshot. Terminal, and not a backup.</summary>
    internal const string ErrorState = "error";

    /// <summary>Whether EC2 reports this snapshot as finished and restorable.</summary>
    internal bool IsCompleted => string.Equals(State, CompletedState, StringComparison.Ordinal);

    /// <summary>Whether EC2 reports this snapshot as failed. Terminal — retrying is reasonable.</summary>
    internal bool IsErrored => string.Equals(State, ErrorState, StringComparison.Ordinal);

    /// <summary>Projects one <c>snapshotSet/item</c> element.</summary>
    internal static Ec2Snapshot? From(XElement item)
    {
        var snapshotId = Ec2Xml.Text(item, "snapshotId");
        if (snapshotId is null)
        {
            return null;
        }

        return new Ec2Snapshot(
            snapshotId,
            Ec2Xml.Text(item, "volumeId"),
            Ec2Xml.Text(item, "status") ?? Ec2Xml.Text(item, "state"),
            Ec2Xml.Text(item, "statusMessage") ?? Ec2Xml.Text(item, "stateMessage"),
            Ec2Xml.Text(item, "description"),
            Ec2Xml.Text(item, "progress"),
            int.TryParse(Ec2Xml.Text(item, "volumeSize"), CultureInfo.InvariantCulture, out var size) ? size : null,
            Ec2Xml.Timestamp(item, "startTime"),
            Ec2Xml.Text(item, "ownerId"),
            Ec2Xml.Tags(item));
    }
}
