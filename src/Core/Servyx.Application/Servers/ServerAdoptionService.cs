using Microsoft.Extensions.Logging;
using Servyx.Domain.Common;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;

namespace Servyx.Application.Servers;

/// <summary>
/// <see cref="IServerAdoptionService"/> implementation. Every write here touches ONLY <see cref="IServerRepository"/>
/// and <see cref="IServerDefinitionBindingStore"/> — Servyx's own database — and this type holds no
/// <c>ITransport</c>/execution-target dependency of any kind, so there is no collaborator through which a
/// container command could even be issued. Both read paths — <see cref="ListCandidatesAsync"/> and
/// <see cref="ListTrackedAsync"/> — report a genuine read failure through their result type
/// (<see cref="CandidatesResult.DiscoveryFailed"/>/<see cref="TrackedServersResult.TrackingFailed"/>) rather
/// than flattening it into an indistinguishable empty list; the one exception is
/// <see cref="ListCandidatesAsync"/>'s own already-adopted exclusion check, which degrades to "no exclusions
/// known" on its own persistence failure rather than failing the whole listing — see that private helper's
/// remarks for why the two failure modes are held to different standards. Mutating paths
/// (<see cref="AdoptAsync"/>/<see cref="ForgetAsync"/>) let a genuine persistence fault propagate as an
/// exception, reserving the result type for expected, non-exceptional outcomes only.
/// </summary>
public sealed class ServerAdoptionService : IServerAdoptionService
{
    private readonly IServerDiscovery _discovery;
    private readonly IServerRepository _repository;
    private readonly IServerDefinitionBindingStore _bindingStore;
    private readonly IAdoptionDefinitionCatalog _definitions;
    private readonly ILogger<ServerAdoptionService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a <see cref="ServerAdoptionService"/>.</summary>
    /// <param name="timeProvider">Clock used to stamp <see cref="Server.CreatedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ServerAdoptionService(
        IServerDiscovery discovery,
        IServerRepository repository,
        IServerDefinitionBindingStore bindingStore,
        IAdoptionDefinitionCatalog definitions,
        ILogger<ServerAdoptionService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(bindingStore);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _repository = repository;
        _bindingStore = bindingStore;
        _definitions = definitions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Degrades honestly on a discovery failure, exactly like <see cref="ListTrackedAsync"/> does for a
    /// persistence failure — <see cref="CandidatesResult.Failed"/>, never a silently-empty
    /// <see cref="CandidatesResult.Ok"/>. "No containers available to adopt" and "Servyx could not reach the
    /// Docker daemon" are different facts, and a caller rendering UI must be able to tell them apart.
    /// </remarks>
    public async Task<CandidatesResult> ListCandidatesAsync(CancellationToken ct = default)
    {
        var criteriaSet = _definitions.AllCriteria();
        if (criteriaSet.Count == 0)
        {
            return CandidatesResult.Ok([]);
        }

        // Aggregated by discovery-native container id: a container can, in principle, match more than one
        // loaded definition's criteria (e.g. two definitions sharing an image repository), so every matching
        // definition id is collected as a suggestion rather than the candidate being duplicated once per
        // definition it matches.
        var byContainerId = new Dictionary<string, (DiscoveredServer Server, List<string> DefinitionIds)>(StringComparer.Ordinal);

        try
        {
            foreach (var entry in criteriaSet)
            {
                var discovered = await _discovery.DiscoverAsync(
                    entry.Criteria.ImageRepository, entry.Criteria.RequiredMountContainerPath, ct).ConfigureAwait(false);

                foreach (var server in discovered)
                {
                    if (byContainerId.TryGetValue(server.ServerId, out var existing))
                    {
                        existing.DefinitionIds.Add(entry.DefinitionRef.Id);
                    }
                    else
                    {
                        byContainerId[server.ServerId] = (server, [entry.DefinitionRef.Id]);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A daemon unreachable, permission denied, etc. is reported honestly as a failed listing —
            // CandidatesResult.Failed, never a silently-empty CandidatesResult.Ok. Unlike
            // ServerQueryService.TryDiscoverAsync (which flattens the equivalent failure to an empty list for
            // callers that only need "what servers exist right now"), this method's whole reason to exist is
            // telling an operator what they can act on, so collapsing "nothing found" and "could not look"
            // into the same empty list would be exactly the lie this type was introduced to stop telling.
            _logger.LogWarning(ex, "Failed to discover adoption candidates.");
            return CandidatesResult.Failed(ex.Message);
        }

        var alreadyAdoptedContainerIds = await TryGetAdoptedContainerIdsAsync(ct).ConfigureAwait(false);

        return CandidatesResult.Ok(byContainerId.Values
            .Where(entry => !alreadyAdoptedContainerIds.Contains(entry.Server.ServerId))
            .Select(entry => new AdoptionCandidate(
                entry.Server.ServerId, entry.Server.Name, entry.Server.Image, entry.Server.State, entry.DefinitionIds))
            .ToList());
    }

    /// <summary>
    /// Best-effort: a failure reading the already-adopted table degrades to "no exclusions known" (every
    /// discovered container is offered as a candidate) rather than failing the whole listing — an operator
    /// being unable to see ANY candidates just because the exclusion check itself could not run would be a
    /// worse outcome than occasionally over-offering an already-adopted one (which <see cref="AdoptAsync"/>
    /// would then itself refuse as <see cref="AdoptionOutcome.AlreadyAdopted"/> anyway).
    /// </summary>
    private async Task<HashSet<string>> TryGetAdoptedContainerIdsAsync(CancellationToken ct)
    {
        try
        {
            var existing = await _repository.ListAsync(ct).ConfigureAwait(false);
            return existing.Select(s => s.ContainerId).ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read already-adopted servers; candidates will not be filtered.");
            return [];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Degrades honestly on a persistence failure, exactly like <see cref="ListCandidatesAsync"/> —
    /// <see cref="TrackedServersResult.Failed"/>, never a silently-empty <see cref="TrackedServersResult.Ok"/>.
    /// An empty "nothing tracked" answer and "the database could not be read" are different facts, and a
    /// caller rendering UI must be able to tell them apart: showing "nothing tracked yet" while the database
    /// is actually broken is a false, and actively misleading, signal — the operator would have no way to
    /// know Servyx cannot currently see what it has adopted.
    /// </remarks>
    public async Task<TrackedServersResult> ListTrackedAsync(CancellationToken ct = default)
    {
        try
        {
            var servers = await _repository.ListAsync(ct).ConfigureAwait(false);
            return TrackedServersResult.Ok(servers
                .Select(s => new TrackedServer(s.Id, s.Name, s.GameDefinitionId, s.AdoptionMode, s.WriteMode))
                .ToList());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read tracked servers.");
            return TrackedServersResult.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<AdoptionResult> AdoptAsync(string containerId, string gameDefinitionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDefinitionId);

        var definitionRef = _definitions.TryGetRefById(gameDefinitionId);
        if (definitionRef is null)
        {
            return AdoptionResult.UnknownDefinition(gameDefinitionId);
        }

        var criteria = _definitions.AllCriteria().FirstOrDefault(c => string.Equals(c.DefinitionRef.Id, gameDefinitionId, StringComparison.Ordinal));
        if (criteria is null)
        {
            // The definition itself loaded, but declares no derivable docker adoption profile (no docker
            // deployment, no detect block, no required mount) — see AdoptionCriteriaFactory.TryDerive.
            // Reported through the same outcome as a truly-unknown id: either way, this definition id cannot
            // be used for adoption right now.
            return AdoptionResult.UnknownDefinition(gameDefinitionId);
        }

        var discovered = await _discovery.DiscoverAsync(
            criteria.Criteria.ImageRepository, criteria.Criteria.RequiredMountContainerPath, ct).ConfigureAwait(false);

        var match = discovered.FirstOrDefault(s => string.Equals(s.ServerId, containerId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return AdoptionResult.ContainerNotFound(containerId);
        }

        // Correlation key: the discovery-native container id, durably persisted on Server.ContainerId — not
        // Name. A container's name is not a safe substitute: it is not unique across hosts (two different
        // hosts can each run a container named, say, "palworld-server", which Name-based correlation would
        // conflate into a false AlreadyAdopted the moment the second one is adopted) and can be renamed by
        // the operator outside Servyx at any time, whereas a container id is assigned once by its own daemon
        // and never changes for that workload's lifetime. The database also enforces this as a unique index
        // (see ServerConfiguration), so this pre-check exists to return an honest AlreadyAdopted result
        // rather than surface a raw unique-constraint violation as an exception.
        var existingServers = await _repository.ListAsync(ct).ConfigureAwait(false);
        var alreadyAdopted = existingServers.FirstOrDefault(s => string.Equals(s.ContainerId, containerId, StringComparison.OrdinalIgnoreCase));
        if (alreadyAdopted is not null)
        {
            return AdoptionResult.AlreadyAdopted(alreadyAdopted.Id);
        }

        var now = _timeProvider.GetUtcNow();
        var server = new Server
        {
            Id = ServerId.New(),
            Name = match.Name,
            ContainerId = containerId,
            GameDefinitionId = definitionRef.Id,
            DefinitionContentHash = definitionRef.ContentHash,
            // Phase 1 has no host-management concept yet — nothing in this codebase ever creates a Host row.
            // Left null (Server.HostId's honest "not modeled" state) rather than fabricated with a random,
            // unlinked HostId.New() — see that property's own remarks. Wiring adoption to a real Hosts row is
            // later-phase scope.
            HostId = null,
            AdoptionMode = AdoptionMode.Adopted,
            // Always ReadOnly on adoption: granting write access is a separate, deliberate operator act
            // (a later phase), never an automatic side effect of adoption.
            WriteMode = ServerWriteMode.ReadOnly,
            CreatedAt = now,
        };

        await _repository.AddAsync(server, ct).ConfigureAwait(false);

        // Pins this container's definition binding to exactly what the operator just chose. This is the
        // same durable record ServerQueryService's own multi-definition discovery path reads/writes
        // independently of Servyx's adoption bookkeeping (see IServerDefinitionBindingStore's remarks) —
        // recording it here means a subsequent multi-definition read never has to re-resolve it from
        // scratch, and never disagrees with what the operator just explicitly chose.
        await _bindingStore.SaveAsync(
                new ServerDefinitionBinding(containerId, ServerDefinitionBindingState.Bound, definitionRef, [], now), ct)
            .ConfigureAwait(false);

        return AdoptionResult.Adopted(server.Id);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removes ONLY the <c>Server</c> row. It issues no command to the container at all — no stop, no
    /// delete, nothing — matching the product invariant that Servyx never owns the workloads it adopts.
    /// There is deliberately no ITransport/IServerDiscovery call anywhere in this method (this type does not
    /// even hold an ITransport dependency at all — see the class remarks).
    /// <para>
    /// Deliberately does NOT also remove the container's <c>ServerDefinitionBindings</c> row, even though
    /// <c>Server.ContainerId</c> now durably persists exactly the key that would let it do so. That row is a
    /// DIFFERENT subsystem's state: <c>ServerQueryService.DiscoverMultiAsync</c> reads/writes the same
    /// discovery-id key space independently of Servyx's adoption bookkeeping, to remember which game
    /// definition governs a container it observes — a fact about the live container, not about whether
    /// Servyx happens to be tracking it for adoption purposes. Forget never stops the container, so it keeps
    /// running and keeps being discovered; deleting its binding here would force that still-running,
    /// still-<c>Bound</c> container back through <c>Ambiguous</c>/<c>NeedsRebind</c> purely because an
    /// operator clicked Forget in this unrelated UI — a real side effect on a subsystem Forget has no
    /// business touching, and a direct contradiction of "Forget must not disturb the live container's
    /// operational state." <b>Do not re-add a call to <c>_bindingStore.RemoveAsync</c> here</b> — if an
    /// orphan-sweep for genuinely stale bindings (ones whose container no longer exists at all) is ever
    /// needed, <see cref="IServerDefinitionBindingStore.RemoveAsync"/> is available for that, driven by
    /// discovery evidence the container is gone, not by this method.
    /// </para>
    /// </remarks>
    public async Task<ForgetResult> ForgetAsync(ServerId id, CancellationToken ct = default)
    {
        var removed = await _repository.RemoveAsync(id, ct).ConfigureAwait(false);
        return removed ? ForgetResult.Forgotten() : ForgetResult.NotFound(id);
    }
}
