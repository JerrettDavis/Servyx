using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="IServerExecutionTargetResolver"/> implementation over <see cref="IHostConnectionSource"/>: the
/// remote (non-null <c>hostKey</c>) branch is a direct lookup against
/// <see cref="IHostConnectionSource.GetConnectionsAsync"/> — the exact same live, cached, restart-free-refreshed
/// set <see cref="CompositeServerDiscovery"/> already fans discovery queries out over — so registering or
/// removing a host through the UI (see <see cref="HostConnectionRegistry.Invalidate"/>) changes what this
/// resolver hands back on the very next call, with no separate cache of its own to go stale.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The local branch has no <see cref="IHostConnectionSource"/> analogue.</strong>
/// <see cref="IHostConnectionSource"/> only ever enumerates ssh+docker hosts — configured plus
/// database-registered — and has no notion of "the local Docker daemon" at all (see that type's own
/// remarks). A <see langword="null"/> <c>hostKey</c> instead reaches straight for the local
/// <see cref="ITransport"/> this instance was constructed with (typically whatever <c>AddServyxDocker</c>
/// registered, captured by <c>AddServyxSshDocker</c> before it potentially removes/re-registers the
/// process-wide <see cref="ITransport"/> service type for a statically-declared ssh+docker host — see that
/// method's own remarks) and connects it to a <see cref="TargetDescriptor"/> built from
/// <paramref name="serverId"/> alone, mirroring the exact descriptor shape
/// <c>Servyx.Composition.ServyxServerLifecycles</c> and <c>ServyxBackupContextSource</c> already build for a
/// local container session: <c>TransportId: "docker"</c>, an empty <c>Endpoint</c> (so
/// <c>DockerEndpointResolver</c>'s own <c>DOCKER_HOST</c>/OS-default fallback resolves it exactly as those
/// call sites' precomputed endpoint would have), and <c>Options["containerId"] = serverId</c>. Building it
/// this way — rather than precomputing the endpoint here — is what lets this type live in
/// <c>Servyx.Infrastructure.Ssh</c> without a project reference to <c>Servyx.Infrastructure.Docker</c>,
/// exactly like every other type in this file.
/// </para>
/// <para>
/// <strong>No local <see cref="ITransport"/> is a documented, honest failure — not a silent no-op.</strong>
/// A <see langword="null"/> local transport (this process only ever called <c>AddServyxSshDocker</c>, never
/// <c>AddServyxDocker</c> — e.g. a hypothetical host with no local Docker surface at all) makes the local
/// branch throw rather than pretend a session was opened. This mirrors
/// <c>SshDockerServiceCollectionExtensions</c>'s own comment for the equivalent case in
/// <see cref="HostAwareServerDiscovery"/>'s wiring: "the composite is genuinely the only option" — here,
/// with no composite to fall back to for the local branch specifically, there is genuinely no option at all.
/// </para>
/// </remarks>
public sealed class ServerExecutionTargetResolver : IServerExecutionTargetResolver
{
    private readonly IHostConnectionSource _connections;
    private readonly ITransport? _local;

    /// <summary>
    /// Creates a resolver over <paramref name="connections"/> for the remote branch and
    /// <paramref name="local"/> (nullable) for the local one.
    /// </summary>
    /// <param name="connections">The live registered/configured ssh+docker host set.</param>
    /// <param name="local">
    /// The local Docker transport a null <c>hostKey</c> resolves through, or <see langword="null"/> when this
    /// process never registered one (see this type's remarks for what that does to the local branch).
    /// </param>
    public ServerExecutionTargetResolver(IHostConnectionSource connections, ITransport? local)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
        _local = local;
    }

    /// <inheritdoc />
    public async Task<IExecutionTarget> ResolveAsync(string serverId, string? hostKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        if (hostKey is null)
        {
            if (_local is null)
            {
                throw new InvalidOperationException(
                    $"Cannot resolve a local execution target for server '{serverId}': this process registered "
                    + "no local Docker transport (AddServyxDocker was never called ahead of AddServyxSshDocker), "
                    + "so there is nothing for a null host key to fall back to.");
            }

            return await _local.ConnectAsync(LocalDescriptor(serverId), ct).ConfigureAwait(false);
        }

        var connections = await _connections.GetConnectionsAsync(ct).ConfigureAwait(false);
        foreach (var connection in connections)
        {
            if (string.Equals(connection.HostKey, hostKey, StringComparison.Ordinal))
            {
                return connection.ExecutionTarget;
            }
        }

        throw new InvalidOperationException(
            $"Cannot resolve an execution target for server '{serverId}': '{hostKey}' names no currently "
            + "connectable registered/configured ssh+docker host. Refusing to silently fall back to the local "
            + "Docker daemon or a different host, either of which would run this server's reads/writes against "
            + "the wrong machine.");
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> the local branch connects <see cref="_local"/> against — an
    /// empty <see cref="TargetDescriptor.Endpoint"/> so the transport's own endpoint resolution (DOCKER_HOST,
    /// then an OS-appropriate default) applies exactly as it does for every other local Docker session this
    /// codebase opens, and <c>containerId</c> set to <paramref name="serverId"/>, the same option key
    /// <c>DockerTransport.ResolveContainerRef</c> reads first.
    /// </summary>
    private static TargetDescriptor LocalDescriptor(string serverId) => new(
        "docker",
        Endpoint: string.Empty,
        CredentialUrn: null,
        DockerContext: null,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerId"] = serverId,
        });
}
