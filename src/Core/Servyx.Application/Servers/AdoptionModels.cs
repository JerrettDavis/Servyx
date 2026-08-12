using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Application.Servers;

/// <summary>
/// A discovered container Servyx does not yet track, offered to the operator as something they could
/// adopt. Producing this never creates, starts, or modifies any workload — see
/// <see cref="IServerAdoptionService.ListCandidatesAsync"/>.
/// </summary>
/// <param name="ContainerId">The discovery-native container id (e.g. a Docker container id).</param>
/// <param name="Name">The container's own name, as reported by discovery.</param>
/// <param name="Image">The container's image.</param>
/// <param name="State">The container's current lifecycle state, as reported by discovery (e.g. "running").</param>
/// <param name="SuggestedDefinitionIds">
/// Every loaded game definition whose adoption criteria this container matched — for UI preselection.
/// Usually one entry; more than one only when two loaded definitions share adoption criteria specific
/// enough to both match the same container.
/// </param>
/// <param name="HostName">
/// The human-readable name of the host this container was discovered on, for display only. Resolved from
/// the discovered workload's <see cref="Servyx.Domain.Discovery.DiscoveredServer.HostKey"/>: the name of the
/// matching database-registered <see cref="Servyx.Domain.Entities.Host"/> row when one exists, the host key
/// itself when discovery fanned out to a configuration-declared host with no corresponding row (see
/// <see cref="IServerAdoptionService"/>'s implementation for why one can exist without the other), or
/// <see langword="null"/> when discovery has no multi-host notion at all (a single, non-SSH source) — the
/// same condition under which <see cref="Servyx.Domain.Entities.Server.HostId"/> is left unset on adoption.
/// </param>
public sealed record AdoptionCandidate(
    string ContainerId,
    string Name,
    string Image,
    string State,
    IReadOnlyList<string> SuggestedDefinitionIds,
    string? HostName = null);

/// <summary>
/// Result of listing adoption candidates, distinguishing a genuine (possibly empty) listing from a
/// discovery failure — the <see cref="IServerAdoptionService.ListCandidatesAsync"/> sibling of
/// <see cref="TrackedServersResult"/>, same reasoning. <see cref="DiscoveryFailed"/> must never collapse
/// into <see cref="Ok"/> with an empty list: an operator seeing "no containers available to adopt" when the
/// truth is "Servyx could not reach the Docker daemon" is a false, and actively misleading, signal.
/// </summary>
/// <param name="Candidates">Every candidate found. Always empty when <paramref name="DiscoveryFailed"/> is <see langword="true"/>.</param>
/// <param name="DiscoveryFailed"><see langword="true"/> when the underlying discovery call threw rather than returning (possibly empty) results.</param>
/// <param name="FailureDetail">The failing exception's message, when <paramref name="DiscoveryFailed"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
public sealed record CandidatesResult(IReadOnlyList<AdoptionCandidate> Candidates, bool DiscoveryFailed, string? FailureDetail)
{
    /// <summary>Discovery succeeded; <paramref name="candidates"/> is the true (possibly empty) candidate list.</summary>
    public static CandidatesResult Ok(IReadOnlyList<AdoptionCandidate> candidates) => new(candidates, DiscoveryFailed: false, FailureDetail: null);

    /// <summary>Discovery failed outright — the candidate list could not be produced at all, not "read as empty".</summary>
    public static CandidatesResult Failed(string? detail) => new([], DiscoveryFailed: true, FailureDetail: detail);
}

/// <summary>A <see cref="Servyx.Domain.Entities.Server"/> row Servyx already tracks, for the "VIEW it" / "FORGET it" half of adoption.</summary>
/// <param name="Id">The tracked server's own id.</param>
/// <param name="Name">The tracked server's name (the container name it was adopted under).</param>
/// <param name="GameDefinitionId">The game definition this server is pinned to.</param>
/// <param name="AdoptionMode">Whether this row was adopted from an existing workload or provisioned.</param>
/// <param name="WriteMode">Servyx's current write-access posture for this server.</param>
public sealed record TrackedServer(
    ServerId Id,
    string Name,
    string GameDefinitionId,
    AdoptionMode AdoptionMode,
    ServerWriteMode WriteMode);

/// <summary>
/// Result of listing tracked servers, distinguishing a genuine (possibly empty) listing from a failure to
/// produce one — the same "failed vs. genuinely empty" honesty <see cref="Servyx.Application.Servers.ServerListResult"/>
/// already draws for adopted-server discovery, applied here to Servyx's own tracking table.
/// <see cref="TrackingFailed"/> must never collapse into <see cref="Ok"/> with an empty list: an operator
/// seeing "nothing tracked" when the truth is "Servyx's own database could not be read" is a false, and
/// actively misleading, signal — see <see cref="IServerAdoptionService.ListTrackedAsync"/>'s remarks.
/// </summary>
/// <param name="Servers">Every tracked server found. Always empty when <paramref name="TrackingFailed"/> is <see langword="true"/>.</param>
/// <param name="TrackingFailed"><see langword="true"/> when the underlying read threw rather than returning (possibly empty) results.</param>
/// <param name="FailureDetail">The failing exception's message, when <paramref name="TrackingFailed"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
public sealed record TrackedServersResult(IReadOnlyList<TrackedServer> Servers, bool TrackingFailed, string? FailureDetail)
{
    /// <summary>The read succeeded; <paramref name="servers"/> is the true (possibly empty) tracked-server list.</summary>
    public static TrackedServersResult Ok(IReadOnlyList<TrackedServer> servers) => new(servers, TrackingFailed: false, FailureDetail: null);

    /// <summary>The read failed outright — the tracked-server list could not be produced at all, not "read as empty".</summary>
    public static TrackedServersResult Failed(string? detail) => new([], TrackingFailed: true, FailureDetail: detail);
}

/// <summary>Which of the well-known outcomes <see cref="IServerAdoptionService.AdoptAsync"/> landed on.</summary>
public enum AdoptionOutcome
{
    /// <summary>A new <see cref="Servyx.Domain.Entities.Server"/> row was created for the adopted container.</summary>
    Adopted,

    /// <summary>The container was already adopted; no second row was created.</summary>
    AlreadyAdopted,

    /// <summary>No loaded game definition matches the requested id (or it has no derivable adoption profile).</summary>
    UnknownDefinition,

    /// <summary>The named container could not be found by discovery — it may have stopped or been removed since it was listed.</summary>
    ContainerNotFound,
}

/// <summary>
/// The outcome of one <see cref="IServerAdoptionService.AdoptAsync"/> call. Every member here is an
/// expected, non-exceptional outcome — see that method's remarks for which conditions instead throw.
/// </summary>
public sealed record AdoptionResult(AdoptionOutcome Outcome, ServerId? ServerId, string? Detail)
{
    /// <summary>A new row was created; <paramref name="id"/> is its <see cref="Servyx.Domain.Entities.Server.Id"/>.</summary>
    public static AdoptionResult Adopted(ServerId id) => new(AdoptionOutcome.Adopted, id, null);

    /// <summary>The container was already tracked as <paramref name="existingId"/>; no second row was created.</summary>
    public static AdoptionResult AlreadyAdopted(ServerId existingId) =>
        new(AdoptionOutcome.AlreadyAdopted, existingId, "This container is already adopted by Servyx.");

    /// <summary>No loaded definition answers to <paramref name="gameDefinitionId"/> (or it has no derivable adoption profile).</summary>
    public static AdoptionResult UnknownDefinition(string gameDefinitionId) =>
        new(AdoptionOutcome.UnknownDefinition, null, $"No usable game definition '{gameDefinitionId}' is loaded.");

    /// <summary><paramref name="containerId"/> could not be found by a fresh discovery pass.</summary>
    public static AdoptionResult ContainerNotFound(string containerId) =>
        new(AdoptionOutcome.ContainerNotFound, null,
            $"Container '{containerId}' was not found. It may have stopped or been removed since it was listed.");
}

/// <summary>Which of the well-known outcomes <see cref="IServerAdoptionService.ForgetAsync"/> landed on.</summary>
public enum ForgetOutcome
{
    /// <summary>The tracked <see cref="Servyx.Domain.Entities.Server"/> row was removed.</summary>
    Forgotten,

    /// <summary>No tracked server existed with the given id; nothing to remove.</summary>
    NotFound,
}

/// <summary>
/// The outcome of one <see cref="IServerAdoptionService.ForgetAsync"/> call. Never implies any command was
/// issued to the container itself — see that method's remarks.
/// </summary>
public sealed record ForgetResult(ForgetOutcome Outcome, string? Detail)
{
    /// <summary>The row was removed. Servyx stops tracking the server; the container itself was never touched.</summary>
    public static ForgetResult Forgotten() => new(ForgetOutcome.Forgotten, null);

    /// <summary>No tracked row existed for <paramref name="id"/>.</summary>
    public static ForgetResult NotFound(ServerId id) =>
        new(ForgetOutcome.NotFound, $"No tracked server '{id}' was found to forget.");
}
