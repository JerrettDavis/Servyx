using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>
/// Everything <see cref="DigitalOceanSnapshotBackupProvider"/> needs to know about one Servyx server: which
/// droplet backs it, and how long its snapshots are kept.
/// </summary>
/// <param name="ServerId">The Servyx server id. Must satisfy <see cref="SnapshotOwnership.IsSupportedServerId"/>.</param>
/// <param name="DropletId">The DigitalOcean droplet the server runs on.</param>
/// <param name="DefaultRetention">
/// The retention applied when <see cref="DigitalOceanSnapshotBackupProvider.PruneAsync"/> is called without a
/// policy. It governs Servyx-owned snapshots only and can never reach a foreign one.
/// </param>
public sealed record DigitalOceanSnapshotContext(
    string ServerId,
    long DropletId,
    RetentionPolicy DefaultRetention);

/// <summary>
/// Turns a Servyx server id into the droplet that backs it.
/// </summary>
/// <remarks>
/// Deliberately not defaulted anywhere in this assembly, for the same reason
/// <c>ISshBackupContextSource</c> is not: the mapping from a server to a droplet id is knowledge only the
/// composition root has, and a plausible-looking default would snapshot — or, far worse, restore over — the
/// wrong machine.
/// </remarks>
public interface IDigitalOceanSnapshotContextSource
{
    /// <summary>Reads the snapshot context for a server, or <see langword="null"/> if there is none.</summary>
    /// <param name="serverId">The Servyx server id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DigitalOceanSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// Encodes and decodes the opaque <see cref="BackupArtifact.Id"/> strings this adapter hands out.
/// </summary>
/// <remarks>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the
/// id has to carry which server it belongs to as well as which snapshot it names. It is still treated as
/// opaque: resolution always goes back through a fresh listing of the account's snapshots and matches on the
/// whole id, so an id naming a snapshot that has since been deleted at DigitalOcean fails as "not found"
/// rather than being trusted as something to restore from or delete.
/// </remarks>
public static class SnapshotBackupId
{
    /// <summary>The separator between the server id and the snapshot id.</summary>
    public const string Separator = "::";

    /// <summary>Builds the backup id for a snapshot.</summary>
    /// <param name="serverId">The owning Servyx server.</param>
    /// <param name="snapshotId">The DigitalOcean snapshot id.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> contains <see cref="Separator"/>.</exception>
    public static string Format(string serverId, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        if (serverId.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' contains the reserved backup-id separator '{Separator}'.",
                nameof(serverId));
        }

        return serverId + Separator + snapshotId;
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

    /// <summary>The provider-defined <see cref="BackupArtifact.Location"/> of a snapshot.</summary>
    /// <remarks>
    /// A URI rather than a path, because a snapshot is not a file on any disk Servyx controls — it is a
    /// resource in a DigitalOcean account with its own id, its own billing and its own lifecycle. Rendering
    /// it as something path-shaped would invite exactly the wrong mental model.
    /// </remarks>
    public static string LocationOf(string snapshotId) => "digitalocean://snapshots/" + snapshotId;
}
