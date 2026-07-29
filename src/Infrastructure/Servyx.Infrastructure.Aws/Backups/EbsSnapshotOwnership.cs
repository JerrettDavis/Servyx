using System.Globalization;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// The one place that decides whether an EBS snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything here.</strong> An AWS account contains snapshots Servyx did not
/// create, and rather more of them than a DigitalOcean account does: hand-taken snapshots, snapshots taken by
/// AWS Backup or a third-party tool, snapshots that back an AMI, and snapshots left over from volumes that no
/// longer exist. Everything this class does not positively recognise is <see cref="BackupOwnership.Foreign"/>,
/// and a foreign snapshot is listed and inspectable but is never deleted. This is the exact analogue of the
/// Docker adapter treating a game image's own cron archives as foreign.
/// </para>
/// <para>
/// <strong>Ownership is a conjunction of four independent marks, and every one must hold.</strong> A snapshot
/// is Servyx's only if its <em>tags</em> carry all four of:
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="ManagedTag"/> = <c>true</c>, matched byte-for-byte — the same key and the same exact-match rule
/// the EC2 orphan sweep uses, so a snapshot and an instance agree on what "Servyx-managed" spells;
/// </description></item>
/// <item><description>
/// <see cref="InstanceIdTag"/> = the Servyx server being asked about — a snapshot belonging to a
/// <em>different</em> Servyx server is foreign to this one, which is what stops one server's retention
/// deleting another server's backups;
/// </description></item>
/// <item><description>
/// <see cref="SourceInstanceTag"/> = the EC2 instance the snapshot was taken from — the direct analogue of the
/// DigitalOcean classifier's <c>resource_id</c> check, and the mark that binds a snapshot to a machine rather
/// than only to a name;
/// </description></item>
/// <item><description>
/// <see cref="SetTag"/> parsing as <see cref="FormatSetName"/> produced it, for this exact server id — the
/// analogue of the DigitalOcean classifier's <c>name</c> check.
/// </description></item>
/// </list>
/// <para>
/// <strong>Why all four live in tags, when DigitalOcean's third mark was the snapshot's name.</strong> An EBS
/// snapshot has no name; it has a <c>description</c>, which Servyx does write the set name into, and a native
/// tag collection. The description is <em>not</em> one of the marks: it is free-form human-facing text that
/// anyone can retype in the console, and a mark that a stranger can forge by typing is not a mark. Tags are
/// applied atomically by <c>CreateSnapshots</c> in the same call that creates the snapshot, which is a
/// stronger place to keep identity than a field that exists to be edited.
/// </para>
/// <para>
/// <strong>Why the source-instance mark is a tag and not a live attachment check.</strong> The obvious
/// alternative to mark 3 is "the snapshot's <c>volumeId</c> is one of the volumes currently attached to the
/// instance". That is wrong in the expensive direction: a data volume detached last week would flip every
/// snapshot ever taken of it to foreign, and Servyx never prunes what it cannot prove it owns, so those
/// snapshots would bill forever. A tag recorded at creation does not change when the machine's disks do.
/// </para>
/// <para>
/// <strong>The failure directions are deliberately asymmetric.</strong> A snapshot Servyx took but whose tags
/// are missing reads as foreign, and the consequence is that it is never pruned and bills until a human removes
/// it. That is a cost bug. The opposite mistake — a hand-taken snapshot that happened to look Servyx-shaped —
/// would be an irreversible deletion of somebody's only copy. Four marks that must all hold makes the second
/// mistake require four coincidences at once and pushes every partial match into the first, survivable
/// direction. <see cref="EbsSnapshotBackupProvider.CreateAsync"/> refuses to report success when it cannot
/// verify all four afterwards, so the cost bug is loud rather than silent.
/// </para>
/// </remarks>
public static class EbsSnapshotOwnership
{
    /// <summary>The prefix every backup set name Servyx writes begins with.</summary>
    public const string SetNamePrefix = "servyx-snapshot-";

    /// <summary>The UTC timestamp format a Servyx set name ends with. Exactly 16 characters.</summary>
    public const string SetNameTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The rendered length of <see cref="SetNameTimestampFormat"/>, e.g. <c>20260727T100000Z</c>.</summary>
    public const int SetNameTimestampLength = 16;

    /// <summary>The longest server id that can be carried in a set name and still fit an EC2 tag value.</summary>
    public const int MaxServerIdLength = 128;

    /// <summary>Mark 1: the key marking a snapshot as created and owned by Servyx — <c>servyx.managed</c>.</summary>
    public const string ManagedTag = ServyxEc2Tags.ManagedTag;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxEc2Tags.ManagedTagValue;

    /// <summary>Mark 2: the key binding a snapshot to one Servyx server — <c>servyx.instance-id</c>.</summary>
    public const string InstanceIdTag = ServyxEc2Tags.InstanceIdTag;

    /// <summary>Mark 3: the key recording which EC2 instance a snapshot was taken from.</summary>
    /// <remarks>
    /// Distinct from <see cref="InstanceIdTag"/> and the two are constantly confused, so the spelling is
    /// deliberately unambiguous: <c>servyx.instance-id</c> is the <em>Servyx</em> server id (e.g.
    /// <c>srv-0001</c>) and <c>servyx.source-instance</c> is the <em>EC2</em> instance id (e.g.
    /// <c>i-0abc…</c>). Both must match, because one Servyx server can be re-provisioned onto a new EC2
    /// instance and the old machine's snapshots are not backups of the new one.
    /// </remarks>
    public const string SourceInstanceTag = ServyxTagKeys.Prefix + "source-instance";

    /// <summary>Mark 4: the key carrying the backup set a snapshot belongs to.</summary>
    /// <remarks>
    /// This is what makes a multi-volume backup one artifact rather than several. Every snapshot
    /// <c>CreateSnapshots</c> produces in one call carries the same value here, so the set can be reassembled
    /// from a listing without relying on timestamps being close together.
    /// </remarks>
    public const string SetTag = ServyxTagKeys.Prefix + "snapshot-set";

    /// <summary>The EC2 tag key that gives a snapshot its display name in the console.</summary>
    /// <remarks>
    /// Not a Servyx key and not part of the identity, for the same reason
    /// <see cref="ServyxEc2Tags.NameTag"/> is not: an untitled snapshot in the console is indistinguishable
    /// from every other untitled snapshot, which makes a human audit of what Servyx owns needlessly hard.
    /// </remarks>
    public const string NameTag = ServyxEc2Tags.NameTag;

    /// <summary>The EC2 resource type a snapshot <c>TagSpecification</c> names.</summary>
    public const string TagResourceType = "snapshot";

    /// <summary>
    /// Whether <paramref name="serverId"/> can carry a Servyx EBS snapshot at all.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="ServyxEc2Tags.IsTaggableValue"/> in one direction and identical in another. EC2
    /// tag values permit whitespace and <c>+ = : / @</c>; a set name is parsed back by splitting a fixed-length
    /// timestamp off the end, so anything is <em>parseable</em>, but a value containing whitespace or a
    /// separator character makes a name a human cannot read back reliably in the console. The set is therefore
    /// letters, digits, <c>-</c>, <c>_</c> and <c>.</c> — note that <c>.</c> IS allowed here, unlike in a
    /// DigitalOcean snapshot name, because an EC2 tag stores it unencoded.
    /// </remarks>
    public static bool IsSupportedServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > MaxServerIdLength)
        {
            return false;
        }

        foreach (var c in serverId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the backup set name Servyx gives one multi-volume capture of a server.</summary>
    /// <param name="serverId">The Servyx server the set backs.</param>
    /// <param name="takenAt">When the capture was requested, in UTC.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> is not a supported server id.</exception>
    public static string FormatSetName(string serverId, DateTimeOffset takenAt)
    {
        if (!IsSupportedServerId(serverId))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' cannot be carried in an EBS snapshot set name, so snapshots taken for it "
                + "could never be recognised as Servyx's afterwards — and unrecognisable snapshots bill forever and "
                + "are never pruned. Ids may contain only letters, digits, '-', '_' and '.'.",
                nameof(serverId));
        }

        return SetNamePrefix
            + serverId
            + "-"
            + takenAt.UtcDateTime.ToString(SetNameTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads back a set name <see cref="FormatSetName"/> produced.</summary>
    /// <param name="setName">The value of the <see cref="SetTag"/> tag, as EC2 reports it.</param>
    /// <param name="serverId">The server id encoded in the name.</param>
    /// <param name="takenAt">The UTC instant encoded in the name.</param>
    /// <returns><see langword="false"/> for any value this encoding did not produce.</returns>
    public static bool TryParseSetName(string? setName, out string serverId, out DateTimeOffset takenAt)
    {
        serverId = string.Empty;
        takenAt = default;

        if (setName is null || !setName.StartsWith(SetNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = setName[SetNamePrefix.Length..];
        if (remainder.Length < SetNameTimestampLength + 2 || remainder[^(SetNameTimestampLength + 1)] != '-')
        {
            return false;
        }

        var candidateServerId = remainder[..^(SetNameTimestampLength + 1)];
        if (!IsSupportedServerId(candidateServerId))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                remainder[^SetNameTimestampLength..],
                SetNameTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        serverId = candidateServerId;
        takenAt = parsed;
        return true;
    }

    /// <summary>
    /// Classifies one snapshot for one server. Returns <see cref="BackupOwnership.Servyx"/> only when all four
    /// marks described in the type remarks hold; everything else is <see cref="BackupOwnership.Foreign"/>.
    /// </summary>
    /// <param name="tags">The snapshot's tags, exactly as EC2 reports them.</param>
    /// <param name="serverId">The Servyx server the classification is being made for.</param>
    /// <param name="ec2InstanceId">The EC2 instance that server runs on.</param>
    public static BackupOwnership Classify(
        IReadOnlyDictionary<string, string>? tags,
        string serverId,
        string ec2InstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ec2InstanceId);

        if (!IsSupportedServerId(serverId) || tags is null || tags.Count == 0)
        {
            return BackupOwnership.Foreign;
        }

        // Mark 1 - the managed marker, matched exactly. ServyxTagKeys.IsManaged is an ordinal comparison and
        // not a truthiness test, for the same reason it is one in the orphan sweep: the output here is a
        // delete list, and a classifier that guesses wrong deletes somebody else's only copy.
        if (!ServyxTagKeys.IsManaged(tags))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 2 - it must belong to *this* Servyx server.
        if (!Matches(tags, InstanceIdTag, serverId))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 3 - it must have been taken from *this* EC2 instance. A snapshot of the machine this server used
        // to run on is not a backup of the machine it runs on now.
        if (!Matches(tags, SourceInstanceTag, ec2InstanceId))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 4 - the set tag must be a name this adapter wrote, for this exact server.
        return tags.TryGetValue(SetTag, out var setName)
            && TryParseSetName(setName, out var namedServer, out _)
            && string.Equals(namedServer, serverId, StringComparison.Ordinal)
            ? BackupOwnership.Servyx
            : BackupOwnership.Foreign;
    }

    /// <summary>
    /// Reads the backup set a Servyx-owned snapshot belongs to, or <see langword="null"/> if its tags do not
    /// carry a set name this adapter wrote.
    /// </summary>
    public static string? ReadSetName(IReadOnlyDictionary<string, string>? tags) =>
        tags is not null
        && tags.TryGetValue(SetTag, out var setName)
        && TryParseSetName(setName, out _, out _)
            ? setName
            : null;

    /// <summary>
    /// Builds the exact tag dictionary <c>CreateSnapshots</c> applies to every snapshot in one set.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ServyxTagKeys.Build"/> so the caller-supplied extras are written first and can
    /// never shadow a canonical key, then validated against EC2's own charset rules through
    /// <see cref="ServyxEc2Tags.Validate"/> before any call is made — a snapshot EC2 refuses to tag is better
    /// than a snapshot EC2 creates untagged, and the check happens before either is possible.
    /// </remarks>
    /// <param name="serverId">The Servyx server the set backs.</param>
    /// <param name="ec2InstanceId">The EC2 instance the volumes are attached to.</param>
    /// <param name="jobId">The provisioning job identity to carry through, for sweep parity with the instance.</param>
    /// <param name="connectorId">The connector identity to carry through, for sweep parity with the instance.</param>
    /// <param name="setName">The set name from <see cref="FormatSetName"/>.</param>
    /// <exception cref="ArgumentException">Any value would be rejected by EC2, or the set name is not one this adapter wrote.</exception>
    public static IReadOnlyDictionary<string, string> BuildTags(
        string serverId,
        string ec2InstanceId,
        string jobId,
        string connectorId,
        string setName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ec2InstanceId);

        if (!TryParseSetName(setName, out var namedServer, out _)
            || !string.Equals(namedServer, serverId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Set name '{setName}' is not one this adapter produced for server '{serverId}', so snapshots "
                + "tagged with it could never be classified as Servyx's afterwards.",
                nameof(setName));
        }

        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameTag] = setName,
            [SourceInstanceTag] = ec2InstanceId,
            [SetTag] = setName,
        };

        return ServyxEc2Tags.Validate(ServyxTagKeys.Build(serverId, jobId, connectorId, extras));
    }

    private static bool Matches(IReadOnlyDictionary<string, string> tags, string key, string expected) =>
        tags.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.Ordinal);
}
