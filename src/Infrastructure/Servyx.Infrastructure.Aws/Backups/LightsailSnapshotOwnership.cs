using System.Globalization;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// The one place that decides whether a Lightsail instance snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything here.</strong> A Lightsail account contains instance snapshots
/// Servyx did not create: taken by hand in the console, taken by another tool, produced by Lightsail's own
/// <em>automatic snapshot</em> add-on, or left over from an instance that no longer exists (a manual snapshot
/// outlives its instance; only automatic ones are deleted with it). Everything this class does not positively
/// recognise is <see cref="BackupOwnership.Foreign"/>, and a foreign snapshot is listed and inspectable but is
/// never deleted. This is the exact analogue of the Docker adapter treating a game image's own cron archives as
/// foreign.
/// </para>
/// <para>
/// <strong>Ownership is a conjunction of four independent marks, and every one must hold.</strong> A snapshot is
/// Servyx's only if:
/// </para>
/// <list type="number">
/// <item><description>
/// its native <c>fromInstanceName</c> is the Lightsail instance Servyx is being asked about — a snapshot of a
/// <em>different</em> instance is foreign to this server even when Servyx took it, which is what stops one
/// server's retention deleting another server's backups;
/// </description></item>
/// <item><description>
/// its tags carry <see cref="ManagedTag"/> = <c>true</c>, matched byte-for-byte through the same exact-match
/// rule the Lightsail instance sweep uses, so a snapshot and an instance agree on what "Servyx-managed" spells;
/// </description></item>
/// <item><description>
/// its tags carry <see cref="InstanceIdTag"/> = the Servyx server being asked about — a snapshot belonging to a
/// different Servyx server that happened to run on this machine is not this server's backup;
/// </description></item>
/// <item><description>
/// its <c>name</c> parses as <see cref="FormatSnapshotName"/> produced it, for this exact server id.
/// </description></item>
/// </list>
/// <para>
/// <strong>Mark 1 is a native provider field here, and that is stronger than the EBS adapter's equivalent.</strong>
/// <c>EbsSnapshotOwnership</c> has to carry the source machine in a <em>tag</em> (<c>servyx.source-instance</c>)
/// because an EBS snapshot records only the volume it came from, and a volume can be detached; the EC2 adapter's
/// remarks explain at length why a live attachment check would be worse. Lightsail records
/// <c>fromInstanceName</c> on the snapshot object itself, written by the service at creation time and not
/// writable by anybody afterwards — there is no API that edits it. So the mark that binds a snapshot to a
/// machine is, on this adapter, a fact the provider asserts rather than a fact Servyx asked the provider to
/// remember. It is the direct analogue of the DigitalOcean classifier's <c>resource_id</c> check.
/// </para>
/// <para>
/// <strong>Lightsail's own automatic snapshots cannot pass marks 2 and 3, and cannot be made to.</strong> AWS
/// documents that automatic snapshots "cannot be tagged or exported directly to Amazon EC2". A Servyx tag can
/// therefore never appear on one, so an account with the automatic-snapshot add-on enabled produces snapshots
/// this classifier reports as foreign — listed, visible, never pruned by Servyx. That is the correct outcome and
/// not a gap: those snapshots are managed by Lightsail's own seven-deep rotation, and a second retention policy
/// deleting them would be two systems fighting over the same artifacts.
/// </para>
/// <para>
/// <strong>The failure directions are deliberately asymmetric.</strong> A snapshot Servyx took but whose tags are
/// missing reads as foreign, and the consequence is that it is never pruned and bills until a human removes it.
/// That is a cost bug. The opposite mistake — a hand-taken snapshot that happened to look Servyx-shaped — would
/// be an irreversible deletion of somebody's only copy. Four marks that must all hold makes the second mistake
/// require four coincidences at once and pushes every partial match into the first, survivable direction.
/// <see cref="LightsailSnapshotBackupProvider.CreateAsync"/> refuses to report success when it cannot verify all
/// four afterwards, so the cost bug is loud rather than silent.
/// </para>
/// </remarks>
public static class LightsailSnapshotOwnership
{
    /// <summary>The prefix every snapshot name Servyx writes begins with.</summary>
    public const string SnapshotNamePrefix = "servyx-snapshot-";

    /// <summary>The UTC timestamp format a Servyx snapshot name ends with. Exactly 16 characters.</summary>
    public const string NameTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The rendered length of <see cref="NameTimestampFormat"/>, e.g. <c>20260727T100000Z</c>.</summary>
    public const int NameTimestampLength = 16;

    /// <summary>
    /// The longest server id that can be carried in a snapshot name and still leave the name inside Lightsail's
    /// 255-character resource-name limit.
    /// </summary>
    public const int MaxServerIdLength = 128;

    /// <summary>Mark 2: the key marking a snapshot as created and owned by Servyx — <c>servyx.managed</c>.</summary>
    public const string ManagedTag = ServyxLightsailTags.ManagedTag;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxLightsailTags.ManagedTagValue;

    /// <summary>Mark 3: the key binding a snapshot to one Servyx server — <c>servyx.instance-id</c>.</summary>
    public const string InstanceIdTag = ServyxLightsailTags.InstanceIdTag;

    /// <summary>
    /// Whether <paramref name="serverId"/> can carry a Servyx Lightsail snapshot at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Stricter than <see cref="EbsSnapshotOwnership.IsSupportedServerId"/> by exactly one character,
    /// and the difference is a real API constraint rather than a stylistic one.</strong> The EBS set name lives
    /// in a <em>tag value</em>, where EC2 permits <c>.</c>, so that adapter allows it. A Lightsail snapshot's
    /// identity is its <em>resource name</em>, and AWS publishes the pattern for it as
    /// <c>\w[\w\-]*\w</c> — word characters and hyphens only, starting and ending on a word character. A
    /// <c>.</c> is not a word character, so a server id containing one would produce a name Lightsail refuses,
    /// and the snapshot would either not be created or (worse, if the rule ever loosened) be created under a
    /// name this classifier could not read back. Refused up front instead.
    /// </para>
    /// <para>
    /// So the charset here is letters, digits, <c>-</c> and <c>_</c> — the same set the DigitalOcean adapter
    /// settled on, for a different reason, and one character narrower than the EBS adapter's.
    /// </para>
    /// </remarks>
    public static bool IsSupportedServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > MaxServerIdLength)
        {
            return false;
        }

        foreach (var c in serverId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds the snapshot name Servyx gives one capture of a server.</summary>
    /// <param name="serverId">The Servyx server the snapshot backs.</param>
    /// <param name="takenAt">When the capture was requested, in UTC.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> is not a supported server id.</exception>
    public static string FormatSnapshotName(string serverId, DateTimeOffset takenAt)
    {
        if (!IsSupportedServerId(serverId))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' cannot be carried in a Lightsail instance-snapshot name, so a snapshot "
                + "taken for it could never be recognised as Servyx's afterwards — and an unrecognisable snapshot "
                + "bills forever and is never pruned. Lightsail resource names match \\w[\\w\\-]*\\w, so ids may "
                + "contain only letters, digits, '-' and '_'.",
                nameof(serverId));
        }

        return SnapshotNamePrefix
            + serverId
            + "-"
            + takenAt.UtcDateTime.ToString(NameTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads back a snapshot name <see cref="FormatSnapshotName"/> produced.</summary>
    /// <param name="name">The snapshot name as Lightsail reports it.</param>
    /// <param name="serverId">The server id encoded in the name.</param>
    /// <param name="takenAt">The UTC instant encoded in the name.</param>
    /// <returns><see langword="false"/> for any name this encoding did not produce.</returns>
    public static bool TryParseSnapshotName(string? name, out string serverId, out DateTimeOffset takenAt)
    {
        serverId = string.Empty;
        takenAt = default;

        if (name is null || !name.StartsWith(SnapshotNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = name[SnapshotNamePrefix.Length..];
        if (remainder.Length < NameTimestampLength + 2 || remainder[^(NameTimestampLength + 1)] != '-')
        {
            return false;
        }

        var candidateServerId = remainder[..^(NameTimestampLength + 1)];
        if (!IsSupportedServerId(candidateServerId))
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                remainder[^NameTimestampLength..],
                NameTimestampFormat,
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
    /// <param name="fromInstanceName">The snapshot's native <c>fromInstanceName</c> field.</param>
    /// <param name="snapshotName">The snapshot's <c>name</c> — its identity.</param>
    /// <param name="tags">The snapshot's tags, exactly as Lightsail reports them.</param>
    /// <param name="serverId">The Servyx server the classification is being made for.</param>
    /// <param name="lightsailInstanceName">The Lightsail instance that server runs on.</param>
    public static BackupOwnership Classify(
        string? fromInstanceName,
        string? snapshotName,
        IReadOnlyDictionary<string, string>? tags,
        string serverId,
        string lightsailInstanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lightsailInstanceName);

        if (!IsSupportedServerId(serverId))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 1 - it must be a snapshot of *this* instance, according to the provider's own record of where it
        // came from. A snapshot of the machine this server used to run on is not a backup of the machine it runs
        // on now, and a snapshot of somebody else's machine is not this server's business at all.
        if (!string.Equals(fromInstanceName, lightsailInstanceName, StringComparison.Ordinal))
        {
            return BackupOwnership.Foreign;
        }

        if (tags is null || tags.Count == 0)
        {
            return BackupOwnership.Foreign;
        }

        // Mark 2 - the managed marker, matched exactly. ServyxTagKeys.IsManaged is an ordinal comparison and not
        // a truthiness test, for the same reason it is one in the orphan sweep: the output here feeds a delete
        // list, and a classifier that guesses wrong deletes somebody else's only copy.
        if (!ServyxTagKeys.IsManaged(tags))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 3 - it must belong to *this* Servyx server.
        if (!tags.TryGetValue(InstanceIdTag, out var taggedServer)
            || !string.Equals(taggedServer, serverId, StringComparison.Ordinal))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 4 - the name must be one this adapter wrote, for this exact server.
        return TryParseSnapshotName(snapshotName, out var namedServer, out _)
            && string.Equals(namedServer, serverId, StringComparison.Ordinal)
            ? BackupOwnership.Servyx
            : BackupOwnership.Foreign;
    }

    /// <summary>
    /// Builds the exact tag dictionary <c>CreateInstanceSnapshot</c> applies to the snapshot it creates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Routed through <see cref="ServyxTagKeys.Build"/> so the canonical keys are written last and can never be
    /// shadowed, then validated against Lightsail's own charset rules through
    /// <see cref="ServyxLightsailTags.Validate"/> before any call is made — a snapshot Lightsail refuses to tag
    /// is better than a snapshot Lightsail creates untagged, and the check happens before either is possible.
    /// </para>
    /// <para>
    /// <strong>There is no source-instance tag and no <c>Name</c> tag, and both absences are deliberate.</strong>
    /// The EBS adapter needs the first because an EBS snapshot does not record its instance; Lightsail records it
    /// natively as <c>fromInstanceName</c>, so writing a tag that duplicated it would create a second, weaker
    /// copy of a fact the provider already asserts — and a mark a human could edit is not a mark. The second is
    /// absent for the reason <c>ServyxLightsailTags</c> gives for instances: a Lightsail snapshot's identity
    /// <em>is</em> its caller-chosen name, which is already what the console displays.
    /// </para>
    /// </remarks>
    /// <param name="serverId">The Servyx server the snapshot backs.</param>
    /// <param name="jobId">The provisioning job identity to carry through, for sweep parity with the instance.</param>
    /// <param name="connectorId">The connector identity to carry through, for sweep parity with the instance.</param>
    /// <exception cref="ArgumentException">Any value would be rejected by Lightsail.</exception>
    public static IReadOnlyDictionary<string, string> BuildTags(string serverId, string jobId, string connectorId)
    {
        if (!IsSupportedServerId(serverId))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' cannot be carried in a Lightsail instance-snapshot name, so a snapshot "
                + "tagged for it could never be classified as Servyx's afterwards.",
                nameof(serverId));
        }

        return ServyxLightsailTags.Validate(ServyxTagKeys.Build(serverId, jobId, connectorId));
    }
}
