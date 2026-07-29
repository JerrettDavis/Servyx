using System.Collections.Concurrent;
using Servyx.Application.Servers;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Web.Services;

/// <summary>
/// Turns a server id into the <see cref="DockerBackupContext"/> <c>DockerBackupProvider</c> needs: an
/// execution target for the adopted container, the root its backup paths are relative to, where Servyx
/// writes its own archives, and where the image's own cron archives already live.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece only the composition root can supply</strong> — see
/// <c>AddServyxDockerBackups</c>'s remarks. Turning "palworld-server" into a container, a data directory,
/// and a set of include globs is host knowledge, and a plausible default living in the provider would
/// silently back up the wrong paths.
/// </para>
/// <para>
/// <strong>Sessions are cached and owned here.</strong> The provider never disposes an
/// <see cref="IExecutionTarget"/> it is handed, per <see cref="IDockerBackupContextSource"/>'s contract, so
/// one session per server is created on first use and disposed when this service is. Creating a session
/// per call would open a Docker client per listing.
/// </para>
/// <para>
/// <strong>No quiesce step is configured, and that is a stated limitation rather than a hidden one.</strong>
/// Flushing Palworld's in-memory world before archiving needs an authenticated RCON session, which this
/// host does not compose yet. <see cref="DockerBackupContext.Quiesce"/> is therefore
/// <see langword="null"/>, which the provider treats as "no flush was asked for": it archives what is on
/// disk, and a running server may have state it has not written out. The alternative — naming a quiesce
/// step with no control channel to issue it on — is refused outright by
/// <c>DockerBackupProvider.CreateAsync</c>, and rightly: a backup silently taken without the flush its
/// definition asked for is exactly what <c>BackupQuiesceFailedException</c> exists to prevent. Once an
/// RCON session is composed, filling both <see cref="DockerBackupContext.Quiesce"/> and
/// <see cref="DockerBackupContext.Control"/> in here is the whole change.
/// </para>
/// </remarks>
public sealed class ServyxBackupContextSource : IDockerBackupContextSource, IAsyncDisposable
{
    private readonly IServerQueryService _query;
    private readonly ITransport _transport;
    private readonly BackupWiringOptions _options;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context source.</summary>
    /// <param name="query">Resolves a server id to the adopted container and its mount.</param>
    /// <param name="transport">The (write-guarded) transport sessions are opened through.</param>
    /// <param name="options">Where archives are read from and written to.</param>
    public ServyxBackupContextSource(IServerQueryService query, ITransport transport, BackupWiringOptions options)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        _query = query;
        _transport = transport;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<DockerBackupContext> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{serverId}' is not an adopted server on this Docker daemon, so there is nothing to back up.");

        var containerName = detail.Summary.Name;
        var root = Normalize(_options.ContainerDataRoot ?? detail.MountContainerPath ?? BackupWiringOptions.DefaultContainerDataRoot);
        var target = await SessionAsync(containerName, root, ct).ConfigureAwait(false);

        var source = new BackupSource(
            BackupWiringOptions.DataSourceId,
            target,
            root,
            _options.Include,
            [.. _options.Exclude, _options.ForeignDirectory, _options.ForeignDirectory + "/**"]);

        return new DockerBackupContext(
            ServerId: detail.Summary.Id,
            DeploymentKind: "docker",
            Sources: [source],
            Store: new BackupStore(target, root, _options.StoreDirectory),
            Foreign:
            [
                new ForeignBackupSource(
                    PalworldCronBackupAdopter.Id,
                    target,
                    root,
                    _options.ForeignDirectory,
                    "*.tar.gz",
                    // The cron archives' entries are relative to the same data root this source reads, so
                    // they are restorable. A null here would make them listable and inspectable only.
                    RestoreSourceId: BackupWiringOptions.DataSourceId),
            ],
            DefaultRetention: _options.DefaultRetention,

            // Null, deliberately, and not a fabricated step: see the type remarks. A context that declares
            // a quiesce it has no control channel for is refused by the provider outright.
            Quiesce: null,
            Control: null);
    }

    private Task<IExecutionTarget> SessionAsync(string containerName, string root, CancellationToken ct)
    {
        var lazy = _sessions.GetOrAdd(
            containerName,
            key => new Lazy<Task<IExecutionTarget>>(() => _transport.ConnectAsync(
                new TargetDescriptor(
                    "docker",
                    DockerEndpointResolver.Resolve(explicitEndpoint: null).ToString(),
                    CredentialUrn: null,
                    DockerContext: null,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        // The same key ServerWriteModes emits a grant for, so a server the operator
                        // enabled writes on is the server this session is allowed to write to.
                        ["containerName"] = key,
                        ["rootPath"] = root,
                    }),
                ct)));

        return lazy.Value;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _sessions.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A session that failed to open, or that the daemon already closed, must not stop the
                // remaining sessions from being released during shutdown.
            }
        }

        _sessions.Clear();
    }

    private static string Normalize(string root)
    {
        var normalized = root.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }
}
