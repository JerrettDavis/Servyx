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
/// broken host). Each leg is additionally bounded by <see cref="DefaultHostTimeout"/>, so one host that hangs
/// costs that budget rather than the caller's whole request. Partial failure degrades; total failure — every
/// host failing, leaving nothing that answered — is reported to the caller instead, see
/// <see cref="DiscoverAsync"/>.
/// </remarks>
public sealed class CompositeServerDiscovery : IServerDiscovery
{
    /// <summary>
    /// The per-host budget for a single fan-out leg. Bounds the whole fan-out at roughly one host's worth of
    /// waiting rather than the sum of every unreachable host's connect timeout.
    /// </summary>
    public static readonly TimeSpan DefaultHostTimeout = TimeSpan.FromSeconds(20);

    private readonly IHostConnectionSource _connections;
    private readonly TimeSpan _hostTimeout;
    private readonly ILogger<CompositeServerDiscovery>? _logger;

    /// <summary>Creates a discovery service fanning out over every host <paramref name="connections"/> reports.</summary>
    /// <param name="connections">The live host set to query.</param>
    /// <param name="loggerFactory">Optional; used to log (and skip) a host whose query fails.</param>
    /// <param name="hostTimeout">Overrides <see cref="DefaultHostTimeout"/>.</param>
    public CompositeServerDiscovery(
        IHostConnectionSource connections,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? hostTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (hostTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(hostTimeout), timeout, "The per-host timeout must be positive.");
        }

        _connections = connections;
        _hostTimeout = hostTimeout ?? DefaultHostTimeout;
        _logger = loggerFactory?.CreateLogger<CompositeServerDiscovery>();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws while at least one host answered — see this type's remarks. It throws when EVERY host
    /// failed, because an empty list would then be indistinguishable from "these hosts run no matching
    /// container", and it is the caller's degraded branch, not its empty-state branch, that the operator needs
    /// to see. It CAN still throw if <see cref="IHostConnectionSource.GetConnectionsAsync"/> itself throws
    /// (e.g. <see cref="HostConnectionRegistry"/> refusing a genuinely empty combined host set), since at that
    /// point there is no host to even attempt.
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
        var failures = new List<(string HostKey, Exception Failure)>();
        foreach (var (hostKey, results, failure) in perHost)
        {
            if (failure is not null)
            {
                _logger?.LogWarning(
                    failure,
                    "Discovery failed for host '{HostKey}'; skipping it and returning results from the "
                    + "remaining host(s).",
                    hostKey);
                failures.Add((hostKey, failure));
                continue;
            }

            aggregated.AddRange(results);
        }

        // Every host failed, so there is no host left whose silence an empty list could honestly represent.
        // Returning [] here is what made a mis-registered host render as "no containers available to adopt"
        // with the real reason visible only in the log; throwing routes it to the caller's degraded branch
        // (CandidatesResult.Failed → the adoption panel's "could not be read" state) with the reason attached.
        // A partial failure still returns the union above: the hosts that answered did answer.
        if (failures.Count > 0 && failures.Count == perHost.Length)
        {
            var detail = string.Join("; ", failures.Select(f => $"{f.HostKey}: {f.Failure.Message}"));
            throw new InvalidOperationException(
                $"Discovery failed on every host ({detail}).",
                new AggregateException(failures.Select(f => f.Failure)));
        }

        return aggregated;
    }

    private async Task<(string HostKey, IReadOnlyList<DiscoveredServer> Results, Exception? Failure)> DiscoverOneAsync(
        HostConnection host, string imageRepository, string requiredMountContainerPath, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_hostTimeout);

        try
        {
            var discovery = new SshDockerServerDiscovery(host.ExecutionTarget);
            var results = await discovery.DiscoverAsync(imageRepository, requiredMountContainerPath, budget.Token)
                .ConfigureAwait(false);

            IReadOnlyList<DiscoveredServer> tagged =
                [.. results.Select(server => server with { HostKey = host.HostKey })];

            return (host.HostKey, tagged, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The budget expired, not the caller's own token — a slow host, reported as this host's failure
            // rather than allowed to hold the whole fan-out open.
            return (host.HostKey, [], new TimeoutException(
                $"Discovery on host '{host.HostKey}' did not complete within {_hostTimeout.TotalSeconds:0.#}s."));
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
