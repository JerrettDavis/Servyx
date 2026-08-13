using Servyx.Domain.Common;

namespace Servyx.Application.Servers;

/// <summary>
/// Lets an operator bring an already-running container under Servyx's management, view what is already
/// tracked, or stop tracking one — the product invariant this implements is Servyx's README verbatim:
/// "Adopts existing containers rather than owning them. Servyx attaches to game servers you already run; it
/// never creates one on your behalf." Provisioning (creating a new container) is a distinct, opt-in
/// capability elsewhere in this codebase and out of scope here.
/// </summary>
/// <remarks>
/// Every write this service performs touches ONLY Servyx's own database — it never issues a command to the
/// workload itself, so it carries no write-mode grant and is registered unconditionally in the composition
/// root, reachable even on a fully read-only host with the provisioning gate closed.
/// </remarks>
public interface IServerAdoptionService
{
    /// <summary>
    /// Lists every discoverable container not already adopted, across every loaded game definition with a
    /// derivable docker adoption profile. Read-only: never creates, starts, or modifies a workload. Reports
    /// whether discovery itself failed rather than flattening that into an indistinguishable empty list —
    /// see <see cref="CandidatesResult"/>. Never throws for an ordinary discovery failure (e.g. the daemon
    /// unreachable); a caller must check <see cref="CandidatesResult.DiscoveryFailed"/> rather than assume an
    /// empty <see cref="CandidatesResult.Candidates"/> means "no containers to adopt".
    /// </summary>
    Task<CandidatesResult> ListCandidatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists every server Servyx currently tracks (adopted or, later, provisioned) — the "VIEW it" half of
    /// this service. Reports whether the read itself failed rather than flattening that into an
    /// indistinguishable empty list — see <see cref="TrackedServersResult"/>. Never throws for an ordinary
    /// persistence failure (e.g. the database being unreachable); a caller must check
    /// <see cref="TrackedServersResult.TrackingFailed"/> rather than assume an empty
    /// <see cref="TrackedServersResult.Servers"/> means "nothing tracked".
    /// </summary>
    Task<TrackedServersResult> ListTrackedAsync(CancellationToken ct = default);

    /// <summary>
    /// Adopts <paramref name="containerId"/> under <paramref name="gameDefinitionId"/>: persists a new
    /// <c>Server</c> row (always <c>AdoptionMode.Adopted</c>, always <c>ServerWriteMode.ReadOnly</c> —
    /// granting write access is a separate, deliberate act outside this phase's scope) and records its
    /// definition binding. Never issues any command to the container itself.
    /// </summary>
    /// <param name="containerId">The discovery-native id of the container to adopt.</param>
    /// <param name="gameDefinitionId">Which loaded game definition governs the adopted server.</param>
    /// <param name="actor">
    /// The authenticated operator's identity, for the audit trail. Optional, unlike <c>IUserService</c>'s and
    /// <c>IHostRegistrationService</c>'s actor parameters — this method predates the audit trail and already
    /// has a large existing test suite calling it without one, so a default of
    /// <see cref="Servyx.Domain.Entities.AuditActors.System"/> keeps every one of those call sites compiling
    /// unchanged rather than forcing an unrelated rewrite. A caller with a real operator identity (the
    /// adoption panel) always supplies it.
    /// </param>
    /// <param name="ct">Cancels the adoption.</param>
    /// <remarks>
    /// Idempotent: adopting an already-adopted container returns
    /// <see cref="AdoptionOutcome.AlreadyAdopted"/> rather than creating a second row or throwing. An unknown
    /// <paramref name="gameDefinitionId"/>, or a container that vanished between listing and adopting, are
    /// likewise expected outcomes reported through the result, not exceptions. A genuine fault — most
    /// notably the database being unavailable — still throws; a caller that needs to degrade that honestly
    /// for a UI should catch it there, the same way <c>LiveDashboardDataService</c> already catches and
    /// degrades every other backend call rather than this service inventing a second convention.
    /// </remarks>
    Task<AdoptionResult> AdoptAsync(
        string containerId, string gameDefinitionId, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Removes Servyx's own tracking record for <paramref name="id"/> — the <c>Server</c> row — and nothing
    /// else. This method issues NO command to the container: the workload keeps running exactly as it was,
    /// untouched. "Forget" only ever means "Servyx stops tracking it."
    /// </summary>
    /// <param name="id">The tracked server to forget.</param>
    /// <param name="actor">The authenticated operator's identity, for the audit trail. See <see cref="AdoptAsync"/>'s remarks on why this is optional.</param>
    /// <param name="ct">Cancels the operation.</param>
    Task<ForgetResult> ForgetAsync(ServerId id, string? actor = null, CancellationToken ct = default);
}
