using System.Globalization;
using Microsoft.Extensions.Logging;
using Servyx.Application.Servers;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Web.Definitions;
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
    private readonly PalworldDefinitionInfo? _definition;
    private readonly TargetDescriptor _dockerTarget;

    /// <summary>Creates a <see cref="LiveDashboardDataService"/>.</summary>
    /// <param name="definition">
    /// The bundled game definition's parsed metadata, if it loaded successfully at startup. When
    /// <see langword="null"/> (the file was missing or failed to parse), <see cref="GetGamesAsync"/>
    /// returns an honest empty catalogue rather than fabricating a card.
    /// </param>
    public LiveDashboardDataService(IServerQueryService query, ILogger<LiveDashboardDataService> logger, PalworldDefinitionInfo? definition = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(logger);

        _query = query;
        _logger = logger;
        _definition = definition;
        _dockerTarget = BuildDockerTarget(logger);
    }

    private static TargetDescriptor BuildDockerTarget(ILogger logger)
    {
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
    {
        try
        {
            var state = await _query.GetConnectionStateAsync(_dockerTarget, ct).ConfigureAwait(false);
            return state.Reachable ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker connection probe at '{Endpoint}' failed unexpectedly.", _dockerTarget.Endpoint);
            return ConnectionStatus.Disconnected;
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

        return new DashboardSummary(
            ServersOnline: servers.Count(s => s.State == ServerState.Running),
            ServersTotal: servers.Count,
            TotalPlayers: 0, // Not yet read: requires an authenticated RCON/REST session (M2 scope).
            TotalPlayerCapacity: 0,
            ForeignBackupsCount: 0, // Backup adoption is out of scope for this milestone's wiring.
            AlertsCount: servers.Count(s => s.Health == ContainerHealth.Unhealthy),
            CpuSparkline: cpuPoints,
            MemorySparkline: memPoints);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Servyx.Web.Models.ServerSummary>> GetServersAsync(CancellationToken ct = default)
    {
        try
        {
            var servers = await _query.GetAdoptedServersAsync(ct).ConfigureAwait(false);
            return servers.Select(MapSummary).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list adopted servers; showing an empty list.");
            return [];
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
    /// <remarks>Save-file inspection requires filesystem access this milestone does not wire up (M2+ scope). Always returns <see langword="null"/>.</remarks>
    public Task<SaveInfo?> GetServerSavesAsync(string serverId, CancellationToken ct = default) => Task.FromResult<SaveInfo?>(null);

    /// <inheritdoc />
    /// <remarks>Backup adoption requires filesystem access this milestone does not wire up (M2+ scope). Always returns an empty list.</remarks>
    public Task<IReadOnlyList<BackupEntry>> GetServerBackupsAsync(string serverId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BackupEntry>>([]);

    /// <inheritdoc />
    /// <remarks>Backup adoption requires filesystem access this milestone does not wire up (M2+ scope). Always returns an empty list.</remarks>
    public Task<IReadOnlyList<BackupEntry>> GetAllBackupsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BackupEntry>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<GameCardSummary>> GetGamesAsync(CancellationToken ct = default)
    {
        if (_definition is null)
        {
            return Task.FromResult<IReadOnlyList<GameCardSummary>>([]);
        }

        var card = new GameCardSummary(
            Id: _definition.GameId,
            Name: _definition.GameName,
            Version: _definition.Version,
            Tags: _definition.Tags,
            Trust: TrustTier.Builtin,
            ModsSupported: false,
            DeploymentProfiles:
            [
                new DeploymentProfileSummary(
                    "docker-thijsvanloef",
                    "docker",
                    $"{_definition.DefaultImage}. Adopts an existing container whose image repository matches '{_definition.ImageRepository}'."),
            ]);

        return Task.FromResult<IReadOnlyList<GameCardSummary>>([card]);
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
            PlayersOnline: 0, // Not yet read: requires an authenticated RCON/REST session (M2 scope).
            PlayersMax: 0,
            Uptime: s.StartedAt is null ? null : DateTimeOffset.UtcNow - s.StartedAt.Value,
            Host: s.Host,
            Ports: s.Ports.Select(p => new PortBinding(p.ContainerPort, p.Protocol, PurposeFor(p.ContainerPort), p.Published)).ToList());
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
