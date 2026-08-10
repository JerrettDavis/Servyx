using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
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
/// <remarks>
/// Operates in one of two modes, chosen by which constructor is used:
/// <list type="bullet">
/// <item>
/// <strong>Single-criteria mode</strong> (the original constructor): every discovered server is governed by
/// the one <see cref="AdoptionCriteria"/>/settings/lifecycle this instance was constructed with — exactly
/// today's behaviour, preserved byte-for-byte for the single-definition-loaded case the characterization
/// tests pin.
/// </item>
/// <item>
/// <strong>Multi-definition mode</strong> (the <see cref="DefinitionAdoptionCriteria"/>-set constructor):
/// each discovered server is independently resolved to its own governing definition via
/// <see cref="ServerBindingResolver"/>, optionally anchored across restarts by an
/// <see cref="IServerDefinitionBindingStore"/> — see <see cref="DiscoverMultiAsync"/>.
/// </item>
/// </list>
/// </remarks>
public sealed class ServerQueryService : IServerQueryService
{
    /// <summary>
    /// Game-neutral fallback shown whenever a discovered server's health is
    /// <see cref="ServerHealthStatus.Unhealthy"/> but no <see cref="HealthSignalDefinition"/> was resolved
    /// for it. Says nothing game-specific: unlike the old hardcoded Palworld explanation this replaces,
    /// this text must never be wrong for a server whose definition has not opted into overriding it.
    /// </summary>
    internal const string GenericUnhealthyExplanation =
        "The container's own health check is reporting unhealthy. This definition has not documented " +
        "whether that signal can be trusted, so Servyx is showing it as-is.";

    /// <summary>Shown as <see cref="ServerSummary.Game"/> for a server whose binding is <see cref="ServerBindingStatus.Ambiguous"/>.</summary>
    internal const string AmbiguousGameName = "Unknown (ambiguous binding)";

    /// <summary>Shown as <see cref="ServerSummary.Game"/> for a server whose binding is <see cref="ServerBindingStatus.NeedsRebind"/>.</summary>
    internal const string NeedsRebindGameName = "Unknown (needs re-binding)";

    /// <summary>
    /// The <see cref="Servyx.Domain.Definitions.Model.DeclaredConfigSurface.Id"/> <see cref="BuildSettings"/>
    /// treats as THE environment surface — the one whose <see cref="SettingBinding.ByKey"/> value actually
    /// matches what a running container reports as its environment (<see cref="DiscoveredServer.EnvironmentVariables"/>).
    /// </summary>
    /// <remarks>
    /// Selecting by this identity, rather than by taking a setting's first <see cref="SettingBinding.ByKey"/>
    /// binding regardless of which surface it addresses, is deliberate: the parser only accepts <c>key</c>
    /// addressing on a <c>dotenv</c>-format surface (<c>GameDefinitionYamlParser.ValidateBindingSurfaceFormat</c>),
    /// but nothing stops a future definition from declaring a <em>second</em> dotenv surface — format alone
    /// can never tell those two apart, since both would pass that same check. Identity is the one signal
    /// that still resolves correctly regardless of how many dotenv surfaces exist or what order their
    /// bindings are declared in. <c>"env"</c> is not an arbitrary literal: it is the surface id
    /// <c>definitions/palworld-docker.yaml</c> already declares for this purpose, and the same convention
    /// docs/schema.md's own <c>enabledWhen</c> example (<c>env.RCON_ENABLED == 'true'</c>) already assumes.
    /// A setting whose only <c>key</c> binding(s) address some other surface — including a hypothetical
    /// second dotenv surface — simply has no authoritative environment value, degrading to <c>null</c> like
    /// any other absent key, rather than reading from the wrong place.
    /// </remarks>
    private const string EnvironmentSurfaceId = "env";

    private readonly IServerDiscovery _discovery;
    private readonly IMetricsSource _metricsSource;
    private readonly ILogStream _logStream;
    private readonly ITransport _transport;
    private readonly ILogger<ServerQueryService> _logger;

    /// <summary>
    /// Reads each setting's real configuration surfaces, or <see langword="null"/> when no reader is wired.
    /// </summary>
    /// <remarks>
    /// Optional, and null in every construction that predates it, so <see cref="BuildSettings"/> keeps
    /// producing exactly the environment-only rows it always has. See
    /// <see cref="EnrichAsync"/> for what a non-null one adds and, importantly, what it does not replace.
    /// </remarks>
    private readonly ISettingStateResolverFactory? _settingStates;

    private readonly bool _multiMode;

    // ── Single-criteria mode fields — populated only by the original constructor. ──────────────────────
    private readonly AdoptionCriteria? _criteria;
    private readonly IReadOnlyList<SettingDescriptor> _settings = [];
    private readonly HealthSignalDefinition? _healthSignal;

    // ── Multi-definition mode fields — populated only by the DefinitionAdoptionCriteria-set constructor. ─
    private readonly IReadOnlyList<DefinitionAdoptionCriteria>? _criteriaSet;
    private readonly IBoundDefinitionLookup? _definitionLookup;
    private readonly IServerDefinitionBindingStore? _bindingStore;

    /// <summary>Creates a <see cref="ServerQueryService"/> operating against a single game's adoption criteria.</summary>
    /// <param name="settingGroups">
    /// The bundled game definition's parsed <c>settings</c> block, if a single definition loaded
    /// successfully at startup — see <c>Servyx.Web</c>'s definition bootstrap. Optional, mirroring
    /// <c>ServyxServerLifecycles</c>'s <c>LifecycleDefinition?</c> parameter, so this type can still be
    /// constructed via plain DI activation when no definition is available: <see cref="GetServerDetailAsync"/>
    /// then returns an empty <see cref="ServerDetail.Settings"/> list rather than throwing or falling back
    /// to a second hardcoded table.
    /// </param>
    /// <param name="lifecycle">
    /// The same already-typed <c>lifecycle</c> block <c>ServyxServerLifecycles</c> consumes — see
    /// <c>Servyx.Web</c>'s definition bootstrap, which registers it as a singleton only when a single
    /// definition loaded successfully, exactly like <paramref name="settingGroups"/>. This type reads only
    /// <see cref="LifecycleDefinition.HealthSignal"/> off it; every other block is <c>ServyxServerLifecycles</c>'s
    /// concern. Optional and null by the same "no single definition loaded" rule, so a discovered server's
    /// unhealthy explanation degrades to <see cref="GenericUnhealthyExplanation"/> rather than throwing or
    /// assuming a specific game.
    /// </param>
    /// <param name="settingStates">
    /// Reads each setting's real configuration surfaces — the <c>Desired</c>, <c>Rendered</c> and
    /// <c>Runtime</c> columns, and the drift between them. Optional and null by default, exactly like
    /// <paramref name="settingGroups"/> and <paramref name="lifecycle"/>: with no reader wired, or with one
    /// that cannot reach this server's surfaces, <see cref="ServerDetail.Settings"/> carries the same
    /// environment-sourced <c>Authoritative</c> column it always has and leaves the rest honestly null.
    /// </param>
    public ServerQueryService(
        IServerDiscovery discovery,
        IMetricsSource metricsSource,
        ILogStream logStream,
        ITransport transport,
        AdoptionCriteria criteria,
        ILogger<ServerQueryService> logger,
        IReadOnlyList<SettingGroup>? settingGroups = null,
        LifecycleDefinition? lifecycle = null,
        ISettingStateResolverFactory? settingStates = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(metricsSource);
        ArgumentNullException.ThrowIfNull(logStream);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _metricsSource = metricsSource;
        _logStream = logStream;
        _transport = transport;
        _criteria = criteria;
        _logger = logger;
        _settings = settingGroups is null
            ? []
            : settingGroups.SelectMany(g => g.Items).ToList();
        _healthSignal = lifecycle?.HealthSignal;
        _settingStates = settingStates;
        _multiMode = false;
    }

    /// <summary>
    /// Creates a <see cref="ServerQueryService"/> that independently resolves each discovered server to its
    /// own governing definition out of <paramref name="criteriaSet"/> — the per-server binding path used
    /// once more than one game definition is loaded. See <see cref="ServerBindingResolver"/> for the
    /// discovery fan-out and conflict-resolution rules, and <see cref="DiscoverMultiAsync"/> for how a
    /// resolved binding is anchored through <paramref name="bindingStore"/> across restarts.
    /// </summary>
    /// <param name="criteriaSet">One <see cref="DefinitionAdoptionCriteria"/> per loaded definition with a derivable docker profile.</param>
    /// <param name="definitionLookup">Resolves a bound definition's content hash to its settings/lifecycle/name.</param>
    /// <param name="bindingStore">
    /// Durable storage for resolved bindings, so a restart or image retag reuses the same pinned content
    /// hash rather than re-deriving a possibly different one. <see langword="null"/> is accepted (bindings
    /// are then re-resolved fresh on every call, with no cross-restart pin) so this constructor remains
    /// usable in tests and hosts that have not wired persistence.
    /// </param>
    /// <param name="settingStates">
    /// Reads each setting's real configuration surfaces. Optional and null by default — see the
    /// single-criteria constructor's parameter of the same name.
    /// </param>
    public ServerQueryService(
        IServerDiscovery discovery,
        IMetricsSource metricsSource,
        ILogStream logStream,
        ITransport transport,
        IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet,
        IBoundDefinitionLookup definitionLookup,
        ILogger<ServerQueryService> logger,
        IServerDefinitionBindingStore? bindingStore = null,
        ISettingStateResolverFactory? settingStates = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(metricsSource);
        ArgumentNullException.ThrowIfNull(logStream);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(criteriaSet);
        ArgumentNullException.ThrowIfNull(definitionLookup);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _metricsSource = metricsSource;
        _logStream = logStream;
        _transport = transport;
        _logger = logger;
        _criteriaSet = criteriaSet;
        _definitionLookup = definitionLookup;
        _bindingStore = bindingStore;
        _settingStates = settingStates;
        _multiMode = true;
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
        var attempt = await TryDiscoverAsync(ct).ConfigureAwait(false);
        return attempt.Servers.Select(ToSummary).ToList();
    }

    /// <inheritdoc />
    public async Task<ServerListResult> GetAdoptedServersWithStatusAsync(CancellationToken ct = default)
    {
        var attempt = await TryDiscoverAsync(ct).ConfigureAwait(false);
        return attempt.Failed
            ? ServerListResult.Failed(attempt.FailureDetail)
            : ServerListResult.Ok(attempt.Servers.Select(ToSummary).ToList());
    }

    /// <inheritdoc />
    public async Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        var attempt = await TryDiscoverAsync(ct).ConfigureAwait(false);
        var match = attempt.Servers.FirstOrDefault(s => string.Equals(s.Server.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            ?? attempt.Servers.FirstOrDefault(s => string.Equals(s.Server.Name, serverId, StringComparison.OrdinalIgnoreCase));

        return match is null ? null : await ToDetailAsync(match, ct).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Consistent with every other method on this class: the caller's own cancellation is not a
            // degraded/transport-failure condition, so it propagates rather than being logged as a
            // Warning and swallowed into a quiet null — see TryDiscoverAsync/ReadRecentLogsAsync.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sample metrics for server '{ServerId}'.", serverId);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read logs for server '{ServerId}'.", serverId);
            return [];
        }
    }

    /// <summary>
    /// Everything <see cref="ToSummary"/>/<see cref="ToDetail"/> need for one discovered server, already
    /// resolved to whichever definition (if any, if unambiguous) governs it. In single-criteria mode every
    /// context shares the same <see cref="GameName"/>/<see cref="Settings"/>/<see cref="HealthSignal"/> and
    /// <see cref="ServerBindingStatus.Bound"/> status; in multi-definition mode each is independently
    /// resolved — see <see cref="DiscoverMultiAsync"/>.
    /// </summary>
    private sealed record ServerContext(
        DiscoveredServer Server,
        string GameName,
        string RequiredMountContainerPath,
        IReadOnlyList<SettingDescriptor> Settings,
        HealthSignalDefinition? HealthSignal,
        ServerBindingStatus BindingStatus,
        IReadOnlyList<string> AmbiguousCandidateGameIds);

    /// <summary>
    /// A single discovery attempt: either the (possibly empty) servers discovery actually returned, or a
    /// record that discovery threw instead. <see cref="Failed"/> is the signal
    /// <see cref="GetAdoptedServersWithStatusAsync"/> forwards as <see cref="ServerListResult.DiscoveryFailed"/>
    /// — <see cref="GetAdoptedServersAsync"/> and <see cref="GetServerDetailAsync"/> intentionally discard
    /// it and treat <see cref="Servers"/> alone (empty on failure) as their whole answer, per this
    /// interface's degrade-honestly contract.
    /// </summary>
    private readonly record struct DiscoveryAttempt(IReadOnlyList<ServerContext> Servers, bool Failed, string? FailureDetail);

    private async Task<DiscoveryAttempt> TryDiscoverAsync(CancellationToken ct)
    {
        try
        {
            var servers = _multiMode
                ? await DiscoverMultiAsync(ct).ConfigureAwait(false)
                : await DiscoverSingleAsync(ct).ConfigureAwait(false);
            return new DiscoveryAttempt(servers, Failed: false, FailureDetail: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Daemon unreachable, permission denied, etc. — an honest empty list, not a crash. Logged here,
            // and also carried forward as DiscoveryAttempt.Failed so a caller that needs to tell "zero
            // servers" apart from "the query failed" (GetAdoptedServersWithStatusAsync, and ultimately the
            // dashboard UI) can still do so even though this method's own log line is server-side only.
            _logger.LogWarning(ex, "Failed to discover adopted servers.");
            return new DiscoveryAttempt([], Failed: true, FailureDetail: ex.Message);
        }
    }

    private async Task<IReadOnlyList<ServerContext>> DiscoverSingleAsync(CancellationToken ct)
    {
        var criteria = _criteria!;
        var servers = await _discovery.DiscoverAsync(criteria.ImageRepository, criteria.RequiredMountContainerPath, ct)
            .ConfigureAwait(false);

        return servers
            .Select(s => new ServerContext(
                s, criteria.GameName, criteria.RequiredMountContainerPath, _settings, _healthSignal, ServerBindingStatus.Bound, []))
            .ToList();
    }

    /// <summary>
    /// Fans discovery out across every loaded definition's criteria (<see cref="ServerBindingResolver"/>),
    /// then resolves each match against <see cref="_bindingStore"/> before falling back to the fresh match:
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>
    /// A server previously bound to a content hash that still resolves through <see cref="_definitionLookup"/>
    /// keeps using that exact content — regardless of what today's fresh match says — so a hot-reloaded or
    /// edited definition never silently changes an already-running server's behaviour mid-operation.
    /// </item>
    /// <item>
    /// A server previously bound to a content hash that no longer resolves is <see cref="ServerBindingStatus.NeedsRebind"/>
    /// — never silently re-pointed at whatever the id currently resolves to.
    /// </item>
    /// <item>
    /// A server with no persisted binding uses today's fresh match: an unambiguous match is persisted (so
    /// the next call — and the next restart — reuses it) and used; an ambiguous one is surfaced as
    /// <see cref="ServerBindingStatus.Ambiguous"/> and deliberately left unpersisted, so it is re-evaluated
    /// every call until the ambiguity is resolved (e.g. one of the tied definitions is edited or removed).
    /// </item>
    /// </list>
    /// </remarks>
    private async Task<IReadOnlyList<ServerContext>> DiscoverMultiAsync(CancellationToken ct)
    {
        var criteriaSet = _criteriaSet!;
        var lookup = _definitionLookup!;
        var matches = await ServerBindingResolver.ResolveAsync(_discovery, criteriaSet, _logger, ct).ConfigureAwait(false);

        var contexts = new List<ServerContext>(matches.Count);
        foreach (var match in matches)
        {
            var persisted = _bindingStore is null
                ? null
                : await _bindingStore.TryGetAsync(match.Server.ServerId, ct).ConfigureAwait(false);

            if (persisted is { State: ServerDefinitionBindingState.Bound, Definition: not null })
            {
                var pinnedData = lookup.TryGetByContentHash(persisted.Definition.ContentHash);
                contexts.Add(pinnedData is not null
                    ? BuildBoundContext(match.Server, persisted.Definition, pinnedData, criteriaSet)
                    : BuildNeedsRebindContext(match.Server, persisted.Definition.Id));
                continue;
            }

            if (match.State != ServerMatchState.Bound || match.Definition is null)
            {
                contexts.Add(BuildAmbiguousContext(match.Server, match.Candidates));
                continue;
            }

            var data = lookup.TryGetByContentHash(match.Definition.ContentHash);
            if (data is null)
            {
                // A definition matched by detect rule but whose content hash is not (or no longer)
                // resolvable — treated the same as a stale pin, never silently substituted.
                contexts.Add(BuildNeedsRebindContext(match.Server, match.Definition.Id));
                continue;
            }

            if (_bindingStore is not null)
            {
                await _bindingStore.SaveAsync(
                    new ServerDefinitionBinding(match.Server.ServerId, ServerDefinitionBindingState.Bound, match.Definition, [], DateTimeOffset.UtcNow),
                    ct).ConfigureAwait(false);
            }

            contexts.Add(BuildBoundContext(match.Server, match.Definition, data, criteriaSet));
        }

        return contexts;
    }

    private static ServerContext BuildBoundContext(
        DiscoveredServer server, GameDefinitionRef reference, BoundDefinitionData data, IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet)
    {
        var mount = criteriaSet.FirstOrDefault(c => c.DefinitionRef == reference)?.Criteria.RequiredMountContainerPath ?? string.Empty;
        return new ServerContext(
            server,
            data.GameName,
            mount,
            data.Settings.SelectMany(g => g.Items).ToList(),
            data.Lifecycle.HealthSignal,
            ServerBindingStatus.Bound,
            []);
    }

    private static ServerContext BuildAmbiguousContext(DiscoveredServer server, IReadOnlyList<GameDefinitionRef> candidates) => new(
        server, AmbiguousGameName, string.Empty, [], null, ServerBindingStatus.Ambiguous, candidates.Select(c => c.Id).ToList());

    private static ServerContext BuildNeedsRebindContext(DiscoveredServer server, string previousGameId) => new(
        server, NeedsRebindGameName, string.Empty, [], null, ServerBindingStatus.NeedsRebind, [previousGameId]);

    private ServerSummary ToSummary(ServerContext ctx)
    {
        var health = MapHealth(ctx.Server.HealthStatus);
        return new ServerSummary(
            Id: ctx.Server.ServerId,
            Name: ctx.Server.Name,
            Game: ctx.GameName,
            State: MapState(ctx.Server.State),
            Health: health,
            HealthDetail: health == ServerHealthStatus.Unhealthy
                ? (ctx.HealthSignal?.Explanation ?? GenericUnhealthyExplanation)
                : null,
            StartedAt: ctx.Server.StartedAt,
            // The transport this query service was composed with — "docker" for a local daemon,
            // "ssh+docker" for a remote host observed over SSH — rather than a literal that was only ever
            // true for the first of those two.
            Host: _transport.TransportId,
            Ports: ctx.Server.Ports.Select(p => new ServerPort(p.HostPort, p.ContainerPort, p.Protocol)).ToList(),
            BindingStatus: ctx.BindingStatus,
            AmbiguousCandidateGameIds: ctx.BindingStatus == ServerBindingStatus.Bound ? null : ctx.AmbiguousCandidateGameIds);
    }

    private async Task<ServerDetail> ToDetailAsync(ServerContext ctx, CancellationToken ct)
    {
        var requiredMount = ctx.Server.Mounts.FirstOrDefault(
            m => string.Equals(m.Destination, ctx.RequiredMountContainerPath, StringComparison.Ordinal));

        return new ServerDetail(
            Summary: ToSummary(ctx),
            Image: ctx.Server.Image,
            MountHostPath: requiredMount?.Source,
            MountContainerPath: requiredMount?.Destination ?? ctx.RequiredMountContainerPath,
            Network: ctx.Server.NetworkName,
            IpAddress: ctx.Server.ContainerIp,
            MemoryLimitBytes: ctx.Server.MemoryLimitBytes,
            CpuLimit: ctx.Server.CpuLimit,
            Settings: await EnrichAsync(
                ctx,
                BuildSettings(ctx.Settings, ctx.Server.EnvironmentVariables),
                ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Fills in the <c>Desired</c>, <c>Rendered</c>, <c>Runtime</c> and drift columns of
    /// <paramref name="rows"/> from the server's real configuration surfaces, leaving them exactly as
    /// <see cref="BuildSettings"/> produced them when there is no reader, no state for a row, or any
    /// failure at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>Authoritative</c> is not replaced, only backfilled.</strong>
    /// <see cref="BuildSettings"/> sources it from the running container's own environment; the resolver
    /// sources it from the authoritative configuration surface on disk (for a compose deployment, the
    /// <c>.env</c> file). Those are two different facts — what the workload is running with now, versus what
    /// it would start with next time — and their disagreement is precisely the drift the four-column model
    /// exists to expose. Preferring the file would silently discard the running value and make that drift
    /// invisible, so the resolver's value is used only where the environment had none (a setting with no
    /// <c>env</c>-surface binding at all).
    /// </para>
    /// <para>
    /// <strong>Every failure degrades to today's rows.</strong> Reading surfaces opens sessions and touches
    /// files on a target that may be unreachable, and a settings page must not fail to render because a
    /// container is stopped. A throwing resolver is logged once and the environment-only rows are returned
    /// unchanged — the same degrade-honestly contract every other method on this class follows.
    /// </para>
    /// <para>
    /// <strong>Masking is preserved end to end.</strong> Both sources mask a secret with the same fixed
    /// <c>"********"</c>, and neither the real value nor any part of it is logged here.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ServerSettingValue>> EnrichAsync(
        ServerContext ctx,
        IReadOnlyList<ServerSettingValue> rows,
        CancellationToken ct)
    {
        if (_settingStates is null || rows.Count == 0)
        {
            return rows;
        }

        try
        {
            var resolver = await _settingStates
                .CreateAsync(new SettingStateScope(ctx.Server.ServerId, ctx.Settings), ct)
                .ConfigureAwait(false);

            var enriched = new List<ServerSettingValue>(rows.Count);
            foreach (var row in rows)
            {
                var state = await resolver.ResolveAsync(row.Key, ct).ConfigureAwait(false);
                enriched.Add(row with
                {
                    Authoritative = row.Authoritative ?? state.Authoritative,
                    Desired = state.Desired,
                    Rendered = state.Rendered,
                    Runtime = state.Runtime,
                    Drift = state.Drift,
                    PendingRegeneration = state.PendingRegeneration,
                });
            }

            return enriched;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to read configuration surfaces for server '{ServerId}'; showing environment-sourced "
                + "settings only.",
                ctx.Server.ServerId);

            return rows;
        }
    }

    /// <summary>
    /// Reads each of <paramref name="settings"/> out of a container's raw environment and returns them as
    /// read-model rows, in the same order they were supplied (document order — see
    /// <see cref="ServerQueryService"/>'s <c>_settings</c>/<c>ServerContext.Settings</c> remarks). This is
    /// the one place a secret's real value is ever looked at: <see cref="ServerSettingValue.Authoritative"/>
    /// is set to the fixed mask for any <see cref="SettingDescriptor.IsSecret"/> setting present, never to
    /// <paramref name="environmentVariables"/>'s actual value — nothing downstream of this method ever
    /// sees the real secret.
    /// </summary>
    /// <remarks>
    /// A setting's own <see cref="SettingDescriptor.Key"/> (the definition-schema key, e.g.
    /// <c>admin-password</c>) identifies the row, but is never the key looked up in
    /// <paramref name="environmentVariables"/> — that lookup instead uses the setting's env-surface binding
    /// key (e.g. <c>ADMIN_PASSWORD</c>), taken from the <see cref="SettingBinding.ByKey"/> binding that
    /// addresses <see cref="EnvironmentSurfaceId"/> specifically — see that constant's remarks for why
    /// identity, not list position, is what selects it. A setting with no such binding — declared with only
    /// <c>member</c>/<c>pointer</c> bindings, or with <c>key</c> bindings that all address some other
    /// surface — is treated as absent from the environment, the same as a key that is present in the
    /// binding but missing from <paramref name="environmentVariables"/>.
    /// </remarks>
    private static IReadOnlyList<ServerSettingValue> BuildSettings(
        IReadOnlyList<SettingDescriptor> settings, IReadOnlyDictionary<string, string> environmentVariables)
    {
        var rows = new List<ServerSettingValue>(settings.Count);
        foreach (var setting in settings)
        {
            var envKey = setting.Bindings
                .OfType<SettingBinding.ByKey>()
                .FirstOrDefault(b => b.SurfaceId == EnvironmentSurfaceId)
                ?.Key;
            string? value = null;
            var present = envKey is not null && environmentVariables.TryGetValue(envKey, out value);
            var authoritative = !present ? null : setting.IsSecret ? "********" : value;
            rows.Add(new ServerSettingValue(setting.Key, setting.Label, setting.Group, setting.IsSecret, authoritative));
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
