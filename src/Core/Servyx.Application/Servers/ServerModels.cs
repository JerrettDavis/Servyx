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

/// <summary>
/// Whether a server's game-definition binding was resolved decisively. See
/// <see cref="Servyx.Domain.Definitions.ServerDefinitionBindingState"/>, which this mirrors — kept as a
/// separate type so <c>Servyx.Application</c> callers do not need a <c>Servyx.Domain.Definitions</c>
/// reference just to read a server's binding status off <see cref="ServerSummary"/>.
/// </summary>
public enum ServerBindingStatus
{
    /// <summary>Exactly one definition governs this server. The single-definition-loaded case is always this.</summary>
    Bound,

    /// <summary>Two or more definitions matched this server with equal specificity; see <see cref="ServerSummary.AmbiguousCandidateGameIds"/>.</summary>
    Ambiguous,

    /// <summary>This server was previously bound to a definition content hash no longer resolvable in the catalog.</summary>
    NeedsRebind,
}

/// <summary>Read-model row for a single adopted server, independent of any particular UI framework.</summary>
/// <param name="BindingStatus">
/// <see cref="ServerBindingStatus.Bound"/> for every server in the single-definition-loaded case (today's
/// only case, and the only one the characterization tests pin) — see the multi-definition binding pipeline
/// in <see cref="ServerBindingResolver"/> for when the other two states occur.
/// </param>
/// <param name="AmbiguousCandidateGameIds">
/// The <c>metadata.id</c> of every definition tied for most-specific match, named so an ambiguous server is
/// diagnosable in the UI. Empty unless <paramref name="BindingStatus"/> is <see cref="ServerBindingStatus.Ambiguous"/>.
/// </param>
public sealed record ServerSummary(
    string Id,
    string Name,
    string Game,
    ServerState State,
    ServerHealthStatus Health,
    string? HealthDetail,
    DateTimeOffset? StartedAt,
    string Host,
    IReadOnlyList<ServerPort> Ports,
    ServerBindingStatus BindingStatus = ServerBindingStatus.Bound,
    IReadOnlyList<string>? AmbiguousCandidateGameIds = null);

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
/// candidate containers against. Sourced from a game definition's <c>deployments[].detect</c> block —
/// see <see cref="AdoptionCriteriaFactory"/> — <c>Servyx.Application</c> only needs the resolved strings,
/// not the definition parser itself.
/// </summary>
/// <remarks>
/// There is deliberately no hardcoded default any more (this used to carry a <c>PalworldDefault</c>
/// fallback). A missing or undeliverable game definition now means "no adoption criteria" — visible in
/// logs and in an empty adopted-server list — rather than silently assuming every discovered container is
/// running Palworld.
/// </remarks>
public sealed record AdoptionCriteria(string GameId, string GameName, string ImageRepository, string RequiredMountContainerPath);

/// <summary>
/// Whether the configured execution target (the Docker daemon, for this milestone) is reachable, and
/// which endpoint was tried — surfaced so a degraded/unreachable state can name the endpoint rather than
/// failing silently or throwing.
/// </summary>
public sealed record DockerConnectionState(bool Reachable, string Endpoint, string? Detail);

/// <summary>
/// Result of listing adopted servers that distinguishes a genuinely empty list from one where discovery
/// itself failed (daemon unreachable, stale cached session, permission denied, malformed CLI output,
/// etc.). <see cref="ServerQueryService.GetAdoptedServersAsync"/> flattens a discovery failure to an empty
/// list for callers that only ever cared about "what servers exist" — but a caller rendering UI needs to
/// know when that empty list is not trustworthy, so it can tell the operator "could not be read" instead
/// of the indistinguishable, and actively misleading, "zero servers adopted".
/// </summary>
/// <param name="Servers">The discovered servers. Always empty when <see cref="DiscoveryFailed"/> is <see langword="true"/>.</param>
/// <param name="DiscoveryFailed">
/// <see langword="true"/> when the underlying <c>IServerDiscovery.DiscoverAsync</c> call threw rather than
/// returning (possibly empty) results.
/// </param>
/// <param name="FailureDetail">The failing exception's message, when <see cref="DiscoveryFailed"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
public sealed record ServerListResult(IReadOnlyList<ServerSummary> Servers, bool DiscoveryFailed, string? FailureDetail)
{
    /// <summary>Discovery succeeded; <paramref name="servers"/> is the true (possibly empty) adopted-server list.</summary>
    public static ServerListResult Ok(IReadOnlyList<ServerSummary> servers) => new(servers, DiscoveryFailed: false, FailureDetail: null);

    /// <summary>Discovery failed outright — the server list could not be read at all, not "read as empty".</summary>
    public static ServerListResult Failed(string? detail) => new([], DiscoveryFailed: true, FailureDetail: detail);
}
