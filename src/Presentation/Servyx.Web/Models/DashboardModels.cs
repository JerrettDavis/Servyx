using Servyx.Application.Servers;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions;
using Servyx.Domain.Lifecycle;

namespace Servyx.Web.Models;

/// <summary>Docker host connectivity, shown in the top bar. Distinct from any single server's state.</summary>
public enum ConnectionStatus
{
    Connected,
    Degraded,
    Disconnected,
}

/// <summary>
/// <see cref="ConnectionStatus"/> paired with the specific, transport-reported reason and the transport
/// id actually in play (e.g. "docker" for a local Docker Desktop probe, "ssh+docker" for a remote host
/// reached over SSH). Exists so the UI can render an accurate, transport-specific explanation instead of
/// a hardcoded claim (e.g. "reachable over the npipe transport") that is only ever true for one of the
/// transports Servyx supports and is an active falsehood for the rest.
/// </summary>
/// <param name="Detail">
/// Human-readable detail from the probe itself (see <c>ITransport.ProbeAsync</c>'s <c>TargetHealth.Detail</c>),
/// or <see langword="null"/> if the transport did not report one.
/// </param>
public sealed record DockerConnectionInfo(ConnectionStatus Status, string TransportId, string? Detail);

/// <summary>
/// Result of listing adopted servers that distinguishes a genuinely empty list from one where discovery
/// itself failed — the Web-layer mirror of <c>Servyx.Application.Servers.ServerListResult</c>. See that
/// type's remarks for why this distinction has to survive all the way to the UI.
/// </summary>
/// <param name="LastUpdatedAt">
/// When the underlying data was last actually read — the oldest <c>ServerStatusCache</c> entry's refresh
/// timestamp when <see cref="Servers"/> came from the cache, or <see cref="DateTimeOffset.MinValue"/> for a
/// live (non-cached) read, which is never stale by construction. Trailing-optional so every pre-existing
/// construction site (the mock/stub data services, and the live path before caching existed) keeps compiling
/// unchanged.
/// </param>
/// <param name="IsStale">
/// <see langword="true"/> when <see cref="Servers"/> came from the cache and its data is older than the
/// background refresh worker's own staleness threshold — see <c>Home.razor</c>/<c>ServersList.razor</c>'s
/// "showing cached data" banner. Always <see langword="false"/> for a live (non-cached) read.
/// </param>
public sealed record ServerListResult(
    IReadOnlyList<ServerSummary> Servers,
    bool DiscoveryFailed,
    string? FailureDetail,
    DateTimeOffset LastUpdatedAt = default,
    bool IsStale = false);

/// <summary>
/// Container health as reported by Docker's own HEALTHCHECK. Deliberately a separate signal from
/// <see cref="ServerState"/> — see docs/architecture.md, "Readiness vs. Container Health".
/// </summary>
public enum ContainerHealth
{
    Unknown,
    Healthy,
    Unhealthy,
}

/// <summary>A single network port a game definition declares.</summary>
/// <param name="Port">The port number.</param>
/// <param name="Protocol">"tcp" or "udp".</param>
/// <param name="Purpose">What the port is for, e.g. "game", "query", "rcon", "rest".</param>
/// <param name="Published">Whether the port is published to the host network.</param>
public sealed record PortBinding(int Port, string Protocol, string Purpose, bool Published)
{
    public string Label => Published ? $"{Port}/{Protocol}" : $"{Port}/{Protocol} (not published to host)";
}

/// <summary>Row shown in the dashboard/server list.</summary>
/// <param name="PlayersOnline">
/// Current player count, or <see langword="null"/> when it has not been sampled (e.g. the live Docker
/// path, which cannot read player counts without an RCON/REST session — see
/// <c>DockerMetricsSource</c>). <see langword="null"/> is not "zero players"; render it as an explicit
/// "not sampled" indicator (e.g. "—"), never as <c>0</c>.
/// </param>
/// <param name="PlayersMax">Capacity paired with <paramref name="PlayersOnline"/>; <see langword="null"/> for the same reason.</param>
/// <param name="BindingStatus">
/// The Web-layer mirror of <c>Servyx.Application.Servers.ServerSummary.BindingStatus</c> — reused directly
/// (not a duplicated enum) the same way <see cref="ServerState"/> already is, since its shape is exactly
/// what this layer needs too. Trailing-optional, defaulting to <see cref="ServerBindingStatus.Bound"/> (the
/// single-definition-loaded case every pre-existing construction site — <c>MockDashboardDataService</c>,
/// <c>StubDashboardDataService</c> — implicitly means), so none of them had to change for this field to exist.
/// </param>
/// <param name="AmbiguousCandidateGameIds">
/// See <c>Servyx.Application.Servers.ServerSummary.AmbiguousCandidateGameIds</c>'s remarks. Empty/null
/// unless <paramref name="BindingStatus"/> is not <see cref="ServerBindingStatus.Bound"/>.
/// </param>
public sealed record ServerSummary(
    string Id,
    string Name,
    string Game,
    ServerState State,
    ContainerHealth Health,
    string HealthTooltip,
    int? PlayersOnline,
    int? PlayersMax,
    TimeSpan? Uptime,
    string Host,
    IReadOnlyList<PortBinding> Ports,
    ServerBindingStatus BindingStatus = ServerBindingStatus.Bound,
    IReadOnlyList<string>? AmbiguousCandidateGameIds = null);

/// <summary>Everything shown on the server detail "Overview" tab.</summary>
public sealed record ServerDetail(
    ServerSummary Summary,
    string Image,
    string MountHostPath,
    string MountContainerPath,
    string Network,
    string IpAddress,
    string MemoryLimit,
    string CpuLimit);

/// <summary>A single point in a sparkline placeholder series.</summary>
public sealed record SparklinePoint(DateTimeOffset Timestamp, double Value);

/// <summary>Top-of-dashboard summary tiles.</summary>
/// <param name="TotalPlayers">
/// Aggregate current player count across adopted servers, or <see langword="null"/> when it has not
/// been sampled (see <see cref="ServerSummary.PlayersOnline"/>). Never fabricated as <c>0</c>.
/// </param>
/// <param name="TotalPlayerCapacity">Aggregate capacity paired with <paramref name="TotalPlayers"/>; <see langword="null"/> for the same reason.</param>
/// <param name="LastUpdatedAt">See <see cref="ServerListResult.LastUpdatedAt"/>'s identical remarks, applied to this summary's own underlying read.</param>
/// <param name="IsStale">See <see cref="ServerListResult.IsStale"/>'s identical remarks.</param>
public sealed record DashboardSummary(
    int ServersOnline,
    int ServersTotal,
    int? TotalPlayers,
    int? TotalPlayerCapacity,
    int ForeignBackupsCount,
    int AlertsCount,
    IReadOnlyList<SparklinePoint> CpuSparkline,
    IReadOnlyList<SparklinePoint> MemorySparkline,
    DateTimeOffset LastUpdatedAt = default,
    bool IsStale = false);

/// <summary>One row of the four-column settings table, mirroring <c>Servyx.Domain.Configuration.SettingState</c>.</summary>
public sealed record SettingRow(
    string Group,
    string Key,
    string Label,
    bool IsSecret,
    string? Desired,
    string? Authoritative,
    string? Rendered,
    string? Runtime,
    DriftKind Drift,
    bool PendingRegeneration)
{
    public bool HasDrift => Drift != DriftKind.None;
}

/// <summary>A single mock console log line.</summary>
public sealed record LogLine(DateTimeOffset Timestamp, string Level, string Message);

/// <summary>A single player save file under the world's <c>Players</c> directory.</summary>
public sealed record PlayerSaveFile(string FileName, long SizeBytes);

/// <summary>
/// Read-only view of a server's save world. Models exactly one world: when a definition's
/// <c>saves.worldRoot</c> holds more than one world directory (an old save kept alongside the active one,
/// for instance), <c>LiveDashboardDataService</c> picks the most-recently-modified — see its remarks on
/// <c>GetServerSavesWithStatusAsync</c> for why that, rather than a list, is the deliberate choice here.
/// </summary>
/// <param name="WorldCandidatesTruncated">
/// <see langword="true"/> when more world directories existed under <c>saves.worldRoot</c> than
/// <c>LiveDashboardDataService.MaxWorldDirectoriesScanned</c> allows considering — meaning the
/// most-recently-modified world among the ones actually looked at may not be the true most-recently-modified
/// world overall. Defaults to <see langword="false"/> so every pre-existing 6-argument construction site
/// (the mock data source) keeps compiling unchanged.
/// </param>
/// <param name="PlayerFilesTruncated">
/// <see langword="true"/> when more files existed under the chosen world's <c>saves.playerDir</c> than
/// <c>LiveDashboardDataService.MaxPlayerFilesListed</c> allows listing — meaning <see cref="PlayerFiles"/> is
/// a prefix, not the complete set. Defaults to <see langword="false"/> for the same reason.
/// </param>
public sealed record SaveInfo(
    string WorldId,
    string LevelFileName,
    long LevelFileSizeBytes,
    string LevelMetaFileName,
    long LevelMetaFileSizeBytes,
    IReadOnlyList<PlayerSaveFile> PlayerFiles,
    bool WorldCandidatesTruncated = false,
    bool PlayerFilesTruncated = false);

/// <summary>
/// Whether a save listing could be produced at all — the same three-way honesty
/// <see cref="BackupsAvailability"/> and <see cref="ServerListResult"/> already apply to their own listings,
/// applied to a single server's save world. Critically, <see cref="Failed"/> must never collapse into
/// <see cref="Listed"/> with a null save: an operator seeing "no saves" when the truth is "the host was
/// unreachable" is a false, and potentially alarming, signal.
/// </summary>
public enum SavesAvailability
{
    /// <summary>
    /// The world root was read successfully. <see cref="SavesResult.Save"/> is populated when a world
    /// matching <c>saves.worldIdPattern</c> was found, and <see langword="null"/> when the root exists but
    /// holds none (or does not exist at all) — both are a genuine, trustworthy "no saves yet".
    /// </summary>
    Listed,

    /// <summary>
    /// The world root could not be read — the server is stopped/unreachable, the definition's declared
    /// paths fail containment, or some other I/O failure occurred. Not the same fact as "there are none".
    /// </summary>
    Failed,

    /// <summary>
    /// No save layout is available to read at all: no single game definition is loaded, the loaded
    /// definition declares no <c>saves</c> block, or no execution-target transport is wired into this
    /// process. Distinct from <see cref="Failed"/> — nothing was even attempted.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// This deployment's execution-target transport is not the container-rooted Docker one save inspection
    /// requires — most notably ssh+docker, whose file operations (<c>ListDirectoryAsync</c>/<c>StatAsync</c>/
    /// <c>OpenReadAsync</c>) resolve against the SSH host's own real filesystem, not the container's, because
    /// only its <c>docker</c>-CLI-shaped exec commands actually reach inside the container. Reading saves
    /// through such a transport could silently display host files as container save data, so nothing is
    /// attempted at all — this is a named, visible "not supported for this deployment type" state, never a
    /// silent wrong answer. Distinct from both <see cref="Failed"/> (an attempt that did not succeed) and
    /// <see cref="NotConfigured"/> (nothing to attempt with in the first place).
    /// </summary>
    UnsupportedTransport,
}

/// <summary>
/// Result of reading a single server's save world, distinguishing a genuine (possibly empty) read from a
/// read failure from "nothing is configured to read saves at all" from "this deployment's transport cannot
/// safely be read for saves" — see <see cref="SavesAvailability"/>.
/// </summary>
/// <param name="Save">The world found, when <see cref="Availability"/> is <see cref="SavesAvailability.Listed"/> and one matched; otherwise <see langword="null"/>.</param>
/// <param name="Availability">Which of the four cases this result reports.</param>
/// <param name="FailureDetail">Present when <paramref name="Availability"/> is <see cref="SavesAvailability.Failed"/> or <see cref="SavesAvailability.UnsupportedTransport"/>.</param>
public sealed record SavesResult(SaveInfo? Save, SavesAvailability Availability, string? FailureDetail);

/// <summary>Ownership of a backup artifact — whether Servyx or the workload's own tooling created it.</summary>
public enum BackupOwnership
{
    /// <summary>Created by the container's own cron/tooling. Servyx will never prune, move, or rename these.</summary>
    Foreign,

    /// <summary>Created and owned by Servyx (introduced in Milestone 5).</summary>
    ServyxOwned,
}

/// <summary>A single backup archive entry.</summary>
public sealed record BackupEntry(
    string ServerId,
    string ServerName,
    string FileName,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    BackupOwnership Ownership);

/// <summary>
/// Whether a backup listing could be produced at all — the Web-layer analogue of
/// <c>Servyx.Application.Backups.BackupListResult</c>, collapsed to one server-spanning answer because
/// <c>GetAllBackupsWithStatusAsync</c> reports across every adopted server in one call. See
/// <see cref="BackupsListResult"/>'s remarks for why the three cases must never collapse into each other.
/// </summary>
public enum BackupsAvailability
{
    /// <summary>The listing was produced. <see cref="BackupsListResult.Backups"/> is everything found, including zero.</summary>
    Listed,

    /// <summary>At least one server's backups could not be listed. Not the same fact as "there are none".</summary>
    Failed,

    /// <summary>
    /// No backup provider is registered in this process — the provisioning gate is closed, or it is open but
    /// nothing wired a provider up. A different fact again from both <see cref="Listed"/> with zero entries
    /// and <see cref="Failed"/>: nothing was even attempted.
    /// </summary>
    NotConfigured,
}

/// <summary>
/// Result of listing every backup across every adopted server, distinguishing a genuine (possibly empty)
/// listing from a failure to produce one from "no backup provider is configured at all" — the same
/// three-way distinction <see cref="ServerListResult"/> draws for server discovery, applied to backups.
/// </summary>
/// <param name="Backups">
/// Every backup entry that could be listed. Populated even when <paramref name="Availability"/> is
/// <see cref="BackupsAvailability.Failed"/> — a server that failed contributes nothing, but a server that
/// listed successfully before a later one failed still contributes what it found.
/// </param>
/// <param name="Availability">Which of the three cases this result reports.</param>
/// <param name="FailureDetail">Present only when <paramref name="Availability"/> is <see cref="BackupsAvailability.Failed"/>.</param>
public sealed record BackupsListResult(IReadOnlyList<BackupEntry> Backups, BackupsAvailability Availability, string? FailureDetail);

/// <summary>A deployment profile a game definition offers (e.g. "docker-thijsvanloef").</summary>
public sealed record DeploymentProfileSummary(string Id, string Kind, string Description);

/// <summary>Card shown on the Games page.</summary>
public sealed record GameCardSummary(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> Tags,
    TrustTier Trust,
    bool ModsSupported,
    IReadOnlyList<DeploymentProfileSummary> DeploymentProfiles);

/// <summary>
/// A single definition file the catalog could not load, shown on the Games page as a visually distinct
/// "this definition failed" card rather than being silently dropped from the listing. Mirrors
/// <see cref="Servyx.Definitions.DefinitionFault"/> — see that type's remarks for what each field means and
/// why an author needs all of them to actually fix the file.
/// </summary>
/// <param name="Path">The definition file (or synthesized identifier) this fault is about.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Line">1-based source line, if this fault points at a specific location in the file.</param>
/// <param name="Column">1-based source column, if this fault points at a specific location in the file.</param>
public sealed record GameDefinitionFaultSummary(string Path, string Message, int? Line, int? Column);
