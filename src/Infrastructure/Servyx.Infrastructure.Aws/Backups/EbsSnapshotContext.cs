using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// Everything <see cref="EbsSnapshotBackupProvider"/> needs to know about one Servyx server: which EC2
/// instance backs it, what identity its snapshots carry, and how long they are kept.
/// </summary>
/// <param name="ServerId">The Servyx server id. Must satisfy <see cref="EbsSnapshotOwnership.IsSupportedServerId"/>.</param>
/// <param name="Ec2InstanceId">The EC2 instance the server runs on, e.g. <c>i-0abcdef1234567890</c>.</param>
/// <param name="JobId">
/// The provisioning job identity stamped on the snapshots. Carried so a snapshot and the instance it was taken
/// from answer the same orphan sweep the same way; it is not part of the ownership test.
/// </param>
/// <param name="ConnectorId">The connector identity stamped on the snapshots, for the same reason.</param>
/// <param name="DefaultRetention">
/// The retention applied when <see cref="EbsSnapshotBackupProvider.PruneAsync"/> is called without a policy. It
/// governs Servyx-owned backup sets only and can never reach a foreign snapshot.
/// </param>
public sealed record EbsSnapshotContext(
    string ServerId,
    string Ec2InstanceId,
    string JobId,
    string ConnectorId,
    RetentionPolicy DefaultRetention);

/// <summary>
/// Turns a Servyx server id into the EC2 instance that backs it.
/// </summary>
/// <remarks>
/// Deliberately not defaulted anywhere in this assembly, for the same reason
/// <c>IDigitalOceanSnapshotContextSource</c> and <c>ISshBackupContextSource</c> are not: the mapping from a
/// server to an instance id is knowledge only the composition root has, and a plausible-looking default would
/// snapshot the wrong machine — and, worse, would make another machine's snapshots look prunable.
/// </remarks>
public interface IEbsSnapshotContextSource
{
    /// <summary>Reads the snapshot context for a server, or <see langword="null"/> if there is none.</summary>
    /// <param name="serverId">The Servyx server id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EbsSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// Encodes and decodes the opaque <see cref="BackupArtifact.Id"/> strings this adapter hands out.
/// </summary>
/// <remarks>
/// <para>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the id
/// has to carry which server it belongs to as well as which backup it names. It is still treated as opaque:
/// resolution always goes back through a fresh listing and matches on the whole id, so an id naming a set that
/// has since been deleted at AWS fails as "not found" rather than being trusted as something to act on.
/// </para>
/// <para>
/// <strong>The second half is a backup <em>set</em> name for a Servyx-owned backup and a bare snapshot id for a
/// foreign one</strong>, and the two can never collide: a set name always begins with
/// <see cref="EbsSnapshotOwnership.SetNamePrefix"/> and an EBS snapshot id always begins with <c>snap-</c>.
/// The asymmetry is honest rather than tidy — Servyx groups its own snapshots into sets because it created
/// them together, and it has no grounds to assert that two snapshots it did not create belong together.
/// </para>
/// </remarks>
public static class EbsSnapshotBackupId
{
    /// <summary>The separator between the server id and the set or snapshot id.</summary>
    public const string Separator = "::";

    /// <summary>Builds the backup id for a backup set or a foreign snapshot.</summary>
    /// <param name="serverId">The owning Servyx server.</param>
    /// <param name="key">The Servyx set name, or a foreign snapshot's id.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> contains <see cref="Separator"/>.</exception>
    public static string Format(string serverId, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (serverId.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' contains the reserved backup-id separator '{Separator}'.",
                nameof(serverId));
        }

        return serverId + Separator + key;
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

    /// <summary>The provider-defined <see cref="BackupArtifact.Location"/> of a Servyx backup set.</summary>
    /// <remarks>
    /// A URI rather than a path, because an EBS snapshot is not a file on any disk Servyx controls — it is a
    /// resource in an AWS account with its own id, its own billing and its own lifecycle. The region is part of
    /// it because a snapshot exists in exactly one region and cannot be read from another without being copied
    /// (which is itself a billable operation this adapter never performs).
    /// </remarks>
    /// <param name="region">The AWS region the snapshots live in.</param>
    /// <param name="setName">The Servyx backup set name.</param>
    public static string LocationOfSet(string region, string setName) =>
        "aws://ec2/" + region + "/snapshot-sets/" + setName;

    /// <summary>The provider-defined <see cref="BackupArtifact.Location"/> of a single EBS snapshot.</summary>
    /// <param name="region">The AWS region the snapshot lives in.</param>
    /// <param name="snapshotId">The EBS snapshot id.</param>
    public static string LocationOfSnapshot(string region, string snapshotId) =>
        "aws://ec2/" + region + "/snapshots/" + snapshotId;
}
