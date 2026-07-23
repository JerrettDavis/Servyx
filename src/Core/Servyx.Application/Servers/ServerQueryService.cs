using System.Runtime.CompilerServices;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Servers;

/// <summary>
/// <see cref="IServerQueryService"/> implementation backed by <see cref="Servyx.Domain"/> abstractions.
/// Every public method here is defensive: a transport exception from discovery, metrics, or the log
/// stream is caught and turned into an honest "not available" result rather than propagating — Docker
/// being unreachable is a normal, expected condition for a self-hosted panel, not a bug.
/// </summary>
public sealed class ServerQueryService : IServerQueryService
{
    /// <summary>
    /// The settings this milestone reads from a live container's environment, in display order. Mirrors
    /// the <c>settings</c> block of <c>definitions/palworld-docker.yaml</c>. A future milestone should
    /// source this list from the parsed game definition instead of hardcoding it here — see the
    /// "graceful degradation" note in the project report for why full definition-schema parsing was out
    /// of scope for this pass.
    /// </summary>
    private static readonly (string Key, string Label, string Group, bool IsSecret)[] KnownSettings =
    [
        ("SERVER_NAME", "Server name", "Identity", false),
        ("SERVER_DESCRIPTION", "Description", "Identity", false),
        ("PORT", "Game port", "Networking", false),
        ("RCON_PORT", "RCON port", "Networking", false),
        ("PLAYERS", "Max players", "Gameplay", false),
        ("DIFFICULTY", "Difficulty", "Gameplay", false),
        ("DAY_TIME_SPEEDRATE", "Day time speed", "Gameplay", false),
        ("ENABLE_PLAYER_TO_PLAYER_DAMAGE", "Enable PvP", "Gameplay", false),
        ("ADMIN_PASSWORD", "Admin / RCON password", "Security", true),
        ("SERVER_PASSWORD", "Join password", "Security", true),
    ];

    /// <summary>
    /// The documented explanation for why the thijsvanloef Palworld image reports <c>unhealthy</c> while
    /// running normally (docs/architecture.md, "Readiness vs. Container Health"). Applied whenever a
    /// discovered server's health is <see cref="ServerHealthStatus.Unhealthy"/>; this milestone only
    /// supports the Palworld deployment, so the explanation does not need to vary per game yet — a later
    /// milestone should source this text from the game definition rather than hardcoding it here.
    /// </summary>
    internal const string PalworldUnhealthyExplanation =
        "The container's own HEALTHCHECK calls http://localhost:8212/v1/api/info without admin " +
        "credentials and receives 401 Unauthorized on every probe. The Palworld server itself is " +
        "healthy — /v1/api/players returns OK on the same polling cycle. Servyx derives readiness " +
        "from its own authenticated detectors, never from this signal.";

    private readonly IServerDiscovery _discovery;
    private readonly IMetricsSource _metricsSource;
    private readonly ILogStream _logStream;
    private readonly ITransport _transport;
    private readonly AdoptionCriteria _criteria;

    /// <summary>Creates a <see cref="ServerQueryService"/> operating against the given Domain abstractions.</summary>
    public ServerQueryService(
        IServerDiscovery discovery,
        IMetricsSource metricsSource,
        ILogStream logStream,
        ITransport transport,
        AdoptionCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(metricsSource);
        ArgumentNullException.ThrowIfNull(logStream);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(criteria);

        _discovery = discovery;
        _metricsSource = metricsSource;
        _logStream = logStream;
        _transport = transport;
        _criteria = criteria;
    }

    /// <inheritdoc />
    public async Task<DockerConnectionState> GetConnectionStateAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var health = await _transport.ProbeAsync(target, ct).ConfigureAwait(false);
            return new DockerConnectionState(health.Reachable, target.Endpoint, health.Detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ProbeAsync is contractually side-effect-free and expected to catch its own transport
            // errors (see DockerTransport.ProbeAsync), but a degraded result must never depend on every
            // ITransport implementation honoring that — this is the last line of defense.
            return new DockerConnectionState(false, target.Endpoint, $"Probe failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServerSummary>> GetAdoptedServersAsync(CancellationToken ct = default)
    {
        var servers = await TryDiscoverAsync(ct).ConfigureAwait(false);
        return servers.Select(ToSummary).ToList();
    }

    /// <inheritdoc />
    public async Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var servers = await TryDiscoverAsync(ct).ConfigureAwait(false);
        var match = servers.FirstOrDefault(s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            ?? servers.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : ToDetail(match);
    }

    /// <inheritdoc />
    public async Task<ResourceSample?> GetMetricsSampleAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ResourceSample? sample = null;

        try
        {
            await foreach (var s in _metricsSource.StreamAsync(serverId, cts.Token).ConfigureAwait(false))
            {
                sample = s;
                // A single stats reading is all a "sample" needs; cancel to release the underlying
                // streaming connection rather than leaving it open for a caller who only wanted one shot.
                await cts.CancelAsync().ConfigureAwait(false);
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Expected: this is our own cts.Cancel() unwinding the stream after the first sample.
        }
        catch (Exception)
        {
            return null;
        }

        return sample;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ConsoleLine> FollowLogsAsync(
        string serverId,
        int maxBacklogLines,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var enumerator = _logStream.FollowAsync(serverId, new ConsoleTailOptions(maxBacklogLines), ct).GetAsyncEnumerator(ct);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                var moved = false;
                var failed = false;

                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Consistent with every other method on this class: the caller's own cancellation
                    // is not a degraded/transport-failure condition, so it propagates rather than being
                    // swallowed into a quiet end-of-stream. `throw;` here is not a yield statement, so it
                    // does not run afoul of the "no yield inside a try with a catch" restriction that
                    // shaped the rest of this loop.
                    throw;
                }
                catch (Exception)
                {
                    failed = true;
                }

                if (failed || !moved)
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConsoleLine>> ReadRecentLogsAsync(string serverId, int maxLines, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        if (maxLines <= 0)
        {
            return [];
        }

        try
        {
            return await _logStream.ReadAsync(serverId, 0, maxLines, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<DiscoveredServer>> TryDiscoverAsync(CancellationToken ct)
    {
        try
        {
            return await _discovery.DiscoverAsync(_criteria.ImageRepository, _criteria.RequiredMountContainerPath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Daemon unreachable, permission denied, etc. — an honest empty list, not a crash.
            return [];
        }
    }

    private ServerSummary ToSummary(DiscoveredServer server) => new(
        Id: server.ServerId,
        Name: server.Name,
        Game: _criteria.GameName,
        State: MapState(server.State),
        Health: MapHealth(server.HealthStatus),
        HealthDetail: MapHealth(server.HealthStatus) == ServerHealthStatus.Unhealthy ? PalworldUnhealthyExplanation : null,
        StartedAt: server.StartedAt,
        Host: "docker",
        Ports: server.Ports.Select(p => new ServerPort(p.HostPort, p.ContainerPort, p.Protocol)).ToList());

    private ServerDetail ToDetail(DiscoveredServer server)
    {
        var requiredMount = server.Mounts.FirstOrDefault(
            m => string.Equals(m.Destination, _criteria.RequiredMountContainerPath, StringComparison.Ordinal));

        return new ServerDetail(
            Summary: ToSummary(server),
            Image: server.Image,
            MountHostPath: requiredMount?.Source,
            MountContainerPath: requiredMount?.Destination ?? _criteria.RequiredMountContainerPath,
            Network: server.NetworkName,
            IpAddress: server.ContainerIp,
            MemoryLimitBytes: server.MemoryLimitBytes,
            CpuLimit: server.CpuLimit,
            Settings: BuildSettings(server.EnvironmentVariables));
    }

    /// <summary>
    /// Reads only the allowlisted <see cref="KnownSettings"/> keys out of a container's raw environment
    /// and returns them as read-model rows. This is the one place a secret's real value is ever looked
    /// at: <see cref="ServerSettingValue.Authoritative"/> is set to the fixed mask for any
    /// <c>IsSecret</c> key present, never to <paramref name="environmentVariables"/>'s actual value —
    /// nothing downstream of this method ever sees the real secret.
    /// </summary>
    private static IReadOnlyList<ServerSettingValue> BuildSettings(IReadOnlyDictionary<string, string> environmentVariables)
    {
        var rows = new List<ServerSettingValue>(KnownSettings.Length);
        foreach (var (key, label, group, isSecret) in KnownSettings)
        {
            var present = environmentVariables.TryGetValue(key, out var value);
            var authoritative = !present ? null : isSecret ? "********" : value;
            rows.Add(new ServerSettingValue(key, label, group, isSecret, authoritative));
        }

        return rows;
    }

    private static ServerState MapState(string dockerState) => dockerState.ToLowerInvariant() switch
    {
        "running" => ServerState.Running,
        "restarting" => ServerState.Starting,
        "removing" => ServerState.Stopping,
        "paused" => ServerState.Unknown,
        "created" => ServerState.Stopped,
        "exited" => ServerState.Stopped,
        "dead" => ServerState.Crashed,
        _ => ServerState.Unknown,
    };

    private static ServerHealthStatus MapHealth(string dockerHealth) => dockerHealth.ToLowerInvariant() switch
    {
        "healthy" => ServerHealthStatus.Healthy,
        "unhealthy" => ServerHealthStatus.Unhealthy,
        _ => ServerHealthStatus.Unknown,
    };
}
