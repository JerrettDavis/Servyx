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
    IReadOnlyList<PortBinding> Ports);

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
public sealed record DashboardSummary(
    int ServersOnline,
    int ServersTotal,
    int? TotalPlayers,
    int? TotalPlayerCapacity,
    int ForeignBackupsCount,
    int AlertsCount,
    IReadOnlyList<SparklinePoint> CpuSparkline,
    IReadOnlyList<SparklinePoint> MemorySparkline);

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

/// <summary>Read-only view of a server's save world.</summary>
public sealed record SaveInfo(
    string WorldId,
    string LevelFileName,
    long LevelFileSizeBytes,
    string LevelMetaFileName,
    long LevelMetaFileSizeBytes,
    IReadOnlyList<PlayerSaveFile> PlayerFiles);

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
