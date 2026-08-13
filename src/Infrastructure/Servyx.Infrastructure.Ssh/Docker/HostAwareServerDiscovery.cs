using Servyx.Domain.Discovery;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// The <see cref="IServerDiscovery"/> <c>AddServyxSshDocker</c> actually binds process-wide whenever no
/// ssh+docker host is declared in static configuration (<see cref="SshDockerWiringOptions.Any"/> is
/// <see langword="false"/>): local Docker discovery alone for as long as <see cref="IHostConnectionSource"/>
/// reports no host to fan a query out over, and local Docker discovery UNIONED with
/// <see cref="CompositeServerDiscovery"/>'s fan-out the moment it reports at least one — the case this type
/// exists for: a database-registered <see cref="Servyx.Domain.Entities.Host"/> row an operator added through
/// the UI after this process started, with zero <c>Servyx:Hosts</c> configuration ever declared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not just always use <see cref="CompositeServerDiscovery"/>.</strong> <see cref="HostConnectionRegistry"/>
/// — the production <see cref="IHostConnectionSource"/> — only ever enumerates ssh+docker hosts (configured
/// plus database-registered); it has no notion of "the local Docker daemon" at all, and never will (see its
/// own remarks). Binding <see cref="IServerDiscovery"/> to <see cref="CompositeServerDiscovery"/>
/// unconditionally, with nothing else queried, would mean the overwhelming majority of installs — a plain
/// local Docker daemon, zero ssh+docker hosts ever configured or registered — stop discovering anything at
/// all the moment the "a registered host's containers never appear as adoption candidates" bug was fixed,
/// trading it for a strictly worse regression. This type is the seam that avoids that: it always queries
/// whatever local discovery <c>AddServyxDocker</c> already registered, and additionally queries the composite
/// fan-out once there is something for it to fan out over — never one instead of the other, since a
/// registered/configured host has no bearing on whether the local Docker daemon still has an already-adopted
/// server running on it (see <see cref="DiscoverAsync"/>'s own remarks for why the earlier either/or was a
/// regression in its own right).
/// </para>
/// <para>
/// <strong>Checked per call, not once at construction.</strong> <see cref="IHostConnectionSource.GetConnectionsAsync"/>
/// is cheap once its own cache is warm (see <see cref="HostConnectionRegistry"/>), and re-checking on every
/// <see cref="DiscoverAsync"/> call is what makes a host registered through the UI — with zero
/// <c>Servyx:Hosts</c> config, at any point after this process started — become discoverable without a
/// restart: <c>HostRegistrationService.RegisterAsync</c> calls <c>IHostConnectionRefresher.Invalidate()</c>
/// after writing the row, so the very next call here sees it.
/// </para>
/// <para>
/// <strong>Not the posture used for a statically-declared host.</strong> When <see cref="SshDockerWiringOptions.Any"/>
/// is <see langword="true"/>, <c>AddServyxSshDocker</c> does not wire this type at all — it binds
/// <see cref="IServerDiscovery"/> straight to <see cref="CompositeServerDiscovery"/>, unconditionally
/// displacing local Docker discovery for the process's whole lifetime, exactly as it always has. That
/// distinction is deliberate: a statically-declared host is a durable operator decision to run in
/// remote-host mode, known at process start, whereas this type exists only for the interval before any host
/// — static or database-registered — exists at all, and for however long that interval lasts on a
/// zero-config install.
/// </para>
/// </remarks>
public sealed class HostAwareServerDiscovery : IServerDiscovery
{
    private readonly IHostConnectionSource _connections;
    private readonly IServerDiscovery _remote;
    private readonly IServerDiscovery _local;

    /// <summary>
    /// Creates a discovery service that unions <paramref name="local"/> with <paramref name="remote"/> once
    /// <paramref name="connections"/> reports at least one host, and consults <paramref name="local"/> alone
    /// otherwise.
    /// </summary>
    /// <param name="connections">The live ssh+docker host set — configured plus database-registered.</param>
    /// <param name="remote">
    /// Additionally consulted, concurrently with <paramref name="local"/>, when <paramref name="connections"/>
    /// reports at least one host.
    /// </param>
    /// <param name="local">Always consulted — the local Docker daemon has no relationship to <paramref name="connections"/>.</param>
    public HostAwareServerDiscovery(IHostConnectionSource connections, IServerDiscovery remote, IServerDiscovery local)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(local);

        _connections = connections;
        _remote = remote;
        _local = local;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A registered/configured host never displaces the local Docker daemon as a discovery source — it only
    /// ever adds to it. <see cref="_local"/> and <see cref="_remote"/> are queried concurrently and their
    /// results unioned, rather than the earlier either/or that made an already-adopted local server vanish
    /// from every discovery-backed read (dashboard status, start/stop, detail) the instant an operator
    /// registered their first remote host. No de-duplication is needed between the two: <see cref="_remote"/>
    /// (<c>CompositeServerDiscovery</c> in the production wiring) only ever iterates hosts reported by
    /// <see cref="IHostConnectionSource"/>, which enumerates ssh+docker hosts exclusively — it has no notion of
    /// "the local Docker daemon" (see <see cref="HostConnectionRegistry"/>'s own remarks) — so the two result
    /// sets are structurally disjoint.
    /// </remarks>
    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        string imageRepository, string requiredMountContainerPath, CancellationToken ct = default)
    {
        var hosts = await _connections.GetConnectionsAsync(ct).ConfigureAwait(false);

        if (hosts.Count == 0)
        {
            // No registered/configured host to fan a query out over; querying _remote would be a guaranteed
            // no-op (see CompositeServerDiscovery's remarks), so prefer the cheaper local-only path.
            return await _local.DiscoverAsync(imageRepository, requiredMountContainerPath, ct).ConfigureAwait(false);
        }

        var localTask = _local.DiscoverAsync(imageRepository, requiredMountContainerPath, ct);
        var remoteTask = _remote.DiscoverAsync(imageRepository, requiredMountContainerPath, ct);
        await Task.WhenAll(localTask, remoteTask).ConfigureAwait(false);

        var local = await localTask.ConfigureAwait(false);
        var remote = await remoteTask.ConfigureAwait(false);

        var combined = new List<DiscoveredServer>(local.Count + remote.Count);
        combined.AddRange(local);
        combined.AddRange(remote);
        return combined;
    }
}
