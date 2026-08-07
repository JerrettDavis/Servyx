using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Servers;

/// <summary>
/// Application-level read operations over adopted servers. Consumes <c>Servyx.Domain</c> abstractions
/// only (<c>IServerDiscovery</c>, <c>IMetricsSource</c>, <c>ILogStream</c>, <c>ITransport</c>) — never a
/// specific infrastructure project — so a presentation layer can depend on this interface without also
/// depending on whichever transport (Docker, SSH, local process) happens to be registered in DI.
/// </summary>
/// <remarks>
/// Every method here is documented as read-only and every implementation must degrade gracefully:
/// a transport exception (daemon unreachable, container removed mid-call, etc.) must never propagate to
/// the caller as an unhandled exception. Callers should get back an honest "not available" result
/// (an empty list, a <see langword="null"/> detail, or a <see cref="DockerConnectionState"/> reporting
/// <c>Reachable: false</c>) instead.
/// </remarks>
public interface IServerQueryService
{
    /// <summary>
    /// Probes whether the configured execution target is reachable right now. Side-effect free, per
    /// <see cref="ITransport.ProbeAsync"/>'s contract.
    /// </summary>
    Task<DockerConnectionState> GetConnectionStateAsync(TargetDescriptor target, CancellationToken ct = default);

    /// <summary>
    /// Lists every server currently adopted (matched against this milestone's configured
    /// <see cref="AdoptionCriteria"/>). Returns an empty list — never throws — if the daemon is
    /// unreachable or no container matches.
    /// </summary>
    /// <remarks>
    /// This flattens a discovery failure to an empty list, same as the rest of this interface's
    /// degrade-honestly contract. A caller that needs to distinguish "discovery failed" from "genuinely
    /// zero servers adopted" — e.g. to render that distinction in the UI — should call
    /// <see cref="GetAdoptedServersWithStatusAsync"/> instead.
    /// </remarks>
    Task<IReadOnlyList<ServerSummary>> GetAdoptedServersAsync(CancellationToken ct = default);

    /// <summary>
    /// Same listing as <see cref="GetAdoptedServersAsync"/>, but reports whether discovery itself failed
    /// rather than flattening that into an indistinguishable empty list. Never throws. See
    /// <see cref="ServerListResult"/>.
    /// </summary>
    Task<ServerListResult> GetAdoptedServersWithStatusAsync(CancellationToken ct = default);

    /// <summary>Gets full detail for a single adopted server, or <see langword="null"/> if it is not found or unreachable.</summary>
    Task<ServerDetail?> GetServerDetailAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// Takes a single, best-effort resource-usage sample for a server, or <see langword="null"/> if one
    /// could not be taken (daemon unreachable, container not running, etc.).
    /// </summary>
    Task<ResourceSample?> GetMetricsSampleAsync(string serverId, CancellationToken ct = default);

    /// <summary>Follows a server's console output, replaying backscroll per <paramref name="maxBacklogLines"/> then streaming new lines.</summary>
    IAsyncEnumerable<ConsoleLine> FollowLogsAsync(string serverId, int maxBacklogLines, CancellationToken ct = default);

    /// <summary>
    /// Reads up to <paramref name="maxLines"/> of recent console history as a snapshot list, for
    /// callers (like a page load) that need a bounded, non-streaming read rather than an open-ended
    /// follow. Returns an empty list — never throws — if the daemon is unreachable or the container has
    /// no retained log history.
    /// </summary>
    Task<IReadOnlyList<ConsoleLine>> ReadRecentLogsAsync(string serverId, int maxLines, CancellationToken ct = default);
}
