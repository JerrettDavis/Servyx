using Servyx.Domain.Lifecycle;

namespace Servyx.Application.Servers;

/// <summary>
/// Container health as reported by the workload's own HEALTHCHECK. Deliberately a separate signal from
/// <see cref="ServerState"/> — see docs/architecture.md, "Readiness vs. Container Health". Servyx never
/// derives readiness from this value; it is surfaced purely for operator visibility.
/// </summary>
public enum ServerHealthStatus
{
    Unknown,
    Healthy,
    Unhealthy,
}

/// <summary>A single network port a discovered server exposes.</summary>
public sealed record ServerPort(int? HostPort, int ContainerPort, string Protocol)
{
    /// <summary>Whether this port is published to the host network.</summary>
    public bool Published => HostPort is not null;
}

/// <summary>Read-model row for a single adopted server, independent of any particular UI framework.</summary>
public sealed record ServerSummary(
    string Id,
    string Name,
    string Game,
    ServerState State,
    ServerHealthStatus Health,
    string? HealthDetail,
    DateTimeOffset? StartedAt,
    string Host,
    IReadOnlyList<ServerPort> Ports);

/// <summary>
/// A single setting value read from a server's live configuration surface. Only the
/// <see cref="Authoritative"/> column is populated in this milestone — reading the generated INI
/// (Rendered) and live RCON/REST state (Runtime) is M2/M3 work, so those columns are left for the
/// caller to render as "not yet read" rather than being fabricated here.
/// </summary>
/// <param name="Authoritative">
/// The current value as read from the live container's environment, or <see langword="null"/> if the
/// key was not present. When <see cref="IsSecret"/> is <see langword="true"/>, this is always the fixed
/// mask <c>"********"</c> — the real value is read internally to decide presence but is never assigned
/// to this property.
/// </param>
public sealed record ServerSettingValue(string Key, string Label, string Group, bool IsSecret, string? Authoritative);

/// <summary>Everything the "Overview" and "Settings" views need for a single adopted server.</summary>
public sealed record ServerDetail(
    ServerSummary Summary,
    string Image,
    string? MountHostPath,
    string? MountContainerPath,
    string? Network,
    string? IpAddress,
    long? MemoryLimitBytes,
    double? CpuLimit,
    IReadOnlyList<ServerSettingValue> Settings);

/// <summary>
/// The adoption criteria for a single game's docker deployment profile: the image repository and
/// required container mount path <see cref="Servyx.Domain.Discovery.IServerDiscovery"/> matches
/// candidate containers against. Sourced from a game definition's <c>deployments[].detect</c> block;
/// <c>Servyx.Application</c> only needs the resolved strings, not the definition parser itself.
/// </summary>
public sealed record AdoptionCriteria(string GameId, string GameName, string ImageRepository, string RequiredMountContainerPath)
{
    /// <summary>The bundled Palworld/thijsvanloef criteria this milestone's dashboard targets by default.</summary>
    public static AdoptionCriteria PalworldDefault { get; } = new(
        GameId: "palworld",
        GameName: "Palworld Dedicated Server",
        ImageRepository: "thijsvanloef/palworld-server-docker",
        RequiredMountContainerPath: "/palworld");
}

/// <summary>
/// Whether the configured execution target (the Docker daemon, for this milestone) is reachable, and
/// which endpoint was tried — surfaced so a degraded/unreachable state can name the endpoint rather than
/// failing silently or throwing.
/// </summary>
public sealed record DockerConnectionState(bool Reachable, string Endpoint, string? Detail);
