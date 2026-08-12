using Microsoft.Extensions.Logging;
using Servyx.Domain.Discovery;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="IServerDiscovery"/> that fans a query out across every host <see cref="IHostConnectionSource"/>
/// currently reports — every configured ssh+docker host, plus every enabled database-registered
/// <see cref="Servyx.Domain.Entities.Host"/> (see <see cref="HostConnectionRegistry"/>, the production
/// <see cref="IHostConnectionSource"/>) — replacing the single-host limitation
/// <see cref="SshDockerServerDiscovery"/> has on its own (it only ever sees the one
/// <see cref="Servyx.Domain.Transport.IExecutionTarget"/> session it was constructed with).
/// </summary>
/// <remarks>
/// One host's discovery failing does not fail the others: <see cref="DiscoverAsync"/> runs every host's query
/// concurrently (each delegated to its own <see cref="SshDockerServerDiscovery"/> instance over that host's
/// session), logs and skips a host whose query throws, and returns the union of results from every host that
/// succeeded. This mirrors <c>ServerAdoptionService</c>'s own "degrade honestly, don't fail the whole operation
/// for one bad input" shape for a different kind of partial failure (a broken database read rather than a
/// broken host).
/// </remarks>
public sealed class CompositeServerDiscovery : IServerDiscovery
{
    private readonly IHostConnectionSource _connections;
    private readonly ILogger<CompositeServerDiscovery>? _logger;

    /// <summary>Creates a discovery service fanning out over every host <paramref name="connections"/> reports.</summary>
    /// <param name="connections">The live host set to query.</param>
    /// <param name="loggerFactory">Optional; used to log (and skip) a host whose query fails.</param>
    public CompositeServerDiscovery(IHostConnectionSource connections, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(connections);

        _connections = connections;
        _logger = loggerFactory?.CreateLogger<CompositeServerDiscovery>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws for a single unreachable/misbehaving host — see this type's remarks. It CAN still throw if
    /// <see cref="IHostConnectionSource.GetConnectionsAsync"/> itself throws (e.g. <see cref="HostConnectionRegistry"/>
    /// refusing a genuinely empty combined host set), since at that point there is no host to even attempt.
    /// </remarks>
    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        string imageRepository,
        string requiredMountContainerPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredMountContainerPath);

        var hosts = await _connections.GetConnectionsAsync(ct).ConfigureAwait(false);

        var perHost = await Task.WhenAll(hosts.Select(
            host => DiscoverOneAsync(host, imageRepository, requiredMountContainerPath, ct))).ConfigureAwait(false);

        var aggregated = new List<DiscoveredServer>();
        foreach (var (hostKey, results, failure) in perHost)
        {
            if (failure is not null)
            {
                _logger?.LogWarning(
                    failure,
                    "Discovery failed for host '{HostKey}'; skipping it and returning results from the "
                    + "remaining host(s).",
                    hostKey);
                continue;
            }

            aggregated.AddRange(results);
        }

        return aggregated;
    }

    private static async Task<(string HostKey, IReadOnlyList<DiscoveredServer> Results, Exception? Failure)> DiscoverOneAsync(
        HostConnection host, string imageRepository, string requiredMountContainerPath, CancellationToken ct)
    {
        try
        {
            var discovery = new SshDockerServerDiscovery(host.ExecutionTarget);
            var results = await discovery.DiscoverAsync(imageRepository, requiredMountContainerPath, ct)
                .ConfigureAwait(false);

            IReadOnlyList<DiscoveredServer> tagged =
                [.. results.Select(server => server with { HostKey = host.HostKey })];

            return (host.HostKey, tagged, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (host.HostKey, [], ex);
        }
    }
}
