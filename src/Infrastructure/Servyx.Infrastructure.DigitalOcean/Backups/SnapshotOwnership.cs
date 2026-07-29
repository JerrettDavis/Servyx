using System.Globalization;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>
/// The one place that decides whether a DigitalOcean snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this before widening anything here.</strong> A DigitalOcean account contains snapshots
/// Servyx did not create: taken by hand in the console, taken by another tool, or left over from a droplet
/// that no longer exists. DigitalOcean's own <em>automated backups</em> are a separate product and do not
/// appear as snapshots at all, so they are outside this classification entirely and Servyx can never reach
/// them. Everything this class does not positively recognise is <see cref="BackupOwnership.Foreign"/>, and a
/// foreign snapshot is listed, inspectable and restorable but is never deleted. This is the exact analogue of
/// the Docker adapter treating the Palworld image's own cron archives as foreign.
/// </para>
/// <para>
/// <strong>Ownership is a conjunction of four independent marks, and every one must hold.</strong> A snapshot
/// is Servyx's only if:
/// </para>
/// <list type="number">
/// <item><description>
/// its <c>resource_type</c> is exactly <c>droplet</c> — a volume snapshot is a different resource and is
/// never claimed as a droplet backup;
/// </description></item>
/// <item><description>
/// its <c>resource_id</c> is the droplet Servyx is being asked about — a snapshot of a <em>different</em>
/// droplet is foreign to this server even if Servyx took it, which is what stops one server's retention
/// deleting another server's backups;
/// </description></item>
/// <item><description>
/// its <c>name</c> parses as <see cref="FormatName"/> produced it, for this exact server id;
/// </description></item>
/// <item><description>
/// its <c>tags</c> carry <em>both</em> <c>servyx_managed:true</c> and the server's
/// <c>servyx_instance-id:…</c>, matched byte-for-byte through the same
/// <see cref="ServyxDropletTags"/> encoding the droplet sweep uses.
/// </description></item>
/// </list>
/// <para>
/// <strong>The failure directions are deliberately asymmetric.</strong> A snapshot Servyx took but could not
/// tag — the API call failed, someone stripped the tag, DigitalOcean stopped reporting tags on this endpoint
/// — reads as foreign, and the consequence is that it is never pruned and bills until a human removes it.
/// That is a cost bug. The opposite mistake, a hand-taken snapshot that happened to look Servyx-shaped,
/// would be an irreversible deletion of somebody's only copy. Four marks that must all hold makes the second
/// mistake require four coincidences at once, and pushes every partial match into the first, survivable
/// direction. <see cref="DigitalOceanSnapshotBackupProvider.CreateAsync"/> refuses to report success when it
/// cannot verify all four afterwards, so the cost bug is loud rather than silent.
/// </para>
/// </remarks>
public static class SnapshotOwnership
{
    /// <summary>The prefix every snapshot name Servyx writes begins with.</summary>
    public const string NamePrefix = "servyx-snapshot-";

    /// <summary>The UTC timestamp format a Servyx snapshot name ends with. Exactly 16 characters.</summary>
    public const string NameTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The rendered length of <see cref="NameTimestampFormat"/>, e.g. <c>20260727T100000Z</c>.</summary>
    public const int NameTimestampLength = 16;

    /// <summary>The tag marking a snapshot as created and owned by Servyx: <c>servyx_managed:true</c>.</summary>
    public static string ManagedTag => ServyxDropletTags.ManagedFilter;

    /// <summary>
    /// The tag binding a snapshot to one Servyx server, e.g. <c>servyx_instance-id:srv-0001</c>.
    /// </summary>
    /// <param name="serverId">The Servyx server id. Must be expressible as a DigitalOcean tag value.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> cannot be carried as a tag.</exception>
    public static string InstanceTag(string serverId) =>
        ServyxDropletTags.Encode(ServyxTagKeys.InstanceId, serverId);

    /// <summary>
    /// Whether <paramref name="serverId"/> can carry a Servyx snapshot at all.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="ServyxDropletTags.IsTaggableValue"/> by one character: a <c>:</c> is legal in
    /// a DigitalOcean <em>tag</em> but would sit inside a snapshot <em>name</em>, where it is not reliably
    /// accepted and would make the name's own separator ambiguous to read back. A server id that fails this
    /// is refused up front rather than producing a snapshot whose ownership could not be re-derived.
    /// </remarks>
    public static bool IsSupportedServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId) || serverId.Length > 128)
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

    /// <summary>Builds the snapshot name Servyx gives a backup of <paramref name="serverId"/>.</summary>
    /// <param name="serverId">The Servyx server the snapshot backs.</param>
    /// <param name="takenAt">When the snapshot was requested, in UTC.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> is not a supported server id.</exception>
    public static string FormatName(string serverId, DateTimeOffset takenAt)
    {
        if (!IsSupportedServerId(serverId))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' cannot be carried in a DigitalOcean snapshot name or tag, so a snapshot "
                + "taken for it could never be recognised as Servyx's afterwards — and an unrecognisable snapshot "
                + "bills forever and is never pruned. Ids may contain only letters, digits, '-' and '_'.",
                nameof(serverId));
        }

        return NamePrefix
            + serverId
            + "-"
            + takenAt.UtcDateTime.ToString(NameTimestampFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads back a snapshot name <see cref="FormatName"/> produced.</summary>
    /// <param name="name">The snapshot name as DigitalOcean reports it.</param>
    /// <param name="serverId">The server id encoded in the name.</param>
    /// <param name="takenAt">The UTC instant encoded in the name.</param>
    /// <returns><see langword="false"/> for any name this encoding did not produce.</returns>
    public static bool TryParseName(string? name, out string serverId, out DateTimeOffset takenAt)
    {
        serverId = string.Empty;
        takenAt = default;

        if (name is null || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = name[NamePrefix.Length..];
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
    /// Classifies one snapshot for one server. Returns <see cref="BackupOwnership.Servyx"/> only when all
    /// four marks described in the type remarks hold; everything else is
    /// <see cref="BackupOwnership.Foreign"/>.
    /// </summary>
    /// <param name="resourceType">The snapshot's <c>resource_type</c>.</param>
    /// <param name="resourceId">The snapshot's <c>resource_id</c>.</param>
    /// <param name="name">The snapshot's <c>name</c>.</param>
    /// <param name="tags">The snapshot's <c>tags</c>.</param>
    /// <param name="serverId">The Servyx server the classification is being made for.</param>
    /// <param name="dropletId">The droplet that server is backed by.</param>
    public static BackupOwnership Classify(
        string? resourceType,
        string? resourceId,
        string? name,
        IEnumerable<string>? tags,
        string serverId,
        long dropletId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        if (!IsSupportedServerId(serverId))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 1 — a volume snapshot is a different resource and is never a droplet backup.
        if (!string.Equals(resourceType, DigitalOceanApiClient.DropletSnapshotResourceType, StringComparison.Ordinal))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 2 — it must be a snapshot of *this* droplet. A snapshot of another droplet is foreign to this
        // server even when Servyx took it, which is what stops one server's retention reaching another's.
        if (!long.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
            || owner != dropletId)
        {
            return BackupOwnership.Foreign;
        }

        // Mark 3 — the name must be one this adapter wrote, for this exact server.
        if (!TryParseName(name, out var namedServer, out _)
            || !string.Equals(namedServer, serverId, StringComparison.Ordinal))
        {
            return BackupOwnership.Foreign;
        }

        // Mark 4 — both tags, matched byte-for-byte through the droplet sweep's own encoding.
        var tagList = tags as IReadOnlyCollection<string> ?? tags?.ToList();
        if (tagList is null || tagList.Count == 0)
        {
            return BackupOwnership.Foreign;
        }

        var instanceTag = InstanceTag(serverId);
        var hasManaged = tagList.Any(t => string.Equals(t, ManagedTag, StringComparison.Ordinal));
        var hasInstance = tagList.Any(t => string.Equals(t, instanceTag, StringComparison.Ordinal));

        return hasManaged && hasInstance ? BackupOwnership.Servyx : BackupOwnership.Foreign;
    }
}
