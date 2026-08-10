using System.Collections.Concurrent;
using Servyx.Application.Servers;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Process;
using GameDefinition = Servyx.Domain.Definitions.Model.GameDefinition;

namespace Servyx.Composition;

/// <summary>
/// Turns a server id into the deployment facts <see cref="ISurfaceResolver"/> expands locators against, and
/// into the live sessions <c>SettingStateResolver</c> reads those surfaces through.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece only the composition root can supply</strong>, for the same reason
/// <see cref="ServyxBackupContextSource"/> is: <c>${DATA_DIR}</c> and <c>${COMPOSE_DIR}</c> are per-server
/// deployment facts that no transport knows, and a plausible default living in
/// <c>Servyx.Config</c> would silently resolve a surface onto the wrong filesystem. Until this type existed,
/// the placeholder <c>UnconfiguredSurfaceResolutionContextSource</c> answered every question with
/// <see langword="null"/>, so surface resolution resolved nothing at all.
/// </para>
/// <para>
/// <strong>Two sessions per server, never one.</strong> A <see cref="SurfaceResolutionContext"/> names
/// exactly one <see cref="SurfaceResolutionContext.SessionRoot"/>, and a <c>kind: docker</c> deployment's
/// <c>${DATA_DIR}</c> is a path inside the container while its <c>${COMPOSE_DIR}</c> is a host directory
/// holding <c>.env</c> and <c>compose.yaml</c>. <see cref="ServyxBackupContextSource"/> already splits a
/// definition's backup globs along exactly this line and builds one <c>BackupSource</c> per root. So this
/// source opens up to two sessions and hands out a <em>different</em> context for each: the container
/// session's context carries <see cref="SurfaceResolutionContext.ComposeDirectory"/> as
/// <see langword="null"/>, and the compose session's carries
/// <see cref="SurfaceResolutionContext.DataDirectory"/> as <see langword="null"/>. That nulling is the
/// safety property, not a formality — leaving both populated would let a host path resolve against the
/// container session and be read from the wrong filesystem, which does not fail, it succeeds wrongly.
/// </para>
/// <para>
/// <strong>Nothing here guesses.</strong> <c>${DATA_DIR}</c> comes from the deployment profile's
/// <c>dataDir</c>, or from the adopted container's own reported mount path, or the data session is not
/// opened at all. <c>${COMPOSE_DIR}</c> is operator-configured (<c>Servyx:Backups:ComposeDirectory</c>) and
/// is never inferred: there is no way to discover a host directory from inside a container. An unavailable
/// input yields no session, and <see cref="ISurfaceResolver"/> then reports one actionable failure per
/// surface naming what is missing.
/// </para>
/// <para>
/// <strong>Sessions are cached and owned here.</strong> One pair per server is opened on first use and
/// disposed with this service, so a settings view does not re-connect per page load and nothing downstream
/// disposes a session it was handed — the same ownership rule
/// <see cref="Servyx.Infrastructure.Docker.Backups.IDockerBackupContextSource"/> already states.
/// </para>
/// <para>
/// <strong>It asks discovery, never <see cref="IServerQueryService"/> — and that is a correctness
/// requirement, not a layering preference.</strong> <c>ServerQueryService</c> optionally consumes
/// <see cref="ISettingStateResolverFactory"/>, which consumes this type; all three are singletons, so
/// asking the query service for a server's details would be asking the very instance already executing.
/// <c>GetServerDetailAsync</c> enriches its settings rows, enrichment builds this session set, and the
/// memoizing <see cref="Lazy{T}"/> publishes its task at the first await — so the re-entrant call does not
/// recurse and blow the stack, it receives the pending task the outer frame is already awaiting and the two
/// wait on each other forever. Silent, permanent, and invisible to every catch block, because a deadlocked
/// task never throws. Deferring the lookup behind a <c>Func</c> does not help: the cycle is at call time,
/// not construction time. The dependency is therefore removed rather than guarded — the only fact needed
/// here is which container id and name a server id names, which <see cref="IServerDiscovery"/> answers
/// directly and which nothing in the settings pipeline sits above.
/// </para>
/// <para>
/// <strong>Read-only by construction of its consumers, not by privilege.</strong> Both sessions are opened
/// over write-guarded transports exactly as every other session in this process is, so a server without
/// <c>Servyx:Servers:&lt;name&gt;:WriteMode = Enabled</c> is refused a write here as everywhere else. The
/// settings-read path issues none regardless.
/// </para>
/// </remarks>
public sealed class ServyxSurfaceResolutionContextSource
    : ISurfaceResolutionContextSource, IServerConfigSessionSource, IAsyncDisposable, IDisposable
{
    /// <summary>Description used for the session rooted at the workload's own data directory.</summary>
    internal const string DataSessionDescription = "the deployment's data directory";

    /// <summary>Description used for the session rooted at the operator-configured host compose directory.</summary>
    internal const string ComposeSessionDescription = "the host compose directory";

    private readonly IServerDiscovery _discovery;
    private readonly AdoptionCriteria? _criteria;
    private readonly ITransport _transport;
    private readonly GameDefinition? _definition;
    private readonly string? _containerDataRoot;
    private readonly string? _composeDirectory;
    private readonly ITransport? _composeTransport;

    private readonly ConcurrentDictionary<string, Lazy<Task<ServerConfigSessions?>>> _byServer =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<IExecutionTarget, SurfaceResolutionContext> _contexts =
        new(ReferenceComparer.Instance);

    /// <summary>Creates the context source.</summary>
    /// <param name="discovery">
    /// Lists candidate containers, and is deliberately the LOWEST layer that can answer this type's only
    /// question — which container id and name a server id names.
    /// </param>
    /// <param name="criteria">
    /// The single loaded definition's adoption criteria, or null when none is derivable — in which case no
    /// server is discoverable here and every surface degrades to unreadable-with-a-reason. Also supplies the
    /// last-resort <c>${DATA_DIR}</c> fallback: <see cref="AdoptionCriteria.RequiredMountContainerPath"/> is
    /// exactly what <c>ServerDetail.MountContainerPath</c> reports for an adopted container, so nothing is
    /// lost by reading it from here.
    /// </param>
    /// <param name="transport">The (write-guarded) transport the data session is opened through.</param>
    /// <param name="definition">
    /// The single loaded game definition, or null when none (or more than one) is loaded — the same
    /// "exactly one definition loaded" rule the RCON and backup wiring already applies. With no definition
    /// there is no declared surface set, so no server has any surfaces to read and every state degrades to
    /// unreadable-with-a-reason rather than to a guess.
    /// </param>
    /// <param name="containerDataRoot">
    /// An explicit override for <c>${DATA_DIR}</c> (<c>Servyx:Backups:ContainerDataRoot</c>), or null to use
    /// the profile's own <c>dataDir</c> and then the adopted container's reported mount path.
    /// </param>
    /// <param name="composeDirectory">
    /// The absolute host directory holding this server's compose file and <c>.env</c>
    /// (<c>Servyx:Backups:ComposeDirectory</c>), or null when the operator has not configured one — in which
    /// case no compose session exists and every <c>${COMPOSE_DIR}</c>-rooted surface is reported
    /// unresolvable rather than resolved against a guess.
    /// </param>
    /// <param name="composeTransport">
    /// A write-guarded transport reaching <paramref name="composeDirectory"/>, or null when that option is
    /// unset. Must already be a <c>WriteGuardedTransport</c>, built at its construction site like every
    /// other transport in this process.
    /// </param>
    public ServyxSurfaceResolutionContextSource(
        IServerDiscovery discovery,
        AdoptionCriteria? criteria,
        ITransport transport,
        GameDefinition? definition = null,
        string? containerDataRoot = null,
        string? composeDirectory = null,
        ITransport? composeTransport = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(transport);

        _discovery = discovery;
        _criteria = criteria;
        _transport = transport;
        _definition = definition;
        _containerDataRoot = string.IsNullOrWhiteSpace(containerDataRoot) ? null : containerDataRoot;
        _composeDirectory = string.IsNullOrWhiteSpace(composeDirectory) ? null : composeDirectory.TrimEnd('/', '\\');
        _composeTransport = composeTransport;
    }

    /// <inheritdoc />
    public async Task<SurfaceResolutionContext?> GetAsync(
        string serverId,
        IExecutionTarget target,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(target);

        // Opening the sessions is what registers their contexts, so the lookup below can only succeed once
        // this server has been described at least once.
        await GetAsync(serverId, ct).ConfigureAwait(false);

        // Matched by session identity rather than by server id: the two sessions for one server answer to
        // two different contexts, and only the caller's own target says which one is being asked about.
        return _contexts.TryGetValue(target, out var context) ? context : null;
    }

    /// <inheritdoc />
    public Task<ServerConfigSessions?> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var lazy = _byServer.GetOrAdd(
            serverId,
            key => new Lazy<Task<ServerConfigSessions?>>(() => BuildAsync(key, ct)));

        return lazy.Value;
    }

    private async Task<ServerConfigSessions?> BuildAsync(string serverId, CancellationToken ct)
    {
        // Docker is the only deployment kind Servyx discovers today, so it is the only profile whose
        // surfaces can be bound to a discovered server. A process profile's surfaces are declared against a
        // deployment this host has no session for, and silently reading them over the Docker transport would
        // be the wrong-filesystem failure again.
        var profile = _definition?.Deployments.FirstOrDefault(d => d.Kind == DeploymentKind.Docker);
        if (profile is null || profile.Surfaces.Count == 0 || _criteria is null)
        {
            return null;
        }

        var container = await DiscoverAsync(serverId, ct).ConfigureAwait(false);
        if (container is null)
        {
            return null;
        }

        var sessions = new List<ConfigSession>(2);

        var dataRoot = Normalize(_containerDataRoot ?? profile.DataDir ?? _criteria.RequiredMountContainerPath);
        if (dataRoot is not null)
        {
            var target = await ConnectAsync(
                _transport,
                BuildDockerDescriptor(container.Name, container.ServerId, dataRoot),
                ct).ConfigureAwait(false);

            if (target is not null)
            {
                sessions.Add(Register(
                    target,
                    DataSessionDescription,
                    new SurfaceResolutionContext(
                        _transport.Capabilities,
                        SessionRoot: dataRoot,
                        DataDirectory: dataRoot,

                        // Null on purpose. See this type's remarks: a host compose path resolved against the
                        // container session would be read from inside the container, successfully and wrongly.
                        ComposeDirectory: null,
                        DataDirectoryIsContainerScoped: true)));
            }
        }

        if (_composeDirectory is { } composeDirectory && _composeTransport is not null)
        {
            var target = await ConnectAsync(
                _composeTransport,
                BuildComposeDescriptor(composeDirectory, container.Name, container.ServerId),
                ct).ConfigureAwait(false);

            if (target is not null)
            {
                sessions.Add(Register(
                    target,
                    ComposeSessionDescription,
                    new SurfaceResolutionContext(
                        _composeTransport.Capabilities,
                        SessionRoot: composeDirectory,

                        // Null for the mirror-image reason: ${DATA_DIR} names a container path, and this
                        // session's file channel reaches the host.
                        DataDirectory: null,
                        ComposeDirectory: composeDirectory,
                        DataDirectoryIsContainerScoped: false)));
            }
        }

        return new ServerConfigSessions(sessions, profile.Surfaces);
    }

    /// <summary>
    /// Finds the container <paramref name="serverId"/> names, matching id first and then name — the same
    /// two-step <c>ServerQueryService.GetServerDetailAsync</c> uses, so a route that works there works here.
    /// </summary>
    /// <remarks>
    /// A daemon that is down is a normal condition for a self-hosted panel, and a settings view must degrade
    /// to "could not be read" rather than throw out of a page load.
    /// </remarks>
    private async Task<DiscoveredServer?> DiscoverAsync(string serverId, CancellationToken ct)
    {
        IReadOnlyList<DiscoveredServer> candidates;
        try
        {
            candidates = await _discovery
                .DiscoverAsync(_criteria!.ImageRepository, _criteria.RequiredMountContainerPath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return candidates.FirstOrDefault(s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.OrdinalIgnoreCase));
    }

    private ConfigSession Register(IExecutionTarget target, string description, SurfaceResolutionContext context)
    {
        _contexts[target] = context;
        return new ConfigSession(target, description);
    }

    /// <summary>
    /// Opens one session, or reports none. A daemon that is down, a container that has been removed, or a
    /// compose directory that does not exist are all normal conditions for a self-hosted panel — the
    /// settings view degrades to "could not be read" rather than throwing out of a page load.
    /// </summary>
    private static async Task<IExecutionTarget?> ConnectAsync(
        ITransport transport,
        TargetDescriptor descriptor,
        CancellationToken ct)
    {
        try
        {
            return await transport.ConnectAsync(descriptor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Builds the descriptor the container-rooted session is opened against.</summary>
    /// <remarks>
    /// Mirrors <see cref="ServyxBackupContextSource"/>'s own descriptor, including the container id: that is
    /// the identity the operator's per-server write grant is keyed on, and omitting it would resolve every
    /// session read-only regardless of what the operator granted. This path never writes, but a descriptor
    /// that misdescribes its own server is a trap for whatever reuses it.
    /// </remarks>
    private static TargetDescriptor BuildDockerDescriptor(string containerName, string containerId, string root) =>
        new(
            "docker",
            DockerEndpointResolver.Resolve(explicitEndpoint: null).ToString(),
            CredentialUrn: null,
            DockerContext: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerId"] = containerId,
                ["containerName"] = containerName,
                ["rootPath"] = root,
            });

    /// <summary>Builds the descriptor the host compose session is opened against.</summary>
    private static TargetDescriptor BuildComposeDescriptor(string composeDirectory, string containerName, string containerId) =>
        new(
            LocalProcessTransport.Id,
            composeDirectory,
            CredentialUrn: null,
            DockerContext: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LocalProcessTransport.RootPathOption] = composeDirectory,
                [ComposeWriteModeResolver.ContainerIdOption] = containerId,
                [ComposeWriteModeResolver.ContainerNameOption] = containerName,
            });

    private static string? Normalize(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var normalized = root.Replace('\\', '/').TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _byServer.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            // Only a build that has already finished can be drained. Awaiting one still in flight would
            // block disposal on however long a daemon takes to answer — and, if that build is itself stuck,
            // forever. Disposal must always terminate; a session belonging to an unfinished build is
            // released when its own transport is, which is the same guarantee it had before this type
            // existed.
            if (!lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            var sessions = lazy.Value.Result;

            foreach (var session in sessions?.Sessions ?? [])
            {
                try
                {
                    await session.Target.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A session the daemon already closed must not stop the rest from being released.
                }
            }
        }

        _byServer.Clear();
        _contexts.Clear();
    }

    /// <summary>
    /// Releases the same sessions <see cref="DisposeAsync"/> does, synchronously.
    /// </summary>
    /// <remarks>
    /// Implemented alongside <see cref="IAsyncDisposable"/> rather than instead of it because
    /// <c>ServiceProvider.Dispose()</c> — which every synchronously-disposed host and test harness calls —
    /// <em>throws</em> for a resolved singleton that implements only <see cref="IAsyncDisposable"/>. A
    /// service registered in <c>AddServyxCore</c> is resolved by hosts this project does not own, so it has
    /// to be disposable both ways. There is no sync-over-async hazard worth avoiding here: every task being
    /// awaited has already completed or is a session teardown, and disposal runs with no synchronization
    /// context.
    /// </remarks>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Keys the context map by session identity. Two sessions for one server are distinguished only by which
    /// object they are, so value equality — which a record-shaped <see cref="IExecutionTarget"/> in a test
    /// would supply — must not be allowed to collapse them.
    /// </summary>
    private sealed class ReferenceComparer : IEqualityComparer<IExecutionTarget>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(IExecutionTarget? x, IExecutionTarget? y) => ReferenceEquals(x, y);

        public int GetHashCode(IExecutionTarget obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
