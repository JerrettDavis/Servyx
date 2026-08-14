using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Persistence;

namespace Servyx.Composition;

/// <summary>
/// The <see cref="BackgroundService"/> that periodically refreshes every adopted server's status and a
/// resource sample, off the request path entirely, and publishes the result into <see cref="ServerStatusCache"/>
/// (and durably, into <c>ServerStatusSnapshot</c> rows) — the one writer <c>ServerStatusCache</c>'s own
/// remarks describe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Depends on <see cref="IServerDiscovery"/>, NOT <see cref="IServerQueryService"/>.</strong>
/// <c>ServerQueryService</c> optionally consumes <c>ISettingStateResolverFactory</c>, which consumes
/// <c>ServyxSurfaceResolutionContextSource</c>, and all three are process-lifetime singletons — reaching back
/// into <c>IServerQueryService</c> from here would risk the same reentrancy hazard
/// <c>ServyxCoreCompositionExtensions</c> documents at its own <c>ServyxSurfaceResolutionContextSource</c>
/// registration. This service instead resolves servers the same way <c>ServerQueryService</c> does
/// internally — a direct <see cref="IServerDiscovery.DiscoverAsync"/> call in single-definition mode, or
/// <see cref="ServerBindingResolver.ResolveAsync"/> (the same static, dependency-free algorithm
/// <c>ServerQueryService</c> uses) in multi-definition mode — without ever touching
/// <see cref="IServerQueryService"/> itself.
/// </para>
/// <para>
/// <strong>Shape copied from <see cref="ChangePlanRetentionService"/>.</strong> <see cref="RunOnceAsync"/> is
/// awaited once immediately at startup, then on every <see cref="PeriodicTimer"/> tick — unlike
/// <see cref="ScheduledBackupService"/>'s deliberate one-interval startup delay, a page load right after
/// process start should not have to wait a full interval for the cache to have anything real in it (priming
/// from the database, in <see cref="ServerStatusCache.Prime"/>, covers the gap between restart and this first
/// tick completing).
/// </para>
/// <para>
/// <strong>Per-server non-overlap gating, copied from <see cref="ScheduledBackupService"/>.</strong> Each
/// discovered server has its own single-permit <see cref="SemaphoreSlim"/>, taken with a zero timeout. Ticks
/// run strictly sequentially already (this service's own <see cref="ExecuteAsync"/> loop awaits one
/// <see cref="RunOnceAsync"/> before starting the next), so the gate's real purpose is defensive: it protects
/// a server whose metrics probe is unusually slow from ever being refreshed twice concurrently, including by
/// a future caller that invokes <see cref="RunOnceAsync"/> out of band (a manual "refresh now" action, or a
/// test). A server whose gate is contended keeps its previous cache entry for that tick rather than being
/// dropped.
/// </para>
/// <para>
/// <strong>A refresh failure never empties the cache.</strong> Every level — one server's metrics sample, one
/// server's whole refresh, the discovery call itself, the database upsert — is caught and logged; a failure
/// leaves the cache holding its last-known-good entries (now aging toward <c>IsStale</c>) rather than
/// clearing them. <c>ExecuteAsync</c>'s own outer catch keeps the service (and therefore future ticks) alive
/// after an unexpected failure, exactly like <see cref="ChangePlanRetentionService"/>.
/// </para>
/// </remarks>
public sealed class ServerStatusRefreshService : BackgroundService
{
    private readonly ServerStatusRefreshOptions _options;
    private readonly IServerDiscovery _discovery;
    private readonly IMetricsSource _metrics;
    private readonly ServerStatusCache _cache;
    private readonly IDbContextFactory<ServyxDbContext> _contexts;
    private readonly ILogger<ServerStatusRefreshService> _logger;
    private readonly AdoptionCriteria? _singleCriteria;
    private readonly IReadOnlyList<DefinitionAdoptionCriteria>? _criteriaSet;
    private readonly TargetDescriptor? _target;
    private readonly TimeProvider _timeProvider;
    private readonly ServyxRconChannels? _rconChannels;
    private readonly string? _maxPlayersEnvKey;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the refresh worker.</summary>
    /// <param name="options">How often to refresh.</param>
    /// <param name="discovery">Lists candidate workloads matching a game's adoption criteria.</param>
    /// <param name="metricsSource">Samples per-server resource usage.</param>
    /// <param name="cache">The in-memory cache this worker is the sole writer of.</param>
    /// <param name="contexts">Opens short-lived contexts to durably upsert each tick's snapshots.</param>
    /// <param name="logger">Where tick results and failures are reported.</param>
    /// <param name="singleCriteria">
    /// The single loaded definition's adoption criteria, when exactly one definition loaded — see
    /// <c>ServyxCoreCompositionExtensions</c>'s <c>useSingleCriteriaMode</c>. Mutually exclusive with
    /// <paramref name="criteriaSet"/>; both null means no game definition loaded, so this worker discovers
    /// nothing every tick, honestly.
    /// </param>
    /// <param name="criteriaSet">
    /// One <see cref="DefinitionAdoptionCriteria"/> per loaded definition with a derivable docker profile,
    /// when zero or more than one definition loaded. Mutually exclusive with <paramref name="singleCriteria"/>.
    /// </param>
    /// <param name="target">
    /// The composing transport's default target, used only to label a server discovery reports no
    /// <see cref="DiscoveredServer.HostKey"/> for (a genuinely local server) with the right transport id
    /// rather than a hardcoded "docker" guess. Optional; falls back to "docker" when not supplied.
    /// </param>
    /// <param name="timeProvider">Clock and timer source. Substituted in tests; defaults to the system clock.</param>
    /// <param name="rconChannels">
    /// Resolves the write-guarded <see cref="Servyx.Domain.Rcon.IRconSession"/> a server's player count is
    /// read over — the same composition-root RCON plumbing <c>ControlChannelTools</c> and
    /// <c>ServyxBackupContextSource</c> already use. Its <see cref="Servyx.Domain.Rcon.IRconSession.GetPlayersAsync"/>
    /// already resolves the definition's own <c>control.players</c> plan (see <c>PlayerListPlan.Resolve</c>),
    /// so a server/definition that declares no player-list source over RCON is naturally skipped — nothing
    /// here re-derives that gate. <see langword="null"/> (its default, so every pre-existing construction
    /// site — every characterization/test in this solution — keeps compiling) means no channel is ever
    /// resolved and every server's player count stays unread. REST and query (A2S) sources named in a
    /// definition's <c>control.players.preferred</c> list are not read by this worker: no client for either
    /// protocol exists in this codebase yet, so only the RCON entry of a definition's preferred order is
    /// honoured today.
    /// </param>
    /// <param name="gameSettings">
    /// The single loaded definition's settings catalogue (<c>useSingleCriteriaMode</c>'s own
    /// <c>singleDefinition?.Settings</c>), used only to find a "max players"-shaped setting's writable
    /// environment-variable key — see <see cref="FindMaxPlayersEnvKey"/>. <see langword="null"/> (its
    /// default) means <see cref="ServerSummary.PlayersMax"/> is never populated from server configuration.
    /// </param>
    public ServerStatusRefreshService(
        ServerStatusRefreshOptions options,
        IServerDiscovery discovery,
        IMetricsSource metricsSource,
        ServerStatusCache cache,
        IDbContextFactory<ServyxDbContext> contexts,
        ILogger<ServerStatusRefreshService> logger,
        AdoptionCriteria? singleCriteria = null,
        IReadOnlyList<DefinitionAdoptionCriteria>? criteriaSet = null,
        TargetDescriptor? target = null,
        TimeProvider? timeProvider = null,
        ServyxRconChannels? rconChannels = null,
        IReadOnlyList<SettingGroup>? gameSettings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(metricsSource);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _discovery = discovery;
        _metrics = metricsSource;
        _cache = cache;
        _contexts = contexts;
        _logger = logger;
        _singleCriteria = singleCriteria;
        _criteriaSet = criteriaSet;
        _target = target;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rconChannels = rconChannels;
        _maxPlayersEnvKey = FindMaxPlayersEnvKey(gameSettings);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Server status refresh running every {Interval}.", _options.RefreshInterval);

        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.RefreshInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs one refresh tick: discovers every adopted server, refreshes each one's status+metrics
    /// (skipping any whose per-server gate is still held), publishes the result into
    /// <see cref="ServerStatusCache"/>, and durably upserts it. Exposed so a test can drive one tick without a
    /// host and without waiting on wall-clock time.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            await RefreshCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The outermost net, mirroring ChangePlanRetentionService.RunOnceAsync: the cache keeps whatever
            // it already had, aging toward IsStale rather than being cleared, and the next tick retries.
            _logger.LogError(ex, "A server status refresh tick failed. The cache keeps its last known values; the next tick will retry.");
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var discovered = await DiscoverAsync(ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var fresh = new Dictionary<string, ServerStatusEntry>(discovered.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in discovered)
        {
            ct.ThrowIfCancellationRequested();

            var (id, entry) = await RefreshServerAsync(ctx, now, ct).ConfigureAwait(false);
            if (entry is not null)
            {
                fresh[id] = entry;
            }
        }

        _cache.ReplaceAll(fresh);
        await UpsertDatabaseAsync(fresh, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes one server, gated so a refresh already in flight for the same id is never overlapped — see
    /// this type's own remarks. Returns the server's previous cache entry (which may be <see langword="null"/>)
    /// when the gate is contended or the refresh itself fails, so a transient failure never evicts a server
    /// from the published set.
    /// </summary>
    private async Task<(string Id, ServerStatusEntry? Entry)> RefreshServerAsync(
        DiscoveredContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var id = ctx.Server.ServerId;
        var gate = _gates.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));

        if (!gate.Wait(0, CancellationToken.None))
        {
            _logger.LogDebug("Skipping status refresh for server '{ServerId}': the previous refresh has not finished.", id);
            return (id, _cache.Get(id));
        }

        try
        {
            var sample = await TrySampleAsync(id, ct).ConfigureAwait(false);
            var players = await TryGetPlayersAsync(ctx, ct).ConfigureAwait(false);
            var summary = ToSummary(ctx) with { PlayersOnline = players.Online, PlayersMax = players.Max };
            return (id, new ServerStatusEntry(summary, sample, now));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh status for server '{ServerId}'; its cached entry (if any) is kept.", id);
            return (id, _cache.Get(id));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One discovered server, already resolved to its governing game's display name and binding status.</summary>
    private sealed record DiscoveredContext(
        DiscoveredServer Server, string GameName, ServerBindingStatus BindingStatus, IReadOnlyList<string> AmbiguousCandidateGameIds);

    private async Task<IReadOnlyList<DiscoveredContext>> DiscoverAsync(CancellationToken ct)
    {
        if (_singleCriteria is not null)
        {
            try
            {
                var servers = await _discovery
                    .DiscoverAsync(_singleCriteria.ImageRepository, _singleCriteria.RequiredMountContainerPath, ct)
                    .ConfigureAwait(false);

                return servers
                    .Select(s => new DiscoveredContext(s, _singleCriteria.GameName, ServerBindingStatus.Bound, []))
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server status refresh: discovery failed for the single loaded game definition.");
                return [];
            }
        }

        if (_criteriaSet is { Count: > 0 })
        {
            try
            {
                var matches = await ServerBindingResolver.ResolveAsync(_discovery, _criteriaSet, _logger, ct).ConfigureAwait(false);

                return matches
                    .Select(m => m.State == ServerMatchState.Bound && m.Definition is not null
                        ? new DiscoveredContext(m.Server, m.Definition.Id, ServerBindingStatus.Bound, [])
                        : new DiscoveredContext(m.Server, "Unknown (ambiguous binding)", ServerBindingStatus.Ambiguous, m.Candidates.Select(c => c.Id).ToList()))
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Server status refresh: multi-definition discovery failed.");
                return [];
            }
        }

        // No game definition loaded at all — the same honest "adoption matches nothing" state
        // ServerQueryService reports in this case (DefinitionCatalogMode.None).
        return [];
    }

    private async Task<ResourceSample?> TrySampleAsync(string serverId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await foreach (var sample in _metrics.StreamAsync(serverId, cts.Token).ConfigureAwait(false))
            {
                // A single stats reading is all a "sample" needs — same pattern as ServerQueryService
                // .GetMetricsSampleAsync — cancel to release the underlying streaming connection rather than
                // leaving it open for a background tick that only wanted one shot.
                await cts.CancelAsync().ConfigureAwait(false);
                return sample;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Expected: this is our own cts.Cancel() unwinding the stream after the first sample.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to sample metrics for server '{ServerId}' during status refresh.", serverId);
            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads a server's current connected-player count and configured capacity, never throwing: a control
    /// channel that is unconfigured, unreachable, or whose reply cannot be parsed degrades to
    /// <see langword="null"/> for whichever half of the pair it affects, exactly like <see cref="TrySampleAsync"/>
    /// does for a metrics sample — a player-count read failing must never fail (or skip) the rest of this
    /// server's status refresh.
    /// </summary>
    /// <remarks>
    /// <see cref="ServerSummary.PlayersMax"/> is deliberately never sourced from the RCON reply here: for the
    /// shipped Palworld definition the <c>rcon.players</c> parser is a bare CSV roster with no capacity
    /// figure, so relying on the RCON reply would silently leave capacity unpopulated for exactly the
    /// definition this fix targets. <see cref="TryReadConfiguredMaxPlayers"/> reads it instead from the
    /// server's own already-fetched environment variables — see <see cref="FindMaxPlayersEnvKey"/> — falling
    /// back to whatever (if anything) the player-list reply itself carried only when no configured value was
    /// found.
    /// </remarks>
    private async Task<(int? Online, int? Max)> TryGetPlayersAsync(DiscoveredContext ctx, CancellationToken ct)
    {
        var configuredMax = TryReadConfiguredMaxPlayers(ctx.Server.EnvironmentVariables);

        if (_rconChannels is null)
        {
            return (null, configuredMax);
        }

        try
        {
            var session = await _rconChannels
                .GetSessionAsync(ctx.Server.ServerId, ctx.Server.Name, ct)
                .ConfigureAwait(false);

            if (session is null)
            {
                // No RCON channel configured/derivable for this server at all — distinct from a channel that
                // resolved but declared no player-list source, which GetPlayersAsync itself reports as
                // PlayerListFidelity.Unknown below rather than throwing.
                return (null, configuredMax);
            }

            var snapshot = await session.GetPlayersAsync(ct).ConfigureAwait(false);
            var online = snapshot.List.Fidelity == PlayerListFidelity.Unknown ? null : snapshot.List.Count;
            return (online, configuredMax ?? snapshot.List.Max);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read player count for server '{ServerId}' during status refresh.", ctx.Server.ServerId);
            return (null, configuredMax);
        }
    }

    /// <summary>
    /// Reads the server's configured player capacity straight off its already-fetched environment variables
    /// (<see cref="Servyx.Domain.Discovery.DiscoveredServer.EnvironmentVariables"/> — no extra round trip),
    /// using the env key <see cref="_maxPlayersEnvKey"/> resolved once at construction. Degrades to
    /// <see langword="null"/> when no such key was resolved, the variable is absent, or it does not parse as
    /// a positive integer — never a fabricated value.
    /// </summary>
    private int? TryReadConfiguredMaxPlayers(IReadOnlyDictionary<string, string> environmentVariables)
    {
        if (_maxPlayersEnvKey is null)
        {
            return null;
        }

        return environmentVariables.TryGetValue(_maxPlayersEnvKey, out var raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0
            ? value
            : null;
    }

    /// <summary>
    /// Finds the environment-variable key backing the loaded definition's "max players" setting, if it
    /// declares one — e.g. <c>palworld-docker.yaml</c>'s <c>settings[group=Gameplay].items[key=PLAYERS]</c>,
    /// labelled "Max players" and write-bound to the <c>env</c> surface's <c>PLAYERS</c> key.
    /// </summary>
    /// <remarks>
    /// A label-text heuristic, not a dedicated schema field: the definition model
    /// (<see cref="Servyx.Domain.Definitions.Model.SettingDescriptor"/>) has no semantic tag marking a
    /// setting as "the player capacity", only a human-readable <see cref="SettingDescriptor.Label"/> — adding
    /// one is a bigger, deliberate schema change this fix does not make. The heuristic matches any
    /// <see cref="SettingType.Int"/> setting whose label mentions both "max" and "player" (ordinal-insensitive),
    /// with a write-bound key on the <c>env</c> surface — true for every shipped definition today, and cheap
    /// enough (a handful of string comparisons, once per process/definition-reload rather than per tick) to
    /// be worth doing generically rather than hardcoding the literal key <c>"PLAYERS"</c> for Palworld alone.
    /// Returns <see langword="null"/> when no such setting is found, e.g. <paramref name="settings"/> is null
    /// (no single definition loaded) or the loaded definition models capacity differently.
    /// </remarks>
    private static string? FindMaxPlayersEnvKey(IReadOnlyList<SettingGroup>? settings)
    {
        if (settings is null)
        {
            return null;
        }

        foreach (var group in settings)
        {
            foreach (var item in group.Items)
            {
                if (item.Type != SettingType.Int
                    || !item.Label.Contains("max", StringComparison.OrdinalIgnoreCase)
                    || !item.Label.Contains("player", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.WritableSurface?.Binding is SettingBinding.ByKey { SurfaceId: "env" } byKey)
                {
                    return byKey.Key;
                }
            }
        }

        return null;
    }

    private ServerSummary ToSummary(DiscoveredContext ctx)
    {
        var health = MapHealth(ctx.Server.HealthStatus);
        return new ServerSummary(
            Id: ctx.Server.ServerId,
            Name: ctx.Server.Name,
            Game: ctx.GameName,
            State: MapState(ctx.Server.State),
            Health: health,
            HealthDetail: health == ServerHealthStatus.Unhealthy ? ServerStatusMapping.GenericUnhealthyExplanation : null,
            StartedAt: ctx.Server.StartedAt,
            Host: ctx.Server.HostKey ?? _target?.TransportId ?? "docker",
            Ports: ctx.Server.Ports.Select(p => new ServerPort(p.HostPort, p.ContainerPort, p.Protocol)).ToList(),
            BindingStatus: ctx.BindingStatus,
            AmbiguousCandidateGameIds: ctx.BindingStatus == ServerBindingStatus.Bound ? null : ctx.AmbiguousCandidateGameIds,
            HostKey: ctx.Server.HostKey);
    }

    private async Task UpsertDatabaseAsync(IReadOnlyDictionary<string, ServerStatusEntry> fresh, CancellationToken ct)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        try
        {
            await using var context = await _contexts.CreateDbContextAsync(ct).ConfigureAwait(false);

            var ids = fresh.Keys.ToList();
            var existing = await context.ServerStatusSnapshots
                .Where(row => ids.Contains(row.ContainerId))
                .ToDictionaryAsync(row => row.ContainerId, StringComparer.OrdinalIgnoreCase, ct)
                .ConfigureAwait(false);

            foreach (var (id, entry) in fresh)
            {
                if (existing.TryGetValue(id, out var row))
                {
                    ServerStatusMapping.ApplyTo(row, entry);
                }
                else
                {
                    context.ServerStatusSnapshots.Add(ServerStatusMapping.ToNewRecord(id, entry));
                }
            }

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist {Count} server status snapshot(s) to the database; the in-memory cache is "
                + "still up to date and will be retried next tick.",
                fresh.Count);
        }
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
