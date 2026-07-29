using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The Lightsail <em>snapshot</em> objects this adapter reads, projected out of the API's JSON into ordinary
/// records.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <c>LightsailApiModels.cs</c> for the reason <c>Ec2SnapshotApiModels.cs</c> is kept apart from
/// <c>Ec2ApiModels.cs</c>: the provisioning half of this assembly and the backup half read disjoint parts of the
/// service's schema, and a reviewer of one should not have to page past the other.
/// </para>
/// <para>
/// <strong>The one field worth reading twice is <see cref="LightsailInstanceSnapshot.FromAttachedDisks"/>.</strong>
/// AWS's <c>InstanceSnapshot</c> type documents it as "an array of disk objects containing information about all
/// block storage disks", and AWS's own user guide states the consequence outright: "If you've attached block
/// storage disks to your instance, Lightsail copies those additional disks as part of your snapshot." So a
/// Lightsail instance snapshot is a capture of the whole machine — system disk <em>and</em> attached disks — and
/// this field is the provider's own record of which disks that was. It is projected here rather than dropped
/// precisely so <c>LightsailSnapshotBackupProvider.InspectAsync</c> can name them instead of asserting coverage
/// it has not read.
/// </para>
/// <para>
/// <strong><see cref="LightsailInstanceSnapshot.Progress"/> is deliberately absent.</strong> AWS documents the
/// field as "populated only for disk snapshots, and null for instance snapshots", so there is nothing to project
/// and a percentage rendered from it would be a fabricated one. The only progress signal an instance snapshot
/// has is <see cref="LightsailInstanceSnapshot.State"/>, which is why the create path polls that and nothing else.
/// </para>
/// </remarks>
internal sealed record LightsailInstanceSnapshot(
    string Name,
    string? State,
    DateTimeOffset? CreatedAt,
    int? SizeInGb,
    string? FromInstanceName,
    string? FromBlueprintId,
    string? FromBundleId,
    bool IsFromAutoSnapshot,
    string? AvailabilityZone,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<LightsailSnapshotDisk> FromAttachedDisks)
{
    /// <summary>The state a finished, restorable instance snapshot is in.</summary>
    internal const string AvailableState = "available";

    /// <summary>The state a snapshot still being copied is in.</summary>
    internal const string PendingState = "pending";

    /// <summary>The state a snapshot AWS gave up on is in.</summary>
    internal const string ErrorState = "error";

    /// <summary>Whether Lightsail reports this snapshot as finished and restorable.</summary>
    internal bool IsAvailable => string.Equals(State, AvailableState, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Lightsail reports this snapshot as having failed outright.</summary>
    internal bool IsErrored => string.Equals(State, ErrorState, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The total source size this snapshot captured, in GB: the system disk plus every attached block storage
    /// disk, or <see langword="null"/> when Lightsail reported no size for the system disk.
    /// </summary>
    /// <remarks>
    /// <strong>The attached disks have to be added in, and forgetting them would break the estimate in the
    /// dangerous direction.</strong> AWS documents <c>InstanceSnapshot.sizeInGb</c> as "the size in GB of the
    /// SSD" — the instance's own system disk — while each attached disk carries its own <c>sizeInGb</c> inside
    /// <see cref="FromAttachedDisks"/>. A cost ceiling computed from the system disk alone would sit
    /// <em>below</em> the real charge for any instance with a data disk attached, which is the one direction a
    /// figure called a ceiling must never err in.
    /// </remarks>
    internal int? TotalSourceGigabytes =>
        SizeInGb is { } system
            ? system + FromAttachedDisks.Sum(d => d.SizeInGb ?? 0)
            : null;

    /// <summary>Projects one element of a <c>GetInstanceSnapshots</c> response, or a <c>GetInstanceSnapshot</c>'s object.</summary>
    internal static LightsailInstanceSnapshot? From(JsonObject? item)
    {
        var name = LightsailJson.Text(item, "name");
        if (name is null)
        {
            return null;
        }

        var location = item?["location"] as JsonObject;
        var disks = new List<LightsailSnapshotDisk>();

        if (item?["fromAttachedDisks"] is JsonArray attached)
        {
            foreach (var node in attached)
            {
                var disk = LightsailSnapshotDisk.From(node as JsonObject);
                if (disk is not null)
                {
                    disks.Add(disk);
                }
            }
        }

        return new LightsailInstanceSnapshot(
            name,
            LightsailJson.Text(item, "state"),
            LightsailJson.UnixSeconds(item, "createdAt"),
            LightsailSnapshotJson.Int32(item, "sizeInGb"),
            LightsailJson.Text(item, "fromInstanceName"),
            LightsailJson.Text(item, "fromBlueprintId"),
            LightsailJson.Text(item, "fromBundleId"),
            LightsailJson.Bool(item, "isFromAutoSnapshot") ?? false,
            LightsailJson.Text(location, "availabilityZone"),
            LightsailJson.Tags(item?["tags"] as JsonArray),
            disks);
    }
}

/// <summary>One block storage disk an instance snapshot copied along with the system disk.</summary>
/// <param name="Name">The disk's Lightsail name.</param>
/// <param name="Path">The guest device path the disk was attached at, e.g. <c>/dev/xvdf</c>.</param>
/// <param name="SizeInGb">The disk's allocated size in GB, as Lightsail reports it.</param>
/// <param name="IsSystemDisk">Whether Lightsail marks this as the instance's own system disk.</param>
internal sealed record LightsailSnapshotDisk(string Name, string? Path, int? SizeInGb, bool IsSystemDisk)
{
    /// <summary>Projects one element of a <c>fromAttachedDisks</c> array.</summary>
    internal static LightsailSnapshotDisk? From(JsonObject? item)
    {
        var name = LightsailJson.Text(item, "name");

        return name is null
            ? null
            : new LightsailSnapshotDisk(
                name,
                LightsailJson.Text(item, "path"),
                LightsailSnapshotJson.Int32(item, "sizeInGb"),
                LightsailJson.Bool(item, "isSystemDisk") ?? false);
    }
}

/// <summary>The one JSON reader the snapshot half needs that the provisioning half never did.</summary>
/// <remarks>
/// A separate type rather than two more methods on <c>LightsailJson</c>, so adding backups changed no file the
/// provisioning adapter's tests already pin. <c>sizeInGb</c> is the only integral field either snapshot object
/// carries, and it is read through <see cref="double"/> because AWS JSON 1.1 renders numbers without committing
/// to an integral type.
/// </remarks>
internal static class LightsailSnapshotJson
{
    /// <summary>A named property read as a whole number, or <see langword="null"/> when absent or not numeric.</summary>
    internal static int? Int32(JsonObject? obj, string property)
    {
        if (obj is null || !obj.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        double value;
        try
        {
            value = node.GetValue<double>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        return value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
    }
}
