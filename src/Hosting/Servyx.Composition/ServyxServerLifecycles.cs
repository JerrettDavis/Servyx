using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Servyx.Application.Lifecycle;
using Servyx.Application.Servers;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;

namespace Servyx.Composition;

/// <summary>
/// Turns a server id into the <see cref="IServerLifecycle"/> that server's Start/Restart/Stop/Kill
/// controls drive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the piece only the composition root can supply.</strong> <see cref="ServerLifecycleService"/>
/// — see its own remarks — is constructed once per server, closing over that server's container ref, RCON
/// server name, and parsed <see cref="LifecycleDefinition"/>. Turning a route's <c>{Id}</c> into all three
/// is host knowledge, exactly as <c>ServyxBackupContextSource</c> turns a server id into a
/// <c>DockerBackupContext</c>. Sessions are memoized per container name, mirroring that type's own pattern,
/// so repeated calls for the same server reuse one <see cref="IExecutionTarget"/> rather than reconnecting.
/// </para>
/// <para>
/// <strong>Registered unconditionally, on both sides of the provisioning gate — a label's dependencies,
/// not a capability.</strong> Every member reachable through the <see cref="IServerLifecycle"/> this type
/// hands out that actually mutates anything (Start/Stop/Restart/Kill) is refused by the write guard —
/// <see cref="WriteGuardedExecutionTarget"/> for the container lifecycle call, <c>WriteGuardedRconSession</c>
/// for the RCON stop-ladder stages — unless the server carries <c>WriteMode.Enabled</c>, which only exists
/// when the provisioning gate is open and the operator granted it. Registering this factory itself grants
/// nothing; it only makes the read-only half (<see cref="IServerLifecycle.GetStatusAsync"/>, and rendering
/// the definition's stop-escalation ladder via <see cref="StopPlan"/>) reachable on a read-only host, the
/// same way <c>WritableServers</c> is always registered so a page can honestly answer "is this writable?"
/// even when the answer is always no.
/// </para>
/// <para>
/// With no lifecycle definition loaded (the bundled definition is missing, or its <c>lifecycle</c> block
/// failed to parse), <see cref="GetAsync"/> always returns <see langword="null"/> and <see cref="StopPlan"/>
/// is <see langword="null"/> — the Overview tab then renders no lifecycle controls at all, rather than ones
/// guaranteed to fail.
/// </para>
/// </remarks>
public sealed class ServyxServerLifecycles : IAsyncDisposable
{
    private readonly IServerQueryService _query;
    private readonly ITransport _transport;
    private readonly IContainerStateProbe _stateProbe;
    private readonly IRconChannelResolver _rconResolver;
    private readonly ILogStream _logStream;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LifecycleDefinition? _definition;
    private readonly ConcurrentDictionary<string, Lazy<Task<IExecutionTarget>>> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<IServerLifecycle?>>> _lifecycles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the factory.</summary>
    /// <param name="query">Resolves a server id to the adopted container's detail (name, current state).</param>
    /// <param name="transport">The (write-guarded) transport lifecycle sessions are opened through.</param>
    /// <param name="stateProbe">Read-only "has it exited" probe, shared across every server's lifecycle.</param>
    /// <param name="rconResolver">Resolves the write-guarded RCON control session for a server.</param>
    /// <param name="logStream">The server's console output, used by log-regex readiness probes.</param>
    /// <param name="loggerFactory">Used to create one <see cref="ILogger{TCategoryName}"/> per constructed <see cref="ServerLifecycleService"/>.</param>
    /// <param name="definition">
    /// The bundled definition's parsed <c>lifecycle</c> block, if it loaded successfully at startup. Optional
    /// so this type can be constructed via plain DI activation even when no lifecycle definition exists.
    /// </param>
    public ServyxServerLifecycles(
        IServerQueryService query,
        ITransport transport,
        IContainerStateProbe stateProbe,
        IRconChannelResolver rconResolver,
        ILogStream logStream,
        ILoggerFactory loggerFactory,
        LifecycleDefinition? definition = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(stateProbe);
        ArgumentNullException.ThrowIfNull(rconResolver);
        ArgumentNullException.ThrowIfNull(logStream);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _query = query;
        _transport = transport;
        _stateProbe = stateProbe;
        _rconResolver = rconResolver;
        _logStream = logStream;
        _loggerFactory = loggerFactory;
        _definition = definition;
    }

    /// <summary>
    /// The bundled definition's stop-escalation ladder, or <see langword="null"/> when no lifecycle
    /// definition loaded. Exposed directly so a page can render the plan (e.g. under
    /// <c>WriteMode.PreviewOnly</c>) without needing an <see cref="IServerLifecycle"/> instance at all.
    /// </summary>
    public StopPlan? StopPlan => _definition?.Stop;

    /// <summary>
    /// Returns the <see cref="IServerLifecycle"/> for <paramref name="serverId"/>, or <see langword="null"/>
    /// when no lifecycle definition is available or the server is not (or no longer) adopted.
    /// </summary>
    public Task<IServerLifecycle?> GetAsync(string? serverId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverId) || _definition is null)
        {
            return Task.FromResult<IServerLifecycle?>(null);
        }

        var lazy = _lifecycles.GetOrAdd(serverId, key => new Lazy<Task<IServerLifecycle?>>(() => BuildAsync(key, ct)));
        return lazy.Value;
    }

    private async Task<IServerLifecycle?> BuildAsync(string serverId, CancellationToken ct)
    {
        var definition = _definition;
        if (definition is null)
        {
            return null;
        }

        var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var containerName = detail.Summary.Name;
        var session = await SessionAsync(containerName, ct).ConfigureAwait(false);
        if (session is not IContainerLifecycle containerLifecycle)
        {
            return null;
        }

        return new ServerLifecycleService(
            detail.Summary.Id,
            definition,
            containerLifecycle,
            _stateProbe,
            _rconResolver,
            _logStream,
            _loggerFactory.CreateLogger<ServerLifecycleService>(),
            serverName: containerName);
    }

    private Task<IExecutionTarget> SessionAsync(string containerName, CancellationToken ct)
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
                        // The same key ServerWriteModes emits a grant for, so a server the operator enabled
                        // writes on is the server this lifecycle session is allowed to write to.
                        ["containerName"] = key,
                    }),
                ct)));

        return lazy.Value;
    }

    /// <summary>Disposes every session this factory has opened. Failures are swallowed per-session so one bad session cannot block the rest.</summary>
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
}
