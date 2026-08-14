using Servyx.Domain.Lifecycle;

namespace Servyx.Infrastructure.Persistence.Entities;

/// <summary>
/// A single published network port, as it was last observed for a server — the durable, JSON-encoded shape
/// backing <see cref="ServerStatusSnapshot.Ports"/>. Deliberately its own small record rather than reusing
/// an Application-layer port type: this project references <c>Servyx.Domain</c> only, never
/// <c>Servyx.Application</c> (see this project's own csproj remarks and <c>ServerDefinitionBindingRecord</c>'s
/// precedent), so the durable shape and the read-model shape are mapped between explicitly, in
/// <c>Servyx.Composition</c>, rather than shared.
/// </summary>
public sealed record ServerPortSnapshot(int? HostPort, int ContainerPort, string Protocol);

/// <summary>
/// The durable, last-known status of a single adopted server, written by the background refresh worker
/// (<c>ServerStatusRefreshService</c> in <c>Servyx.Composition</c>) and read back at startup to prime the
/// in-memory <c>ServerStatusCache</c> before the first live refresh tick completes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No foreign key to <c>Servers</c>/<c>Hosts</c>, deliberately</strong> — the same reasoning
/// <see cref="ServerDefinitionBindingRecord"/> documents for itself: this row is written independently of
/// whether a Servyx <c>Server</c> entity for this container exists at all, and must survive that entity's
/// absence or removal rather than being constrained to it.
/// </para>
/// <para>
/// <strong>Keyed by the discovery-native container id</strong> (<see cref="ContainerId"/>), not any
/// Servyx-minted id — again mirroring <see cref="ServerDefinitionBindingRecord.ServerId"/>.
/// </para>
/// <para>
/// <strong>Health and binding status are stored as plain strings, not enums.</strong> Both concepts
/// (<c>ServerHealthStatus</c>, <c>ServerBindingStatus</c>) are declared in <c>Servyx.Application</c>, which
/// this project deliberately does not reference (see the csproj remarks above) — so the mapping between the
/// stored string and the real enum lives entirely in <c>Servyx.Composition</c>'s <c>ServerStatusMapping</c>,
/// the same layer that already bridges Domain and Application for this cache.
/// </para>
/// </remarks>
public sealed class ServerStatusSnapshot
{
    /// <summary>The discovery-native server id (e.g. a Docker container id) this snapshot is for.</summary>
    public required string ContainerId { get; set; }

    /// <summary>The server's display name, as last observed.</summary>
    public required string Name { get; set; }

    /// <summary>The governing game's display name (or id, when no richer name was resolvable), as last observed.</summary>
    public required string Game { get; set; }

    /// <summary>The server's lifecycle state, as last observed.</summary>
    public required ServerState State { get; set; }

    /// <summary>
    /// <c>Servyx.Application.Servers.ServerHealthStatus</c>'s name (<c>"Unknown"</c>/<c>"Healthy"</c>/
    /// <c>"Unhealthy"</c>) — see this type's remarks for why it is a string rather than that enum.
    /// </summary>
    public required string Health { get; set; }

    /// <summary>A human-readable explanation of <see cref="Health"/>, when it is Unhealthy. Null otherwise.</summary>
    public string? HealthDetail { get; set; }

    /// <summary>When the server was last observed to have started, if running.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>A human-readable label for where this server runs, as last observed.</summary>
    public required string Host { get; set; }

    /// <summary>The raw discovery-native host identity this server was found on, if any.</summary>
    public string? HostKey { get; set; }

    /// <summary>
    /// <c>Servyx.Application.Servers.ServerBindingStatus</c>'s name (<c>"Bound"</c>/<c>"Ambiguous"</c>/
    /// <c>"NeedsRebind"</c>) — see this type's remarks for why it is a string rather than that enum.
    /// </summary>
    public required string BindingStatus { get; set; }

    /// <summary>The <c>metadata.id</c> of every definition tied for most-specific match. Empty unless <see cref="BindingStatus"/> is <c>"Ambiguous"</c>.</summary>
    public required IReadOnlyList<string> AmbiguousCandidateGameIds { get; set; }

    /// <summary>Every port this server exposes, as last observed.</summary>
    public required IReadOnlyList<ServerPortSnapshot> Ports { get; set; }

    /// <summary>The most recent CPU-usage sample, as a percentage, or null if none was ever taken.</summary>
    public double? CpuPercent { get; set; }

    /// <summary>The most recent memory-usage sample, in bytes, or null if none was ever taken.</summary>
    public long? MemoryBytes { get; set; }

    /// <summary>
    /// The most recently observed connected-player count, or null when it was never read (no control
    /// channel configured for this server, the definition declares no player-list source, or the last
    /// read attempt failed). Never a fabricated zero — see <c>Servyx.Application.Servers.ServerSummary.PlayersOnline</c>'s remarks.
    /// </summary>
    public int? PlayersOnline { get; set; }

    /// <summary>
    /// The server's configured player capacity, or null when it was not observed. Sourced from the
    /// server's own configuration (e.g. an authoritative environment variable), not from a control-channel
    /// player-list reply — see <c>ServerStatusRefreshService</c>'s remarks.
    /// </summary>
    public int? PlayersMax { get; set; }

    /// <summary>When this snapshot was last refreshed.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
