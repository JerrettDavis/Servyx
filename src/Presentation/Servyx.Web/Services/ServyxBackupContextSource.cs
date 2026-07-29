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
/// <strong>Quiesce is attached exactly when a control channel exists, and never otherwise.</strong> When
/// the operator has configured an RCON channel for a server (see <see cref="RconWiringOptions"/>), this
/// source fills both <see cref="DockerBackupContext.Control"/> and
/// <see cref="DockerBackupContext.Quiesce"/> with the definition's own step — <c>rcon</c> <c>save</c>, 30s
/// — and <c>DockerBackupProvider</c> issues it before a single byte is archived. When no channel is
/// configured, both stay <see langword="null"/>: the provider treats that as "no flush was asked for" and
/// archives on-disk state, exactly as it did before, recording the absence in the manifest's
/// <c>quiesceCommand</c> field so an archive taken without a flush is distinguishable from one taken with
/// it. Naming a quiesce step with no channel to issue it on is refused outright by
/// <c>DockerBackupProvider.CreateAsync</c>, and rightly.
/// </para>
/// <para>
/// <strong>A configured quiesce that fails produces no archive — there is no fallback, by design.</strong>
/// <c>DockerBackupProvider.QuiesceAsync</c> converts every failure route (a refusal from the write guard, a
/// rejected credential, an unreachable endpoint, a 30-second timeout, a <c>Success: false</c> reply) into
/// <c>BackupQuiesceFailedException</c> before <c>CollectAsync</c> is reached, so no archive and no manifest
/// are written. Continuing "best effort" would produce a file that looks exactly like a good backup and is
/// not one — and the operator would only find out at restore time. Turning the channel <em>off</em> is the
/// explicit, per-server way to say "archive without flushing"; it is never what a failure silently
/// degrades into.
/// </para>
/// </remarks>
public sealed class ServyxBackupContextSource : IDockerBackupContextSource, IAsyncDisposable
{
    private readonly IServerQueryService _query;
    private readonly ITransport _transport;
    private readonly BackupWiringOptions _options;
    private readonly ServyxRconChannels _rcon;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a context source.</summary>
    /// <param name="query">Resolves a server id to the adopted container and its mount.</param>
    /// <param name="transport">The (write-guarded) transport sessions are opened through.</param>
    /// <param name="options">Where archives are read from and written to.</param>
    /// <param name="rcon">
    /// The configured RCON control channels. Defaults to <see cref="ServyxRconChannels.None"/>, which
    /// reproduces the pre-M2 behaviour exactly: no control channel, therefore no quiesce step, therefore an
    /// archive of on-disk state that says so in its manifest.
    /// </param>
    public ServyxBackupContextSource(
        IServerQueryService query,
        ITransport transport,
        BackupWiringOptions options,
        ServyxRconChannels? rcon = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);

        _query = query;
        _transport = transport;
        _options = options;
        _rcon = rcon ?? ServyxRconChannels.None;
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

        // Null unless the operator configured an RCON channel for this server. The pair below is all-or-
        // nothing on purpose: a context carrying a quiesce step with no channel is refused by the provider,
        // and a context carrying a channel with no step would open a control session it never used.
        var control = _rcon.TryGetSession(detail.Summary.Id, containerName);

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

            // The definition's own backup.quiesce entry — { kind: control, channel: rcon, command: save,
            // timeout: 30s } — attached only when there is a channel to issue it on. If it fails, the
            // provider raises BackupQuiesceFailedException and writes nothing at all; there is deliberately
            // no "archive anyway" path, because an un-flushed archive is indistinguishable from a good one
            // until the day someone restores it.
            Quiesce: control is null
                ? null
                : new QuiesceStep(RconWiringOptions.QuiesceCommandId, null, RconWiringOptions.QuiesceTimeout),
            Control: control);
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
