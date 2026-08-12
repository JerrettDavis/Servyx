using Microsoft.Extensions.Logging;
using Servyx.Domain.Hosts;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// The live, combined set of ssh+docker hosts <see cref="CompositeServerDiscovery"/> fans a discovery query out
/// against: every host declared under <c>Servyx:Hosts</c> (see <see cref="SshDockerWiringOptions"/>) plus every
/// enabled database-registered <see cref="Servyx.Domain.Entities.Host"/> row (see <see cref="IHostRepository"/>)
/// — combined, deduplicated by name, and handed out as already-lazy <see cref="LazyConnectingExecutionTarget"/>s
/// that only open an SSH session on first real use, exactly like <c>AddServyxSshDocker</c>'s own single-host
/// wiring already does for <see cref="SshDockerWiringOptions.Hosts"/>[0].
/// </summary>
/// <remarks>
/// <para>
/// <strong>Precedence: configuration wins.</strong> A host declared under <c>Servyx:Hosts:&lt;name&gt;</c> and a
/// database row registered under the SAME name is not refused as a conflict — the configuration-declared entry
/// is authoritative and the database row is silently shadowed for that name (logged at
/// <see cref="LogLevel.Warning"/>), never the other way round. This is deliberate: configuration is the thing
/// an operator can read, diff, and audit outside the running process, while a database row can be registered by
/// anyone who can reach the host-registration UI a later increment adds. Letting a database registration
/// silently override a configuration entry with the same name would let an unprivileged registration shadow a
/// trusted, explicitly-declared host.
/// </para>
/// <para>
/// <strong>Restart-free refresh.</strong> The combined set is computed once and cached; <see cref="Invalidate"/>
/// drops that cache so the next <see cref="GetConnectionsAsync"/> call re-reads <see cref="IHostRepository"/>
/// (the configured half is fixed for the process lifetime, so only the database half can ever change). This is
/// the seam the host-registration surface calls after writing a new <see cref="Servyx.Domain.Entities.Host"/>
/// row, so a freshly-registered host becomes discoverable without a process restart. It reaches that seam
/// through <see cref="IHostConnectionRefresher"/>, a deliberately one-method view of this type: a use case that
/// has just written a host row needs to say "your cache is stale" and nothing more, and must not be handed the
/// ability to enumerate or connect to hosts as a side effect of saying it.
/// </para>
/// <para>
/// <strong>Zero hosts is a normal, queryable empty state.</strong> <c>AddServyxSshDocker</c> constructs this
/// type unconditionally now — even when <see cref="SshDockerWiringOptions.Any"/> is <see langword="false"/> —
/// specifically so a fresh install with no configured host AND no database-registered host yet still has
/// something in the container for a later host-registration flow to attach to, and for
/// <see cref="Invalidate"/> to refresh once the first host is registered. A combined set that is empty by the
/// time <see cref="GetConnectionsAsync"/> runs is therefore NOT an error: it returns an empty list, the same
/// "nothing to discover yet" shape any other <see cref="Servyx.Domain.Discovery.IServerDiscovery"/>
/// implementation reports for an empty host. This is a deliberately different stance from
/// <see cref="SshDockerWiringOptions.FromConfiguration"/>'s throw for a <em>populated but entirely unusable</em>
/// <c>Servyx:Hosts</c> section — that throw guards against an operator typo in an explicit declaration; an
/// absent declaration plus an empty host table is just the honest state of a brand-new install.
/// </para>
/// </remarks>
public sealed class HostConnectionRegistry : IHostConnectionSource, IHostConnectionRefresher
{
    private readonly SshDockerWiringOptions _configuredHosts;
    private readonly IHostRepository _hostRepository;
    private readonly ITransport _transport;
    private readonly ILogger<HostConnectionRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<HostConnection>? _cached;

    /// <summary>Creates a registry over <paramref name="configuredHosts"/> and <paramref name="hostRepository"/>.</summary>
    public HostConnectionRegistry(
        SshDockerWiringOptions configuredHosts,
        IHostRepository hostRepository,
        ITransport transport,
        ILogger<HostConnectionRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(configuredHosts);
        ArgumentNullException.ThrowIfNull(hostRepository);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(logger);

        _configuredHosts = configuredHosts;
        _hostRepository = hostRepository;
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Drops the cached combined set, so the next <see cref="GetConnectionsAsync"/> call re-reads
    /// <see cref="IHostRepository"/> instead of returning a snapshot from before a host was registered or
    /// removed.
    /// </summary>
    public void Invalidate() => Volatile.Write(ref _cached, null);

    /// <inheritdoc />
    public async Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        var snapshot = Volatile.Read(ref _cached);
        if (snapshot is not null)
        {
            return snapshot;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: a concurrent caller may have already rebuilt while this one waited.
            var current = Volatile.Read(ref _cached);
            if (current is not null)
            {
                return current;
            }

            var built = await BuildAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _cached, built);
            return built;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<HostConnection>> BuildAsync(CancellationToken ct)
    {
        var byName = new Dictionary<string, TargetDescriptor>(StringComparer.Ordinal);

        // Database rows first — additive, lowest precedence. A read failure here degrades to "no
        // database-registered hosts this cycle" rather than failing discovery outright: the configured set is
        // still usable on its own, and the next Invalidate()-triggered rebuild gets another chance at the
        // database.
        try
        {
            var rows = await _hostRepository.ListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                if (!row.Enabled)
                {
                    continue;
                }

                byName[row.Name] = RegisteredHostTargetFactory.Build(row);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read registered hosts from the database; discovery will use only the configured "
                + "host(s) this cycle.");
        }

        // Configured hosts — authoritative, override a same-named database row.
        foreach (var host in _configuredHosts.Hosts)
        {
            if (byName.ContainsKey(host.Name))
            {
                _logger.LogWarning(
                    "Host '{HostKey}' is declared under 'Servyx:Hosts' AND registered in the database; the "
                    + "configured entry is authoritative and the database row is ignored for this host.",
                    host.Name);
            }

            byName[host.Name] = host.Target;
        }

        if (byName.Count == 0)
        {
            // Not an error — see this type's remarks. A fresh install with no configured host and no
            // database-registered host yet simply has nothing to discover, until an operator registers one.
            return [];
        }

        return byName
            .Select(entry => new HostConnection(
                entry.Key,
                new LazyConnectingExecutionTarget(innerCt => _transport.ConnectAsync(entry.Value, innerCt))))
            .ToList();
    }
}
