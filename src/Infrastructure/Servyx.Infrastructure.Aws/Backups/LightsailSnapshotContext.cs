using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// Everything <see cref="LightsailSnapshotBackupProvider"/> needs to know about one Servyx server: which
/// Lightsail instance backs it, what identity its snapshots carry, and how long they are kept.
/// </summary>
/// <param name="ServerId">The Servyx server id. Must satisfy <see cref="LightsailSnapshotOwnership.IsSupportedServerId"/>.</param>
/// <param name="InstanceName">
/// The Lightsail instance the server runs on, e.g. <c>palworld-01</c>. A <em>name</em>, not a provider-minted id
/// — a Lightsail instance's identity is the name its creator chose, which is why the provisioner stores it as
/// <c>ResourceHandle.ProviderResourceId</c> and why the snapshot classifier can match on it directly.
/// </param>
/// <param name="JobId">
/// The provisioning job identity stamped on the snapshots. Carried so a snapshot and the instance it was taken
/// from answer the same orphan sweep the same way; it is not part of the ownership test.
/// </param>
/// <param name="ConnectorId">The connector identity stamped on the snapshots, for the same reason.</param>
/// <param name="DefaultRetention">
/// The retention applied when <see cref="LightsailSnapshotBackupProvider.PruneAsync"/> is called without a
/// policy. It governs Servyx-owned snapshots only and can never reach a foreign one.
/// </param>
public sealed record LightsailSnapshotContext(
    string ServerId,
    string InstanceName,
    string JobId,
    string ConnectorId,
    RetentionPolicy DefaultRetention);

/// <summary>
/// Turns a Servyx server id into the Lightsail instance that backs it.
/// </summary>
/// <remarks>
/// Deliberately not defaulted anywhere in this assembly, for the same reason <c>IEbsSnapshotContextSource</c>,
/// <c>IDigitalOceanSnapshotContextSource</c> and <c>ISshBackupContextSource</c> are not: the mapping from a
/// server to an instance name is knowledge only the composition root has, and a plausible-looking default would
/// snapshot the wrong machine and — far worse — would make another machine's snapshots look prunable.
/// </remarks>
public interface ILightsailSnapshotContextSource
{
    /// <summary>Reads the snapshot context for a server, or <see langword="null"/> if there is none.</summary>
    /// <param name="serverId">The Servyx server id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LightsailSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// Encodes and decodes the opaque <see cref="BackupArtifact.Id"/> strings this adapter hands out.
/// </summary>
/// <remarks>
/// <para>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the id
/// has to carry which server it belongs to as well as which snapshot it names. It is still treated as opaque:
/// resolution always goes back through a fresh listing and matches on the whole id, so an id naming a snapshot
/// that has since been deleted at Lightsail fails as "not found" rather than being trusted as something to act
/// on.
/// </para>
/// <para>
/// <strong>The second half is always a single snapshot name, Servyx-owned or foreign alike — no sets.</strong>
/// The EBS adapter has to distinguish a set name from a bare snapshot id because one EC2 backup is several
/// snapshots taken together. A Lightsail instance snapshot is one artifact covering the whole machine, so there
/// is nothing to group and nothing to disambiguate. That is the shape simplification this adapter gets for free,
/// and it is the same shape the DigitalOcean adapter has.
/// </para>
/// </remarks>
public static class LightsailSnapshotBackupId
{
    /// <summary>The separator between the server id and the snapshot name.</summary>
    public const string Separator = "::";

    /// <summary>Builds the backup id for an instance snapshot.</summary>
    /// <param name="serverId">The owning Servyx server.</param>
    /// <param name="snapshotName">The Lightsail instance snapshot's name.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> contains <see cref="Separator"/>.</exception>
    public static string Format(string serverId, string snapshotName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotName);

        if (serverId.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' contains the reserved backup-id separator '{Separator}'.",
                nameof(serverId));
        }

        return serverId + Separator + snapshotName;
    }

    /// <summary>Extracts the server id from a backup id.</summary>
    /// <param name="backupId">The backup id.</param>
    /// <param name="serverId">The decoded server id.</param>
    /// <returns><see langword="true"/> when <paramref name="backupId"/> is well-formed.</returns>
    public static bool TryGetServerId(string? backupId, out string serverId)
    {
        serverId = string.Empty;
        if (string.IsNullOrWhiteSpace(backupId))
        {
            return false;
        }

        var index = backupId.IndexOf(Separator, StringComparison.Ordinal);
        if (index <= 0 || index + Separator.Length >= backupId.Length)
        {
            return false;
        }

        serverId = backupId[..index];
        return true;
    }

    /// <summary>The provider-defined <see cref="BackupArtifact.Location"/> of an instance snapshot.</summary>
    /// <remarks>
    /// A URI rather than a path, because a Lightsail snapshot is not a file on any disk Servyx controls — it is a
    /// resource in an AWS account with its own name, its own billing and its own lifecycle. The region is part of
    /// it because a snapshot exists in exactly one region and cannot be used from another without being copied
    /// (which is itself a billable operation this adapter never performs).
    /// </remarks>
    /// <param name="region">The AWS region the snapshot lives in.</param>
    /// <param name="snapshotName">The Lightsail instance snapshot's name.</param>
    public static string LocationOf(string region, string snapshotName) =>
        "aws://lightsail/" + region + "/instance-snapshots/" + snapshotName;
}
