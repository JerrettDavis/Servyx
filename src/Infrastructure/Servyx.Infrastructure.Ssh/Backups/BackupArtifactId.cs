namespace Servyx.Infrastructure.Ssh.Backups;

/// <summary>
/// Encodes and decodes the opaque <c>BackupArtifact.Id</c> strings <see cref="SshBackupProvider"/> hands out.
/// </summary>
/// <remarks>
/// <para>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the
/// id has to carry enough to find the artifact again — which server it belongs to, and where it sits on the
/// host. It is still treated as opaque by callers: resolution always goes back through a fresh listing and
/// matches on the whole id, so an id naming a file that has since been deleted, moved, or replaced fails as
/// "not found" rather than being trusted as a path to act on.
/// </para>
/// <para>
/// The separator is <c>::</c>, which cannot appear in a server id (rejected here) and does not appear in
/// POSIX filenames in practice.
/// </para>
/// <para>
/// This duplicates <c>Servyx.Infrastructure.Docker.Backups.BackupArtifactId</c> — see the remarks on
/// <see cref="BackupGlob"/> for why. Unlike the glob matcher, this one is only <em>incidentally</em> shaped
/// the same: the encoding is an adapter convention about how one filesystem-backed provider names its
/// artifacts, not a domain concept, so the two copies are free to diverge.
/// </para>
/// </remarks>
public static class BackupArtifactId
{
    /// <summary>The separator between the server id and the artifact location.</summary>
    public const string Separator = "::";

    /// <summary>Builds the id for an artifact.</summary>
    /// <param name="serverId">The owning server.</param>
    /// <param name="location">The artifact's absolute location on its target.</param>
    /// <exception cref="ArgumentException"><paramref name="serverId"/> contains <see cref="Separator"/>.</exception>
    public static string Format(string serverId, string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        if (serverId.Contains(Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Server id '{serverId}' contains the reserved backup-id separator '{Separator}'.",
                nameof(serverId));
        }

        return serverId + Separator + location;
    }

    /// <summary>Extracts the server id from a backup id.</summary>
    /// <param name="backupId">The backup id.</param>
    /// <param name="serverId">The decoded server id.</param>
    /// <returns><see langword="true"/> when <paramref name="backupId"/> is well-formed.</returns>
    public static bool TryGetServerId(string backupId, out string serverId)
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
}
