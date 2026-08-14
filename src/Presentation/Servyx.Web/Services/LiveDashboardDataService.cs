using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servyx.Application.Backups;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Definitions;
using Servyx.Domain.Backups;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>
/// <see cref="IDashboardDataService"/> implementation backed by <see cref="IServerQueryService"/> —
/// i.e. real data, read from whatever Docker daemon is reachable. Every method is defensive: a failure
/// anywhere in the query pipeline is caught and logged, and degrades to an honest empty/"unknown" result
/// rather than propagating into a Blazor error boundary. <see cref="IServerQueryService"/> itself already
/// guarantees this for its own operations (daemon-unreachable, container-not-found, etc.); the try/catch
/// blocks here are the last line of defense, not the primary mechanism.
/// </summary>
public sealed class LiveDashboardDataService : IDashboardDataService
{
    private readonly IServerQueryService _query;
    private readonly ILogger<LiveDashboardDataService> _logger;
    private readonly GameDefinitionCatalog? _catalog;
    private readonly TargetDescriptor _target;
    private readonly IBackupDashboard? _backupDashboard;
    private readonly ITransport? _transport;
    private readonly IServerExecutionTargetResolver? _executionTargetResolver;
    private readonly ServerStatusCache? _statusCache;
    private readonly TimeSpan _staleThreshold;

    /// <summary>Creates a <see cref="LiveDashboardDataService"/>.</summary>
    /// <param name="target">
    /// The target this service probes for connection status — the local Docker daemon, or a remote host
    /// reached over ssh+docker, depending on which composition-root wiring extension registered it. Always
    /// injected rather than built here, so this service does not need to know which transport is in play.
    /// </param>
    /// <param name="catalog">
    /// The data-driven game-definition catalog — the composition root's sole source for both
    /// <see cref="GetGamesAsync"/> (every loaded definition, as a card each) and
    /// <see cref="GetGameDefinitionFaultsAsync"/> (every definition that failed to load). A
    /// <see langword="null"/> catalog degrades both to an honest empty list rather than fabricating data.
    /// </param>
    /// <param name="backupDashboard">
    /// The backup surface, if one is registered. <see langword="null"/> whenever the provisioning gate is
    /// closed (the default) or open with no backup provider wired up — the same "optional collaborator,
    /// resolved via DI's default-value fallback" pattern <paramref name="catalog"/> already uses. Backup
    /// methods on this service treat a <see langword="null"/> dashboard as the legitimate "backups are not
    /// configured" state, distinct from both an empty listing and a listing failure — see
    /// <see cref="GetAllBackupsWithStatusAsync"/>.
    /// </param>
    /// <param name="transport">
    /// The (write-guarded) execution-target transport <see cref="GetServerSavesWithStatusAsync"/> opens a
    /// short-lived, read-only session through to inspect a server's save world — the same transport
    /// <c>ServyxBackupContextSource</c> uses for backups, resolved here independently because saves needs
    /// no session caching or quiesce step of its own. <see langword="null"/> (its default, so every
    /// pre-existing construction site keeps compiling) is treated as "nothing in this process can reach a
    /// filesystem to read saves" — <see cref="SavesAvailability.NotConfigured"/>, never a fabricated empty
    /// read.
    /// </param>
    /// <param name="executionTargetResolver">
    /// Resolves the already-connected execution target for a server adopted on a registered/configured
    /// ssh+docker host, so <see cref="GetServerSavesWithStatusAsync"/> can read its saves through the derived
    /// host-side mount instead of <paramref name="transport"/> — see <c>ServerSavesReader</c>'s remarks.
    /// <see langword="null"/> when this process never registered one (no ssh+docker host support at all), in
    /// which case an adopted server's saves report <c>UnsupportedTransport</c>.
    /// </param>
    /// <param name="statusCache">
    /// The background-refreshed cache <see cref="GetDashboardSummaryAsync"/> and
    /// <see cref="GetServersWithStatusAsync"/> now read from instead of issuing a live discovery/metrics call
    /// on every page load — see <c>ServerStatusCache</c>'s own remarks. <see langword="null"/> (its default,
    /// so every pre-existing construction site — every characterization/degraded-path/backups test in this
    /// solution — keeps compiling and keeps exercising the original live path unchanged) falls back to the
    /// live behaviour those two methods have always had, driven by <paramref name="query"/> directly.
    /// </param>
    /// <param name="refreshOptions">
    /// How often the background worker behind <paramref name="statusCache"/> refreshes, used only to compute
    /// <c>IsStale</c> (a cached read older than twice this interval is considered stale). Optional; falls back
    /// to <c>ServerStatusRefreshOptions.DefaultRefreshInterval</c> when not supplied.
    /// </param>
    public LiveDashboardDataService(
        IServerQueryService query,
        ILogger<LiveDashboardDataService> logger,
        TargetDescriptor target,
        IBackupDashboard? backupDashboard = null,
        GameDefinitionCatalog? catalog = null,
        ITransport? transport = null,
        IServerExecutionTargetResolver? executionTargetResolver = null,
        ServerStatusCache? statusCache = null,
        ServerStatusRefreshOptions? refreshOptions = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(target);

        _query = query;
        _logger = logger;
        _target = target;
        _backupDashboard = backupDashboard;
        _catalog = catalog;
        _transport = transport;
        _executionTargetResolver = executionTargetResolver;
        _statusCache = statusCache;
        _staleThreshold = TimeSpan.FromTicks((refreshOptions?.RefreshInterval ?? ServerStatusRefreshOptions.DefaultRefreshInterval).Ticks * 2);
    }

    /// <inheritdoc />
    public async Task<ConnectionStatus> GetDockerConnectionStatusAsync(CancellationToken ct = default)
        => (await GetDockerConnectionInfoAsync(ct).ConfigureAwait(false)).Status;

    /// <inheritdoc />
    public async Task<DockerConnectionInfo> GetDockerConnectionInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await _query.GetConnectionStateAsync(_target, ct).ConfigureAwait(false);
            var status = state.Reachable ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
            return new DockerConnectionInfo(status, _target.TransportId, state.Detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection probe at '{Endpoint}' failed unexpectedly.", _target.Endpoint);
            return new DockerConnectionInfo(ConnectionStatus.Disconnected, _target.TransportId, $"Probe failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads from <see cref="ServerStatusCache"/> when one was supplied to this instance — see the
    /// constructor's remarks on <c>statusCache</c> — instead of issuing a live discovery/metrics call, so a
    /// page load never blocks on however long the transport probe takes. Falls back to the original live path
    /// (below) when no cache was supplied, which is every pre-existing construction site in this solution.
    /// </remarks>
    public async Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        if (_statusCache is not null)
        {
            return await BuildDashboardSummaryFromCacheAsync(ct).ConfigureAwait(false);
        }

        var servers = await GetServersAsync(ct).ConfigureAwait(false);

        var cpuPoints = new List<SparklinePoint>();
        var memPoints = new List<SparklinePoint>();
        if (servers.Count > 0)
        {
            var sample = await TryGetMetricsSampleAsync(servers[0].Id, ct).ConfigureAwait(false);
            if (sample is not null)
            {
                // Only one point-in-time sample is available in this milestone — a full history requires
                // continuously polling IMetricsSource.StreamAsync over time, which is a background
                // collection concern, not something a single page load can honestly produce. Sparkline
                // shows "No data yet" for a one-point series rather than a fabricated trend.
                cpuPoints.Add(new SparklinePoint(sample.Timestamp, Math.Round(sample.CpuPercent, 1)));
                memPoints.Add(new SparklinePoint(sample.Timestamp, Math.Round(sample.MemoryBytes / (1024d * 1024), 1)));
            }
        }

        // DashboardSummary.ForeignBackupsCount is a plain int, not the three-way BackupsAvailability this
        // service reports elsewhere — so whatever GetAllBackupsWithStatusAsync could count (even a partial
        // count from a listing that later failed for some other server) is reported here; "not configured"
        // and a listing that found nothing both correctly count as zero.
        var backups = await GetAllBackupsWithStatusAsync(ct).ConfigureAwait(false);
        var foreignBackupsCount = backups.Backups.Count(
            b => b.Ownership == Servyx.Web.Models.BackupOwnership.Foreign);

        return new DashboardSummary(
            ServersOnline: servers.Count(s => s.State == ServerState.Running),
            ServersTotal: servers.Count,
            // Sourced from each server's own PlayersOnline/PlayersMax — see MapSummary's remarks. This live
            // (no-cache) path reads them off whatever IServerQueryService.GetAdoptedServersWithStatusAsync
            // returned, which today never populates them itself (only the background-refreshed cache path
            // does, via ServerStatusRefreshService's RCON read), so this honestly sums to null rather than
            // fabricating a count — never a hardcoded null standing in for "not yet implemented".
            TotalPlayers: SumOrNull(servers.Select(s => s.PlayersOnline)),
            TotalPlayerCapacity: SumOrNull(servers.Select(s => s.PlayersMax)),
            ForeignBackupsCount: foreignBackupsCount,
            AlertsCount: servers.Count(s => s.Health == ContainerHealth.Unhealthy),
            CpuSparkline: cpuPoints,
            MemorySparkline: memPoints);
    }

    /// <summary>
    /// Sums the non-null values of <paramref name="values"/>, or returns <see langword="null"/> when every
    /// value is null — so "nothing has ever been sampled" stays honestly unknown rather than reporting a
    /// fabricated aggregate of <c>0</c>. A value that is null for some servers and known for others still
    /// contributes its known servers' sum, mirroring <see cref="GetAllBackupsWithStatusAsync"/>'s own
    /// partial-count reasoning.
    /// </summary>
    private static int? SumOrNull(IEnumerable<int?> values)
    {
        var known = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Servyx.Web.Models.ServerSummary>> GetServersAsync(CancellationToken ct = default)
        => (await GetServersWithStatusAsync(ct).ConfigureAwait(false)).Servers;

    /// <inheritdoc />
    /// <remarks>
    /// Reads from <see cref="ServerStatusCache"/> when one was supplied to this instance, exactly like
    /// <see cref="GetDashboardSummaryAsync"/> — see that method's and the constructor's remarks. Falls back to
    /// the original live path (below) when no cache was supplied.
    /// </remarks>
    public async Task<Servyx.Web.Models.ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default)
    {
        if (_statusCache is not null)
        {
            return BuildServerListFromCache();
        }

        try
        {
            var result = await _query.GetAdoptedServersWithStatusAsync(ct).ConfigureAwait(false);
            return new Servyx.Web.Models.ServerListResult(
                result.Servers.Select(MapSummary).ToList(),
                result.DiscoveryFailed,
                result.FailureDetail);
        }
        catch (Exception ex)
        {
            // Last line of defense — IServerQueryService itself never throws for this, but a degraded
            // result must never depend on every implementation honoring that (mirrors
            // ServerQueryService.GetConnectionStateAsync's own reasoning for the same pattern).
            _logger.LogWarning(ex, "Failed to list adopted servers; reporting discovery as failed.");
            return new Servyx.Web.Models.ServerListResult([], DiscoveryFailed: true, FailureDetail: ex.Message);
        }
    }

    /// <summary>
    /// Builds a <see cref="Servyx.Web.Models.ServerListResult"/> straight from <see cref="_statusCache"/>'s
    /// current contents — no live call, ever. A cache with nothing in it yet (a fresh install, before the
    /// first background refresh tick completes) degrades to an honestly empty, maximally-stale list rather
    /// than <c>DiscoveryFailed</c>: nothing was attempted and failed here, there is simply nothing cached yet.
    /// </summary>
    private Servyx.Web.Models.ServerListResult BuildServerListFromCache()
    {
        var entries = _statusCache!.GetAll();
        var servers = entries.Select(e => MapSummary(e.Summary)).ToList();
        var lastUpdatedAt = entries.Count == 0 ? DateTimeOffset.MinValue : entries.Min(e => e.UpdatedAt);

        return new Servyx.Web.Models.ServerListResult(
            servers, DiscoveryFailed: false, FailureDetail: null, lastUpdatedAt, IsStale(lastUpdatedAt));
    }

    /// <summary>
    /// Builds a <see cref="DashboardSummary"/> straight from <see cref="_statusCache"/>'s current contents.
    /// Backups are the one exception — there is no backup cache (out of scope for this pass), so
    /// <see cref="GetAllBackupsWithStatusAsync"/> is still called, which in turn calls
    /// <see cref="GetServersAsync"/> → <see cref="GetServersWithStatusAsync"/>, itself now cache-backed.
    /// </summary>
    private async Task<DashboardSummary> BuildDashboardSummaryFromCacheAsync(CancellationToken ct)
    {
        var entries = _statusCache!.GetAll();
        var lastUpdatedAt = entries.Count == 0 ? DateTimeOffset.MinValue : entries.Min(e => e.UpdatedAt);

        var cpuPoints = new List<SparklinePoint>();
        var memPoints = new List<SparklinePoint>();

        // Only one point-in-time sample is available — see the live path's identical remark below for why a
        // full history is a background-collection concern this milestone does not attempt.
        var sampled = entries.FirstOrDefault(e => e.Metrics is not null);
        if (sampled?.Metrics is { } sample)
        {
            cpuPoints.Add(new SparklinePoint(sample.Timestamp, Math.Round(sample.CpuPercent, 1)));
            memPoints.Add(new SparklinePoint(sample.Timestamp, Math.Round(sample.MemoryBytes / (1024d * 1024), 1)));
        }

        var backups = await GetAllBackupsWithStatusAsync(ct).ConfigureAwait(false);
        var foreignBackupsCount = backups.Backups.Count(
            b => b.Ownership == Servyx.Web.Models.BackupOwnership.Foreign);

        return new DashboardSummary(
            ServersOnline: entries.Count(e => e.Summary.State == ServerState.Running),
            ServersTotal: entries.Count,
            TotalPlayers: SumOrNull(entries.Select(e => e.Summary.PlayersOnline)),
            TotalPlayerCapacity: SumOrNull(entries.Select(e => e.Summary.PlayersMax)),
            ForeignBackupsCount: foreignBackupsCount,
            AlertsCount: entries.Count(e => MapHealth(e.Summary.Health) == ContainerHealth.Unhealthy),
            CpuSparkline: cpuPoints,
            MemorySparkline: memPoints,
            LastUpdatedAt: lastUpdatedAt,
            IsStale: IsStale(lastUpdatedAt));
    }

    /// <summary>Whether a cache-sourced read stamped <paramref name="lastUpdatedAt"/> is old enough to warrant the "showing cached data" banner.</summary>
    private bool IsStale(DateTimeOffset lastUpdatedAt) => DateTimeOffset.UtcNow - lastUpdatedAt > _staleThreshold;

    /// <inheritdoc />
    public async Task<Servyx.Web.Models.ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false);
            return detail is null ? null : MapDetail(detail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load detail for server '{ServerId}'.", serverId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettingRow>> GetServerSettingsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var detail = await _query.GetServerDetailAsync(serverId, ct).ConfigureAwait(false);
            return detail is null ? [] : MapSettings(detail.Settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings for server '{ServerId}'.", serverId);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogLine>> GetServerLogsAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            var lines = await _query.ReadRecentLogsAsync(serverId, maxLines: 200, ct).ConfigureAwait(false);
            return lines.Select(MapLogLine).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read logs for server '{ServerId}'.", serverId);
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>Thin wrapper over <see cref="GetServerSavesWithStatusAsync"/> for callers that only need the save itself.</remarks>
    public async Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) =>
        (await GetServerSavesWithStatusAsync(serverId, ct).ConfigureAwait(false)).Save;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Driven entirely by the loaded definition's <c>saves</c> block (<see cref="SavesLayout"/>) — there is
    /// no hardcoded Palworld path here. <see cref="SavesAvailability.NotConfigured"/> covers every case
    /// where nothing can even be attempted: no single game definition is loaded (the same "exactly one
    /// definition loaded" rule <c>ServyxBackupContextSource</c> applies), the loaded definition declares no
    /// <c>saves</c> block, or no <see cref="ITransport"/> was wired into this service at all.
    /// </para>
    /// <para>
    /// <strong>Reachability, not existence, is what separates <see cref="SavesAvailability.Failed"/> from a
    /// genuinely empty <see cref="SavesAvailability.Listed"/>.</strong> By the time this method lists
    /// <c>saves.worldRoot</c>, <see cref="IServerQueryService.GetServerDetailAsync"/> has already confirmed
    /// the container exists, so a "path not found" response from the execution target honestly means "no
    /// world has been created there yet" (<see cref="SavesAvailability.Listed"/> with a null save) — while a
    /// connection failure, a container that vanished between the two calls, or a definition-declared path
    /// that fails <see cref="SandboxedPathResolver"/> containment all surface as
    /// <see cref="SavesAvailability.Failed"/>, never silently as "empty".
    /// </para>
    /// <para>
    /// <strong>Session lifetime.</strong> Unlike <c>ServyxBackupContextSource</c>, no session is cached: this
    /// service is a plain DI singleton with no disposal hook, and the Saves tab is loaded rarely enough that
    /// opening and disposing a fresh session per call is the simpler, safer choice over adding one.
    /// </para>
    /// <para>
    /// <strong>Bounding.</strong> The whole read — connect, list, stat — is wrapped in a
    /// <see cref="SavesReadTimeout"/> deadline, and both the number of world directories considered and the
    /// number of player files listed are capped (<see cref="MaxWorldDirectoriesScanned"/>,
    /// <see cref="MaxPlayerFilesListed"/>). Those caps bound what is <em>considered</em> after a directory
    /// listing comes back, not the cost of producing that listing in the first place —
    /// <see cref="Servyx.Infrastructure.Docker.DockerExecutionTarget.ListDirectoryAsync"/> fetches and parses
    /// Docker's entire recursive tar subtree for the requested directory before this method ever sees a
    /// (then-capped) list, because that is what the Docker Engine API's archive endpoint returns; there is no
    /// non-recursive listing call to bound instead. A truly enormous save directory therefore still costs
    /// real time and memory to enumerate even though only the first <see cref="MaxWorldDirectoriesScanned"/>/
    /// <see cref="MaxPlayerFilesListed"/> entries are kept — a real, documented limitation, not a silently
    /// unbounded one. What <em>is</em> capped is what gets shown: a truncated read is never presented as
    /// complete — see <see cref="SaveInfo.WorldCandidatesTruncated"/>/<see cref="SaveInfo.PlayerFilesTruncated"/>
    /// and <c>ServerSavesTab.razor</c>'s rendering of them.
    /// </para>
    /// <para>
    /// <strong>Transport gating.</strong> Only a transport that declares
    /// <see cref="TransportCapabilities.ContainerScopedFiles"/> is safe to read through. When ssh+docker is
    /// wired instead (<c>AddServyxSshDocker</c>), the same <see cref="TargetDescriptor"/> this method would
    /// build resolves against the SSH host's own filesystem, not the container's —
    /// <c>SshDockerTransport.ConnectAsync</c> only rewrites <see cref="TargetDescriptor.TransportId"/> to
    /// <c>"ssh"</c> and forwards, and <c>SshTransport</c>/<c>SftpFileChannel</c> never read
    /// <c>containerName</c>/<c>rootPath</c> out of <see cref="TargetDescriptor.Options"/> at all — so a
    /// container-internal path like <c>/palworld/Pal/Saved</c> becomes a literal path segment on the SSH
    /// host, and <c>SshDockerTransport</c> correctly does not advertise the flag. Reading through that would
    /// risk displaying host files as container save data, which is worse than not reading at all, so this
    /// method checks <see cref="ITransport.Capabilities"/> before opening any session and reports
    /// <see cref="SavesAvailability.UnsupportedTransport"/> instead of attempting a read whose result could
    /// be silently wrong. This is the same capability <c>ServyxBackupContextSource</c> requires before
    /// opening a backup session (<c>RequireContainerScopedFiles</c>) — saves and backups now share one
    /// mechanism for the one concept, differing only in how they degrade: backups throw
    /// <see cref="ContainerScopedFilesNotSupportedException"/> because a failed backup must be loud, while
    /// this read-only inspection degrades to <see cref="SavesAvailability.UnsupportedTransport"/> instead.
    /// </para>
    /// </remarks>
    public async Task<SavesResult> GetServerSavesWithStatusAsync(string serverId, CancellationToken ct = default)
    {
        var result = await ServerSavesReader
            .ReadServerSavesAsync(_query, _transport, _catalog, serverId, _logger, ct, _executionTargetResolver)
            .ConfigureAwait(false);
        return MapSavesResult(result);
    }

    /// <summary>
    /// Maps <see cref="ServerSavesReader"/>'s transport-agnostic <see cref="SavesReadResult"/> onto this
    /// project's own <see cref="SavesResult"/> view model, one-for-one — see
    /// <see cref="GetServerSavesWithStatusAsync"/>.
    /// </summary>
    private static SavesResult MapSavesResult(SavesReadResult result)
    {
        var availability = result.Availability switch
        {
            SavesReadAvailability.Listed => SavesAvailability.Listed,
            SavesReadAvailability.Failed => SavesAvailability.Failed,
            SavesReadAvailability.NotConfigured => SavesAvailability.NotConfigured,
            SavesReadAvailability.UnsupportedTransport => SavesAvailability.UnsupportedTransport,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result), result.Availability, "Unrecognized SavesReadAvailability value."),
        };

        var save = result.Save is null
            ? null
            : new SaveInfo(
                result.Save.WorldId,
                result.Save.LevelFileName,
                result.Save.LevelFileSizeBytes,
                result.Save.LevelMetaFileName,
                result.Save.LevelMetaFileSizeBytes,
                result.Save.PlayerFiles.Select(f => new PlayerSaveFile(f.FileName, f.SizeBytes)).ToList(),
                result.Save.WorldCandidatesTruncated,
                result.Save.PlayerFilesTruncated);

        return new SavesResult(save, availability, result.FailureDetail);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Degrades to an empty list on either "no backup provider is configured" or a listing failure — unlike
    /// <see cref="GetAllBackupsWithStatusAsync"/>, this member's return type has no room to say which, so a
    /// caller that needs the distinction (the Backups page) must use that member instead. This one exists
    /// for callers (the server detail page's backups tab) that only ever rendered a plain list.
    /// </remarks>
    public async Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default)
    {
        if (_backupDashboard is null || !_backupDashboard.ProviderConfigured)
        {
            return [];
        }

        try
        {
            var result = await _backupDashboard.ListAsync(serverId, ct).ConfigureAwait(false);
            if (result is not BackupListResult.Listed listed)
            {
                return [];
            }

            var detail = await GetServerDetailAsync(serverId, ct).ConfigureAwait(false);
            var serverName = detail?.Summary.Name ?? serverId;
            return listed.All.Select(a => MapBackupEntry(serverId, serverName, a)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list backups for server '{ServerId}'.", serverId);
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>Thin wrapper over <see cref="GetAllBackupsWithStatusAsync"/> for callers that only need the flat list.</remarks>
    public async Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default)
        => (await GetAllBackupsWithStatusAsync(ct).ConfigureAwait(false)).Backups;

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors <see cref="GetServersWithStatusAsync"/>'s three-way honesty: a missing/unconfigured
    /// <see cref="IBackupDashboard"/> reports <see cref="BackupsAvailability.NotConfigured"/>, any server
    /// whose listing throws or reports <see cref="BackupListResult.Failed"/> tips the whole result to
    /// <see cref="BackupsAvailability.Failed"/>, and only when every server's listing succeeds is the result
    /// <see cref="BackupsAvailability.Listed"/> — even when that listing is empty. A listing that only
    /// partly failed still reports every entry the servers that succeeded found; see
    /// <see cref="BackupsListResult.Backups"/>'s remarks for why the two are not mutually exclusive.
    /// </remarks>
    public async Task<BackupsListResult> GetAllBackupsWithStatusAsync(CancellationToken ct = default)
    {
        if (_backupDashboard is null || !_backupDashboard.ProviderConfigured)
        {
            return new BackupsListResult([], BackupsAvailability.NotConfigured, null);
        }

        var servers = await GetServersAsync(ct).ConfigureAwait(false);
        var entries = new List<BackupEntry>();
        var failures = new List<string>();

        foreach (var server in servers)
        {
            BackupListResult result;
            try
            {
                result = await _backupDashboard.ListAsync(server.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list backups for server '{ServerId}'.", server.Id);
                failures.Add($"{server.Name} ({server.Id}): {ex.Message}");
                continue;
            }

            switch (result)
            {
                case BackupListResult.Listed listed:
                    entries.AddRange(listed.All.Select(a => MapBackupEntry(server.Id, server.Name, a)));
                    break;

                case BackupListResult.Failed failed:
                    failures.Add($"{server.Name} ({server.Id}): {failed.Message}");
                    break;
            }
        }

        if (failures.Count > 0)
        {
            // At least one server's listing could not be produced. Reporting this as Listed would tell an
            // operator "these are all the backups that exist" when some are simply unknown — see
            // A_backup_listing_failure_is_distinguishable_from_no_backups. True even when other servers
            // listed cleanly: a partial answer is not the same fact as a complete one, so the whole result
            // is marked Failed even though entries collected from the servers that did succeed are kept.
            return new BackupsListResult(entries, BackupsAvailability.Failed, string.Join("; ", failures));
        }

        return new BackupsListResult(entries, BackupsAvailability.Listed, null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sourced entirely from <c>_catalog</c> — the data-driven <see cref="GameDefinitionCatalog"/> the
    /// composition root resolves through DI (see the constructor's remarks on <c>catalog</c>). The original
    /// hardcoded loader this method once read from directly (<c>PalworldDefinitionLoader</c>/
    /// <c>PalworldDefinitionInfo</c>) has been retired; every characterized literal
    /// <c>LiveDashboardDataServiceCharacterizationTests</c> pins for this method is now produced by
    /// <see cref="BuildGamesFromCatalog"/> alone.
    /// </remarks>
    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default)
        => Task.FromResult(BuildGamesFromCatalog(_catalog));

    /// <inheritdoc />
    /// <remarks>
    /// Sourced entirely from <c>_catalog</c>'s own <see cref="IDefinitionCatalogDiagnostics.Faults"/> — see
    /// the constructor's remarks on <c>catalog</c>. Ordered by <see cref="DefinitionFault.Path"/> (ordinal)
    /// for the same reason <see cref="BuildGamesFromCatalog"/> orders cards by id: a page reload must not
    /// reshuffle the list just because <see cref="GameDefinitionCatalog.Faults"/>' own iteration order is
    /// not itself a documented guarantee.
    /// </remarks>
    public Task<IReadOnlyList<GameDefinitionFaultSummary>> GetGameDefinitionFaultsAsync(CancellationToken ct = default)
        => Task.FromResult(BuildFaultsFromCatalog(_catalog));

    /// <summary>
    /// Projects every currently-loaded definition in <paramref name="catalog"/> into a
    /// <see cref="GameCardSummary"/>, ordered by <see cref="Servyx.Domain.Definitions.GameDefinitionRef.Id"/>
    /// (ordinal) so the page's card order is stable across reloads regardless of dictionary iteration order.
    /// Returns an empty list when <paramref name="catalog"/> is <see langword="null"/>. A given entry is
    /// skipped — not the whole catalogue — when its <see cref="LoadedDefinition.Document"/> is not a typed
    /// <see cref="GameDefinition"/> or it has no docker-kind deployment profile complete enough to describe
    /// (no <c>image</c>/<c>detect.imageRepo</c>); such an entry cannot answer "what does an adoptable
    /// container of this game look like" yet, the same test
    /// <see cref="Servyx.Application.Servers.AdoptionCriteriaFactory.TryDerive"/> applies for adoption.
    /// </summary>
    /// <remarks>
    /// Preserves the exact field mapping the single-definition path used before this became a projection —
    /// for the shipped Palworld definition the resulting card is byte-identical to what that path produced;
    /// see <c>LiveDashboardDataServiceCharacterizationTests</c> and
    /// <c>LiveDashboardDataServiceCatalogGamesTests</c>.
    /// </remarks>
    private static IReadOnlyList<GameCardSummary> BuildGamesFromCatalog(GameDefinitionCatalog? catalog)
    {
        if (catalog is null)
        {
            return [];
        }

        var cards = new List<GameCardSummary>();

        foreach (var (id, loaded) in catalog.DefinitionsById.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            if (loaded.Document is not GameDefinition definition)
            {
                continue;
            }

            var dockerProfile = definition.Deployments.FirstOrDefault(d => d.Kind == DeploymentKind.Docker);
            if (dockerProfile is null || dockerProfile.Image is null || dockerProfile.Detect is null
                || dockerProfile.Detect.ImageRepo is null)
            {
                continue;
            }

            cards.Add(new GameCardSummary(
                Id: definition.Metadata.Id,
                Name: definition.Metadata.Name,
                Version: definition.Metadata.Version,
                Tags: definition.Metadata.Tags,
                // CHARACTERIZATION-parity literals: matches the retired hardcoded loader's own output
                // exactly, even though the definition's own Mods.Supported (and a real trust evaluation,
                // once one exists) could answer both questions for real now — see GetGamesAsync's remarks.
                Trust: TrustTier.Builtin,
                ModsSupported: false,
                DeploymentProfiles:
                [
                    new DeploymentProfileSummary(
                        dockerProfile.Id,
                        "docker",
                        $"{dockerProfile.Image.Default}. Adopts an existing container whose image repository matches '{dockerProfile.Detect.ImageRepo}'."),
                ]));
        }

        return cards;
    }

    /// <summary>
    /// Maps every <see cref="DefinitionFault"/> currently recorded on <paramref name="catalog"/> to a
    /// <see cref="GameDefinitionFaultSummary"/>, ordered by <see cref="DefinitionFault.Path"/> (ordinal) for
    /// the same reload-stability reason <see cref="BuildGamesFromCatalog"/> orders its cards. Returns an
    /// empty list when <paramref name="catalog"/> is <see langword="null"/>.
    /// </summary>
    private static IReadOnlyList<GameDefinitionFaultSummary> BuildFaultsFromCatalog(GameDefinitionCatalog? catalog)
    {
        if (catalog is null)
        {
            return [];
        }

        return catalog.Faults
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => new GameDefinitionFaultSummary(f.Path, f.Message, f.Line, f.Column))
            .ToList();
    }

    // -- Saves: definition-driven, read-only inspection of a server's save world -----------------------------
    //
    // The implementation itself now lives in ServerSavesReader (Servyx.Composition), so a second host can
    // offer save inspection without depending on Servyx.Web. GetServerSavesWithStatusAsync above delegates to
    // it and MapSavesResult translates its transport-agnostic result onto this project's own view models.

    /// <summary>
    /// Maps a domain <see cref="BackupArtifact"/> — Servyx-owned or foreign — to the view model
    /// <see cref="BackupEntry"/>. The two <c>BackupOwnership</c> enums (<see cref="Servyx.Domain.Backups.BackupOwnership"/>
    /// and <see cref="Servyx.Web.Models.BackupOwnership"/>) are deliberately separate types — see
    /// <c>BackupsPage.razor</c>'s remarks on why the managed surface and the read-only view do not share
    /// one — so this is the one place that translates between them.
    /// </summary>
    private static BackupEntry MapBackupEntry(string serverId, string serverName, BackupArtifact artifact)
    {
        var fileName = Path.GetFileName(artifact.Location);
        return new BackupEntry(
            ServerId: serverId,
            ServerName: serverName,
            FileName: string.IsNullOrEmpty(fileName) ? artifact.Location : fileName,
            CreatedAt: artifact.CreatedAt,
            SizeBytes: artifact.SizeBytes,
            Ownership: artifact.Ownership == Servyx.Domain.Backups.BackupOwnership.Foreign
                ? Servyx.Web.Models.BackupOwnership.Foreign
                : Servyx.Web.Models.BackupOwnership.ServyxOwned);
    }

    private async Task<ResourceSample?> TryGetMetricsSampleAsync(string serverId, CancellationToken ct)
    {
        try
        {
            return await _query.GetMetricsSampleAsync(serverId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sample metrics for server '{ServerId}'.", serverId);
            return null;
        }
    }

    private static Servyx.Web.Models.ServerSummary MapSummary(Servyx.Application.Servers.ServerSummary s)
    {
        var health = MapHealth(s.Health);
        return new Servyx.Web.Models.ServerSummary(
            Id: s.Id,
            Name: s.Name,
            Game: s.Game,
            State: s.State,
            Health: health,
            HealthTooltip: s.HealthDetail ?? DefaultHealthTooltip(health),
            // Sourced from the Application-layer ServerSummary — populated by ServerStatusRefreshService's
            // background RCON read for the cache-backed path (see BuildServerListFromCache/
            // BuildDashboardSummaryFromCacheAsync), and honestly null for the live (no-cache) path, which
            // IServerQueryService does not yet populate these from. Never a hardcoded null standing in for
            // "not yet implemented" — see ServerSummary.PlayersOnline's remarks for what "unsampled" means.
            PlayersOnline: s.PlayersOnline,
            PlayersMax: s.PlayersMax,
            Uptime: s.StartedAt is null ? null : DateTimeOffset.UtcNow - s.StartedAt.Value,
            Host: s.Host,
            Ports: s.Ports.Select(p => new PortBinding(p.ContainerPort, p.Protocol, PurposeFor(p.ContainerPort), p.Published)).ToList(),
            BindingStatus: s.BindingStatus,
            AmbiguousCandidateGameIds: s.AmbiguousCandidateGameIds);
    }

    private static Servyx.Web.Models.ServerDetail MapDetail(Servyx.Application.Servers.ServerDetail d) => new(
        Summary: MapSummary(d.Summary),
        Image: d.Image,
        MountHostPath: d.MountHostPath ?? "(unknown)",
        MountContainerPath: d.MountContainerPath ?? "(unknown)",
        Network: d.Network ?? "(unknown)",
        IpAddress: d.IpAddress ?? "(unknown)",
        MemoryLimit: d.MemoryLimitBytes is null ? "(unknown)" : FormatBytes(d.MemoryLimitBytes.Value),
        CpuLimit: d.CpuLimit is null ? "(unknown)" : d.CpuLimit.Value.ToString("0.##", CultureInfo.InvariantCulture));

    /// <summary>
    /// Maps every column the read model carries. A column the read model left <see langword="null"/> — no
    /// configuration-surface reader wired, the surface unreachable, or the read failed — stays null here,
    /// so the UI shows "not yet read" rather than a fabricated value. Every value column is routed through
    /// <see cref="MaskIfSecret"/> regardless of where it came from — see that method's remarks for why this
    /// has to be structural rather than something each data source remembers to do.
    /// </summary>
    private static IReadOnlyList<SettingRow> MapSettings(IReadOnlyList<ServerSettingValue> settings) => settings
        .Select(s => new SettingRow(
            Group: s.Group,
            Key: s.Key,
            Label: s.Label,
            IsSecret: s.IsSecret,
            // Masked here as well as at the source, so no data source can bypass masking just by plugging a
            // real value into the read model without also touching this line.
            Desired: MaskIfSecret(s.IsSecret, s.Desired),
            // Defense in depth: ServerQueryService.BuildSettings already masks Authoritative before it
            // ever reaches this layer, so this is redundant-but-harmless for it today.
            Authoritative: MaskIfSecret(s.IsSecret, s.Authoritative),
            Rendered: MaskIfSecret(s.IsSecret, s.Rendered),
            Runtime: MaskIfSecret(s.IsSecret, s.Runtime),
            Drift: s.Drift,
            PendingRegeneration: s.PendingRegeneration))
        .ToList();

    /// <summary>
    /// Masks a setting's raw value at read time when <paramref name="isSecret"/> is <see langword="true"/>,
    /// returning the fixed <c>"********"</c> placeholder (or <see langword="null"/> if there is no value
    /// at all) instead of the real value.
    /// </summary>
    /// <remarks>
    /// <strong>This is the mask, not the Razor <c>&lt;input type="password"&gt;</c> bound to the Desired
    /// column in <c>ServerSettingsTab.razor</c>.</strong> <c>type="password"</c> only hides a value
    /// visually in the browser — the value is still plaintext in the DOM and in any rendered/captured
    /// markup (view source, a screenshot's accessibility tree, a test's <c>cut.Markup</c>). Any current
    /// or future column that can carry a secret-typed setting's real value (Desired, Authoritative,
    /// Rendered, Runtime, or anything added later) MUST be routed through this mask — or an equivalent
    /// read-time mask — before it is assigned to a <see cref="SettingRow"/>. Do not rely on an input's
    /// <c>type</c> attribute, a CSS class, or any other purely visual treatment as the security control.
    /// </remarks>
    internal static string? MaskIfSecret(bool isSecret, string? rawValue) =>
        !isSecret ? rawValue : rawValue is null ? null : "********";

    private static LogLine MapLogLine(ConsoleLine line) =>
        new(line.Timestamp, line.Stream == OutputStream.StdErr ? "ERROR" : "INFO", line.Text);

    private static ContainerHealth MapHealth(ServerHealthStatus health) => health switch
    {
        ServerHealthStatus.Healthy => ContainerHealth.Healthy,
        ServerHealthStatus.Unhealthy => ContainerHealth.Unhealthy,
        _ => ContainerHealth.Unknown,
    };

    private static string DefaultHealthTooltip(ContainerHealth health) => health switch
    {
        ContainerHealth.Healthy => "Reported healthy by the container's own HEALTHCHECK.",
        ContainerHealth.Unhealthy => "Reported unhealthy by the container's own HEALTHCHECK.",
        _ => "Health status not reported by the container.",
    };

    /// <summary>
    /// Maps a container port number to its purpose for the Palworld deployment this milestone supports.
    /// A per-game-definition port purpose (rather than this hardcoded heuristic) is a later-milestone
    /// improvement once the definition's <c>capabilities.network</c> block is parsed.
    /// </summary>
    private static string PurposeFor(int containerPort) => containerPort switch
    {
        8211 => "game",
        27015 => "query",
        25575 => "rcon",
        8212 => "rest",
        _ => "other",
    };

    private static string FormatBytes(long bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        const double mib = 1024d * 1024;

        if (bytes <= 0)
        {
            return "0";
        }

        var gibValue = bytes / gib;
        return gibValue >= 1
            ? $"{gibValue.ToString("0.##", CultureInfo.InvariantCulture)}G"
            : $"{(bytes / mib).ToString("0.##", CultureInfo.InvariantCulture)}M";
    }
}
