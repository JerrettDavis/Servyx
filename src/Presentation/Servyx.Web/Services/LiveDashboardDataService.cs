using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servyx.Application.Backups;
using Servyx.Application.Servers;
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
    public LiveDashboardDataService(
        IServerQueryService query,
        ILogger<LiveDashboardDataService> logger,
        TargetDescriptor target,
        IBackupDashboard? backupDashboard = null,
        GameDefinitionCatalog? catalog = null,
        ITransport? transport = null)
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
    }

    /// <summary>
    /// Builds the default local-Docker probe target, used by the composition root when no ssh+docker host
    /// is configured. Public so <c>Program.cs</c> can register it as the <see cref="TargetDescriptor"/>
    /// singleton this service is constructed with; <c>AddServyxSshDocker</c> replaces that registration
    /// entirely when a remote host is declared, so this method is never called in that case.
    /// </summary>
    /// <param name="logger">Logger used to report a resolution failure without crashing startup.</param>
    public static TargetDescriptor BuildDockerTarget(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var endpoint = DockerEndpointResolver.Resolve((string?)null).ToString();
            return new TargetDescriptor("docker", endpoint, null, null, new Dictionary<string, string>());
        }
        catch (Exception ex)
        {
            // Resolution itself can fail (e.g. a malformed DOCKER_HOST value); still report a named,
            // if unresolved, endpoint rather than letting startup crash over it.
            logger.LogWarning(ex, "Could not resolve a Docker endpoint to probe; connection status will report disconnected.");
            return new TargetDescriptor("docker", "(unresolved Docker endpoint)", null, null, new Dictionary<string, string>());
        }
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
    public async Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
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
            // Not yet read: requires an authenticated RCON/REST session (M2 scope). null means "not
            // sampled", never a fabricated 0 — see ServerSummary.PlayersOnline's remarks.
            TotalPlayers: null,
            TotalPlayerCapacity: null,
            ForeignBackupsCount: foreignBackupsCount,
            AlertsCount: servers.Count(s => s.Health == ContainerHealth.Unhealthy),
            CpuSparkline: cpuPoints,
            MemorySparkline: memPoints);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Servyx.Web.Models.ServerSummary>> GetServersAsync(CancellationToken ct = default)
        => (await GetServersWithStatusAsync(ct).ConfigureAwait(false)).Servers;

    /// <inheritdoc />
    public async Task<Servyx.Web.Models.ServerListResult> GetServersWithStatusAsync(CancellationToken ct = default)
    {
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
        var layout = SingleLoadedDefinition(_catalog)?.Saves;
        if (layout is null)
        {
            return new SavesResult(null, SavesAvailability.NotConfigured, null);
        }

        if (_transport is null)
        {
            return new SavesResult(null, SavesAvailability.NotConfigured, null);
        }

        // Refuse before opening a single session: only a transport whose file operations are themselves
        // container-scoped is safe here. ssh+docker's file operations resolve against the SSH host's own
        // filesystem, not the container's — see SavesAvailability.UnsupportedTransport's remarks and this
        // method's own remarks. Checked via TransportCapabilities.ContainerScopedFiles, not by attempting a
        // read and hoping it fails safely: WriteGuardedTransport delegates Capabilities to whatever it
        // wraps, so this is a cheap, purely local check that never touches the network.
        if (!_transport.Capabilities.HasFlag(TransportCapabilities.ContainerScopedFiles))
        {
            return new SavesResult(
                null,
                SavesAvailability.UnsupportedTransport,
                $"This process is wired to the '{_transport.TransportId}' transport, which does not provide "
                + "container-scoped file access. Save inspection requires a transport whose file operations "
                + "are rooted inside the container, so nothing was attempted.");
        }

        using var timeoutCts = new CancellationTokenSource(SavesReadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var detail = await _query.GetServerDetailAsync(serverId, linked.Token).ConfigureAwait(false);
            if (detail is null)
            {
                return new SavesResult(null, SavesAvailability.Failed, $"'{serverId}' is not an adopted server.");
            }

            var dataRoot = detail.MountContainerPath;
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                return new SavesResult(
                    null, SavesAvailability.Failed, $"No container data root is known for '{serverId}'.");
            }

            var target = await _transport.ConnectAsync(BuildSavesDescriptor(detail.Summary.Name, dataRoot), linked.Token)
                .ConfigureAwait(false);
            try
            {
                return await ReadSavesAsync(target, dataRoot, layout, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SavesResult(null, SavesAvailability.Failed, $"Reading save data timed out after {SavesReadTimeout}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read save data for server '{ServerId}'.", serverId);
            return new SavesResult(null, SavesAvailability.Failed, ex.Message);
        }
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

    /// <summary>Deadline for the whole connect-list-stat sequence a single <see cref="GetServerSavesWithStatusAsync"/> call performs.</summary>
    private static readonly TimeSpan SavesReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Caps how many entries under <c>saves.worldRoot</c> are considered as world-directory candidates, so a
    /// directory with an unexpectedly large fan-out cannot turn one page load into unbounded enumeration.
    /// </summary>
    private const int MaxWorldDirectoriesScanned = 200;

    /// <summary>Caps how many files under the chosen world's <c>saves.playerDir</c> are listed, for the same reason.</summary>
    private const int MaxPlayerFilesListed = 500;

    private static readonly Regex DataDirPrefix = new(@"^\$\{DATA_DIR\}/?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The single loaded <see cref="GameDefinition"/>, or <see langword="null"/> when zero or more than one
    /// is loaded — the same rule <c>Program.cs</c>'s <c>singleDefinition</c> and <c>ServyxBackupContextSource</c>
    /// already apply. Saves has no per-server definition binding (out of scope for this milestone, same as
    /// backups), so this is the only honest way to find "the" definition a server's save layout comes from.
    /// </summary>
    private static GameDefinition? SingleLoadedDefinition(GameDefinitionCatalog? catalog) =>
        catalog is { DefinitionsById.Count: 1 } ? catalog.DefinitionsById.Values.Single().Document as GameDefinition : null;

    /// <summary>Builds the <see cref="TargetDescriptor"/> a saves-inspection session for <paramref name="containerName"/> is opened against.</summary>
    /// <remarks>
    /// Deliberately identical in shape to <c>ServyxBackupContextSource.BuildDockerDescriptor</c> — "docker"
    /// transport id, <c>containerName</c>/<c>rootPath</c> options — rather than a second, independently
    /// evolving way to address the same container. See that method's remarks for the write-guard-grant key
    /// this shares.
    /// </remarks>
    private static TargetDescriptor BuildSavesDescriptor(string containerName, string dataRoot) =>
        new(
            "docker",
            DockerEndpointResolver.Resolve(explicitEndpoint: null).ToString(),
            CredentialUrn: null,
            DockerContext: null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["containerName"] = containerName,
                ["rootPath"] = dataRoot,
            });

    /// <summary>
    /// Strips a leading <c>${DATA_DIR}/</c> (case-insensitive on the variable name only, matching
    /// <c>GameDefinitionYamlParser</c>'s own acceptance of <c>${data_dir}</c>/<c>${Data_Dir}</c>/<c>${DATA_DIR}</c>)
    /// from a definition-declared saves path, leaving whatever remains — including the whole string
    /// unchanged when it does not start with the variable at all — to be resolved relative to the server's
    /// data root by <see cref="SandboxedPathResolver"/>.
    /// </summary>
    private static string StripDataDirPrefix(string raw)
    {
        var match = DataDirPrefix.Match(raw);
        return match.Success ? raw[match.Length..] : raw;
    }

    /// <summary>
    /// Compiles a definition-authored <c>worldIdPattern</c> under <see cref="RegexOptions.NonBacktracking"/>
    /// — guaranteed linear-time matching — with an explicit <see cref="Regex.MatchTimeout"/> as a second,
    /// belt-and-suspenders bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately no backtracking fallback.</strong> An earlier version of this method caught
    /// <see cref="NotSupportedException"/> (thrown when a pattern needs a backreference/lookaround the
    /// linear-time engine cannot run) and fell back to <see cref="RegexOptions.Compiled"/> with the same
    /// timeout. That fallback was dead code reachable only by bypassing the parser, and worse, unsound if it
    /// ever <em>had</em> been reached: <see cref="Regex.MatchTimeout"/> bounds one match attempt, not the
    /// loop that calls it — up to <see cref="MaxWorldDirectoriesScanned"/> adversarial directory names could
    /// each cost up to a full timeout, which the <see cref="SavesReadTimeout"/>
    /// <see cref="CancellationTokenSource"/> deadline does <em>not</em> preempt, because a synchronous
    /// CPU-bound <c>Regex.IsMatch</c> call does not observe a cancellation token. Deleting the fallback
    /// removes that risk entirely rather than merely asserting it is unreachable.
    /// </para>
    /// <para>
    /// <strong>This relies on a real, enforced invariant, not a hope.</strong>
    /// <c>GameDefinitionYamlParser.ValidateSafeRegex</c> compiles every definition-declared
    /// <c>saves.worldIdPattern</c> under <see cref="RegexOptions.NonBacktracking"/> at load time and rejects
    /// the whole definition with a validation <c>Error</c> if that fails (see
    /// <c>GameDefinitionYamlParser.SavesAndMods.cs</c>) — so any <see cref="SavesLayout"/> reachable through
    /// <see cref="GameDefinitionCatalog"/> is guaranteed to compile here too. The only way to reach a pattern
    /// that does not is to construct a <see cref="SavesLayout"/> directly, bypassing the parser (exactly what
    /// this codebase's own adversarial-pattern test does, deliberately) — and for that case, throwing
    /// synchronously and immediately (caught by <see cref="GetServerSavesWithStatusAsync"/>'s outer handler
    /// and reported as <see cref="SavesAvailability.Failed"/>) is strictly safer than the deleted fallback:
    /// the failure is instant, not paid for once per directory.
    /// </para>
    /// </remarks>
    private static Regex? CompileWorldIdPattern(string? pattern) =>
        string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Reads <paramref name="layout"/>'s world root under <paramref name="target"/>, sandboxed to
    /// <paramref name="dataRoot"/>, and returns the most-recently-modified matching world as a
    /// <see cref="SaveInfo"/> — or a null save, still <see cref="SavesAvailability.Listed"/>, when the root
    /// does not exist or holds no matching world. See <see cref="GetServerSavesWithStatusAsync"/>'s remarks
    /// for why a missing path is "empty" rather than "failed" at this point in the call.
    /// </summary>
    private static async Task<SavesResult> ReadSavesAsync(
        IExecutionTarget target, string dataRoot, SavesLayout layout, CancellationToken ct)
    {
        var resolver = new SandboxedPathResolver(dataRoot);
        var worldRootRelative = StripDataDirPrefix(layout.WorldRoot);

        IReadOnlyList<FileEntry> worldEntries;
        try
        {
            worldEntries = await target.ListDirectoryAsync(resolver.Resolve(worldRootRelative), ct).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException)
        {
            // The world root does not exist (yet) inside a container GetServerDetailAsync already confirmed
            // is adopted — a genuine, trustworthy "no saves", never "could not read".
            return new SavesResult(null, SavesAvailability.Listed, null);
        }

        var pattern = CompileWorldIdPattern(layout.WorldIdPattern);
        var matching = worldEntries
            .Where(e => e.IsDirectory)
            .Where(e => pattern is null || SafeIsMatch(pattern, e.Name))
            .ToList();

        // Computed against the full matching set, before the cap below is applied — this is what makes
        // WorldCandidatesTruncated an honest signal that "most recently modified" was decided among fewer
        // than the true full set, rather than always false because it was measured on the already-capped list.
        var worldCandidatesTruncated = matching.Count > MaxWorldDirectoriesScanned;
        var candidates = matching.Take(MaxWorldDirectoriesScanned).ToList();

        if (candidates.Count == 0)
        {
            return new SavesResult(null, SavesAvailability.Listed, null);
        }

        // Deterministic pick when more than one world directory exists (e.g. an old save kept alongside the
        // active one): most-recently-modified first, then ordinal name as a stable tiebreak. Never "whatever
        // the target happened to enumerate first" — see SaveInfo's remarks.
        var chosen = candidates
            .OrderByDescending(e => e.ModifiedAt ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .First();

        var worldRelative = worldRootRelative.Length == 0 ? chosen.Name : worldRootRelative.TrimEnd('/') + "/" + chosen.Name;

        var levelStat = await target.StatAsync(resolver.Resolve(worldRelative + "/" + layout.LevelFile), ct).ConfigureAwait(false);
        var metaStat = await target.StatAsync(resolver.Resolve(worldRelative + "/" + layout.MetaFile), ct).ConfigureAwait(false);

        var playerFiles = new List<PlayerSaveFile>();
        var playerFilesTruncated = false;
        if (!string.IsNullOrEmpty(layout.PlayerDir))
        {
            try
            {
                var playerEntries = await target
                    .ListDirectoryAsync(resolver.Resolve(worldRelative + "/" + layout.PlayerDir), ct)
                    .ConfigureAwait(false);

                var files = playerEntries.Where(e => !e.IsDirectory).ToList();
                playerFilesTruncated = files.Count > MaxPlayerFilesListed;
                playerFiles = files
                    .Take(MaxPlayerFilesListed)
                    .OrderBy(e => e.Name, StringComparer.Ordinal)
                    .Select(e => new PlayerSaveFile(e.Name, e.SizeBytes ?? 0))
                    .ToList();
            }
            catch (DirectoryNotFoundException)
            {
                // No player has joined this world yet — still a genuine, populated SaveInfo, just with no
                // player files, exactly like the Palworld definition's own Players/ directory before anyone
                // connects.
            }
        }

        var save = new SaveInfo(
            WorldId: chosen.Name,
            LevelFileName: layout.LevelFile,
            LevelFileSizeBytes: levelStat.SizeBytes ?? 0,
            LevelMetaFileName: layout.MetaFile,
            LevelMetaFileSizeBytes: metaStat.SizeBytes ?? 0,
            PlayerFiles: playerFiles,
            WorldCandidatesTruncated: worldCandidatesTruncated,
            PlayerFilesTruncated: playerFilesTruncated);

        return new SavesResult(save, SavesAvailability.Listed, null);
    }

    /// <summary>
    /// Evaluates <paramref name="pattern"/> against <paramref name="candidate"/>, treating a
    /// <see cref="RegexMatchTimeoutException"/> as "did not match" — the same "one adversarial input fails
    /// this match, scanning continues" treatment <c>LogRegexReadiness.WaitForReadyAsync</c> gives a
    /// definition-authored regex, rather than letting a single pathological world-directory name fail the
    /// whole read.
    /// </summary>
    private static bool SafeIsMatch(Regex pattern, string candidate)
    {
        try
        {
            return pattern.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

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
            // Not yet read: requires an authenticated RCON/REST session (M2 scope). The Docker API has no
            // notion of "players", so null ("not sampled") is honest here — 0 would fabricate an empty
            // server. See ServerSummary.PlayersOnline's remarks.
            PlayersOnline: null,
            PlayersMax: null,
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
    /// Maps only the M1-supported Authoritative column; Desired/Rendered/Runtime and drift computation
    /// require the DB-backed intent, INI parser, and RCON/REST session respectively (M2/M3 scope) and
    /// are left <see langword="null"/>/<see cref="DriftKind.None"/> so the UI shows them as "not yet
    /// read" rather than a fabricated value. Every value column is routed through
    /// <see cref="MaskIfSecret"/> regardless of whether it is sourced yet — see that method's remarks
    /// for why this has to be structural rather than something each future data source remembers to do.
    /// </summary>
    private static IReadOnlyList<SettingRow> MapSettings(IReadOnlyList<ServerSettingValue> settings) => settings
        .Select(s => new SettingRow(
            Group: s.Group,
            Key: s.Key,
            Label: s.Label,
            IsSecret: s.IsSecret,
            // Not yet sourced (M2+ DB-backed intent). Masked at read time regardless of the hardcoded
            // null today, so a future Desired source can never bypass masking just by plugging a real
            // value in here without also touching this line.
            Desired: MaskIfSecret(s.IsSecret, rawValue: null),
            // Defense in depth: ServerQueryService.BuildSettings already masks Authoritative before it
            // ever reaches this layer, so this is redundant-but-harmless for it today.
            Authoritative: MaskIfSecret(s.IsSecret, s.Authoritative),
            // Not yet sourced (M2 INI parser).
            Rendered: MaskIfSecret(s.IsSecret, rawValue: null),
            // Not yet sourced (M2/M3 RCON/REST session).
            Runtime: MaskIfSecret(s.IsSecret, rawValue: null),
            Drift: DriftKind.None,
            PendingRegeneration: false))
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
