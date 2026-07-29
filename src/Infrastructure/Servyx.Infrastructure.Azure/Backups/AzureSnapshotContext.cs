using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// Everything <see cref="AzureSnapshotBackupProvider"/> needs to know about one Servyx server: which virtual
/// machine backs it, in which resource group, what identity its snapshots carry, and how long they are kept.
/// </summary>
/// <param name="ServerId">The Servyx server id. Must satisfy <see cref="AzureSnapshotOwnership.IsSupportedServerId"/>.</param>
/// <param name="ResourceGroup">
/// The resource group the machine lives in. Snapshots are created in the <em>same</em> group, deliberately: a
/// snapshot is a first-class ARM resource with its own lifetime, and putting it beside the machine it backs is
/// what makes it findable by the same tag sweep, deletable by the same permissions, and visible to whoever
/// audits the group.
/// </param>
/// <param name="VirtualMachineName">The ARM name of the virtual machine, e.g. <c>servyx-srv-0001</c>.</param>
/// <param name="JobId">
/// The provisioning job identity stamped on the snapshots. Carried so a snapshot and the machine it was taken
/// from answer the same orphan sweep the same way; it is not part of the ownership test.
/// </param>
/// <param name="ConnectorId">The connector identity stamped on the snapshots, for the same reason.</param>
/// <param name="DefaultRetention">
/// The retention applied when <see cref="AzureSnapshotBackupProvider.PruneAsync"/> is called without a policy.
/// It governs Servyx-owned backup sets only and can never reach a foreign snapshot.
/// </param>
public sealed record AzureSnapshotContext(
    string ServerId,
    string ResourceGroup,
    string VirtualMachineName,
    string JobId,
    string ConnectorId,
    RetentionPolicy DefaultRetention);

/// <summary>
/// Turns a Servyx server id into the Azure virtual machine that backs it.
/// </summary>
/// <remarks>
/// Deliberately not defaulted anywhere in this assembly, for the same reason
/// <c>IEbsSnapshotContextSource</c> and <c>IDigitalOceanSnapshotContextSource</c> are not: the mapping from a
/// server to a resource group and a machine name is knowledge only the composition root has, and a
/// plausible-looking default would snapshot the wrong machine — and, worse, would make another machine's
/// snapshots look prunable.
/// </remarks>
public interface IAzureSnapshotContextSource
{
    /// <summary>Reads the snapshot context for a server, or <see langword="null"/> if there is none.</summary>
    /// <param name="serverId">The Servyx server id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AzureSnapshotContext?> GetAsync(string serverId, CancellationToken ct = default);
}

/// <summary>
/// Encodes and decodes the opaque <see cref="BackupArtifact.Id"/> strings this adapter hands out.
/// </summary>
/// <remarks>
/// <para>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the id
/// has to carry which server it belongs to as well as which backup it names. It is still treated as opaque:
/// resolution always goes back through a fresh listing and matches on the whole id, so an id naming a set that
/// has since been deleted at Azure fails as "not found" rather than being trusted as something to act on.
/// </para>
/// <para>
/// <strong>The kind prefix is not decoration.</strong> On the EBS adapter a set name and a snapshot id could
/// never collide, because AWS mints snapshot ids and they all begin <c>snap-</c>. Here both halves are ARM
/// <em>names</em>, and a human is perfectly free to name a hand-taken snapshot
/// <c>servyx-snapshot-srv-0001-20260727T100000Z</c>. So a Servyx set is addressed as
/// <see cref="SetPrefix"/> and an individual snapshot as <see cref="SnapshotPrefix"/>, and no foreign resource
/// name can be mistaken for a set id.
/// </para>
/// <para>
/// Servyx groups its own snapshots into sets because it created them together; it has no grounds to assert that
/// two snapshots it did not create belong together, so a foreign snapshot is always addressed on its own.
/// </para>
/// </remarks>
public static class AzureSnapshotBackupId
{
    /// <summary>The separator between the server id and the rest of the backup id.</summary>
    public const string Separator = "::";

    /// <summary>The kind prefix identifying a Servyx-owned backup set.</summary>
    public const string SetPrefix = "set:";

    /// <summary>The kind prefix identifying one individual snapshot resource.</summary>
    public const string SnapshotPrefix = "snapshot:";

    /// <summary>Builds the backup id of a Servyx-owned backup set.</summary>
    /// <param name="serverId">The owning Servyx server.</param>
    /// <param name="setName">The Servyx set name.</param>
    public static string FormatSet(string serverId, string setName) => Format(serverId, SetPrefix + setName);

    /// <summary>Builds the backup id of a single snapshot resource, Servyx's or otherwise.</summary>
    /// <param name="serverId">The Servyx server the snapshot is reported under.</param>
    /// <param name="snapshotName">The snapshot's ARM resource name.</param>
    public static string FormatSnapshot(string serverId, string snapshotName) =>
        Format(serverId, SnapshotPrefix + snapshotName);

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
    /// A URI rather than a path, because a managed-disk snapshot is not a file on any disk Servyx controls — it
    /// is an ARM resource in a subscription with its own resource id, its own tags, its own billing and its own
    /// lifecycle. The subscription and resource group are part of it because that is where a human has to go to
    /// look at it.
    /// </remarks>
    /// <param name="subscriptionId">The subscription the snapshots live in.</param>
    /// <param name="resourceGroup">The resource group the snapshots live in.</param>
    /// <param name="setName">The Servyx backup set name.</param>
    public static string LocationOfSet(string subscriptionId, string resourceGroup, string setName) =>
        "azure://compute/" + subscriptionId + "/" + resourceGroup + "/snapshot-sets/" + setName;

    /// <summary>The provider-defined <see cref="BackupArtifact.Location"/> of a single snapshot.</summary>
    /// <param name="subscriptionId">The subscription the snapshot lives in.</param>
    /// <param name="resourceGroup">The resource group the snapshot lives in.</param>
    /// <param name="snapshotName">The snapshot's ARM resource name.</param>
    public static string LocationOfSnapshot(string subscriptionId, string resourceGroup, string snapshotName) =>
        "azure://compute/" + subscriptionId + "/" + resourceGroup + "/snapshots/" + snapshotName;

    private static string Format(string serverId, string key)
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
}
