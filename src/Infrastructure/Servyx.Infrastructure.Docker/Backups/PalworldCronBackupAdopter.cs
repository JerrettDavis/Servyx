using Servyx.Domain.Backups;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// Discovers the <c>*.tar.gz</c> archives the <c>thijsvanloef/palworld-server-docker</c> image's own cron
/// job writes into its data directory, and surfaces them as <see cref="BackupOwnership.Foreign"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, structurally.</strong> This type touches exactly two members of
/// <see cref="IExecutionTarget"/> — <see cref="IExecutionTarget.ListDirectoryAsync"/> and, for the
/// directory's own existence, nothing else. It never calls <c>WriteFileAsync</c>, <c>DeleteAsync</c>,
/// <c>OpenReadAsync</c>, or <c>ExecuteAsync</c>, so there is no code path here that can create, move,
/// rename, or delete a file. That matters more than usual: these archives belong to the image's 30-day
/// cron retention, and an operator's disaster recovery depends on them still being there.
/// </para>
/// <para>
/// Every artifact it returns is stamped <see cref="BackupOwnership.Foreign"/> unconditionally — the value
/// is a constant in this file, not a parameter, so no configuration mistake can cause a cron archive to
/// be reported as Servyx-owned and thereby become a prune candidate.
/// </para>
/// </remarks>
public sealed class PalworldCronBackupAdopter : IBackupAdopter
{
    /// <summary>The adapter id the Palworld definition's <c>backup.adopt</c> block names.</summary>
    public const string Id = "palworld-docker-cron";

    private readonly IDockerBackupContextSource _contexts;

    /// <summary>Creates an adopter over the given context source.</summary>
    /// <param name="contexts">Supplies the per-server backup context, including its foreign archive directories.</param>
    public PalworldCronBackupAdopter(IDockerBackupContextSource contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <inheritdoc />
    public string AdapterId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// The image's cron job only exists in the container deployment profile; the bare-metal
    /// <c>native-steamcmd</c> profile has no such mechanism, so there is nothing for this adopter to find
    /// there and it declines rather than reporting an empty list as if it had looked.
    /// </remarks>
    public bool Supports(string deploymentKind) =>
        string.Equals(deploymentKind, "docker", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>
    /// Lists each declared foreign directory once and returns the files matching its pattern. A directory
    /// that does not exist yields nothing rather than throwing: a server whose cron has not run yet is a
    /// normal state, not an error.
    /// </remarks>
    public async Task<IReadOnlyList<BackupArtifact>> DiscoverAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var context = await _contexts.GetAsync(serverId, ct).ConfigureAwait(false);
        if (context is null || !Supports(context.DeploymentKind))
        {
            return [];
        }

        var artifacts = new List<BackupArtifact>();

        foreach (var source in context.Foreign.Where(f => string.Equals(f.AdapterId, Id, StringComparison.Ordinal)))
        {
            var resolver = new SandboxedPathResolver(source.Root);
            var directory = BackupGlob.Normalize(source.Directory);

            IReadOnlyList<FileEntry> entries;
            try
            {
                entries = await source.Target
                    .ListDirectoryAsync(resolver.Resolve(directory.Length == 0 ? "." : directory), ct)
                    .ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory || !BackupGlob.Matches(source.Pattern, entry.Name))
                {
                    continue;
                }

                var location = Combine(source.Root, directory, entry.Name);

                artifacts.Add(new BackupArtifact(
                    BackupArtifactId.Format(context.ServerId, location),

                    // Constant, never a parameter: a foreign archive can never be relabelled by config.
                    BackupOwnership.Foreign,
                    entry.ModifiedAt ?? DateTimeOffset.UnixEpoch,
                    entry.SizeBytes ?? 0,
                    location));
            }
        }

        return artifacts;
    }

    private static string Combine(string root, string directory, string fileName)
    {
        var normalizedRoot = root.Replace('\\', '/').TrimEnd('/');
        var relative = directory.Length == 0 ? fileName : directory + "/" + fileName;
        return normalizedRoot.Length == 0 ? "/" + relative : normalizedRoot + "/" + relative;
    }
}
