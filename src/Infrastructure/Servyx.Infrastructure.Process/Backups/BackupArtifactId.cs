namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// Encodes and decodes the opaque <c>BackupArtifact.Id</c> strings <see cref="LocalProcessBackupProvider"/>
/// hands out.
/// </summary>
/// <remarks>
/// <para>
/// <c>IBackupProvider.InspectAsync</c> and <c>PlanRestoreAsync</c> take a backup id and nothing else, so the
/// id has to carry enough to find the artifact again — which server it belongs to, and where it sits on
/// disk. It is still treated as opaque by callers: resolution always goes back through a fresh listing and
/// matches on the whole id, so an id naming a file that has since been deleted, moved, or replaced fails as
/// "not found" rather than being trusted as a path to act on. That property matters more here than it does
/// over SSH, because the path in a local id names a file on the very machine the panel runs on.
/// </para>
/// <para>
/// The separator is <c>::</c>, which cannot appear in a server id (rejected here). It also cannot appear in
/// a Windows path — <c>:</c> is reserved outside the drive-letter position — and does not appear in POSIX
/// filenames in practice, so the split point is unambiguous on both platforms this adapter runs on.
/// </para>
/// <para>
/// This duplicates the Docker and SSH copies of the same type — see the remarks on <see cref="BackupGlob"/>
/// for why. The encoding is an adapter convention about how one filesystem-backed provider names its
/// artifacts, not a domain concept, so the copies are free to diverge.
/// </para>
/// </remarks>
public static class BackupArtifactId
{
    /// <summary>The separator between the server id and the artifact location.</summary>
    public const string Separator = "::";

    /// <summary>Builds the id for an artifact.</summary>
    /// <param name="serverId">The owning server.</param>
    /// <param name="location">The artifact's absolute location on this machine.</param>
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
