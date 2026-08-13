using Microsoft.Extensions.Logging;
using Servyx.Application.Auditing;
using Servyx.Domain.Common;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Servers;

namespace Servyx.Application.Servers;

/// <summary>
/// <see cref="IServerAdoptionService"/> implementation. Every write here touches ONLY <see cref="IServerRepository"/>
/// and <see cref="IServerDefinitionBindingStore"/> — Servyx's own database — and this type holds no
/// <c>ITransport</c>/execution-target dependency of any kind, so there is no collaborator through which a
/// container command could even be issued. <see cref="IHostRepository"/> is also held, but read-only: it is
/// used to resolve a discovered container's <see cref="DiscoveredServer.HostKey"/> to a durable
/// <see cref="Host"/> row (see <see cref="AdoptAsync"/>'s remarks on <see cref="Server.HostId"/>), never
/// written to from here — host registration itself is <c>IHostRegistrationService</c>'s own surface. Both
/// read paths — <see cref="ListCandidatesAsync"/> and <see cref="ListTrackedAsync"/> — report a genuine read
/// failure through their result type
/// (<see cref="CandidatesResult.DiscoveryFailed"/>/<see cref="TrackedServersResult.TrackingFailed"/>) rather
/// than flattening it into an indistinguishable empty list; the one exception is
/// <see cref="ListCandidatesAsync"/>'s own already-adopted exclusion check (and, for the same reason, its
/// host-name resolution for display), both of which degrade to "no exclusions known"/"show the host key
/// as-is" on their own persistence failure rather than failing the whole listing — see
/// <see cref="TryGetAdoptedContainerIdsAsync"/>'s remarks for why the two failure modes are held to different
/// standards. Mutating paths (<see cref="AdoptAsync"/>/<see cref="ForgetAsync"/>) let a genuine persistence
/// fault propagate as an exception, reserving the result type for expected, non-exceptional outcomes only.
/// </summary>
public sealed class ServerAdoptionService : IServerAdoptionService
{
    private readonly IServerDiscovery _discovery;
    private readonly IServerRepository _repository;
    private readonly IServerDefinitionBindingStore _bindingStore;
    private readonly IHostRepository _hostRepository;
    private readonly IAdoptionDefinitionCatalog _definitions;
    private readonly IAuditLogger? _auditLogger;
    private readonly ILogger<ServerAdoptionService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a <see cref="ServerAdoptionService"/>.</summary>
    /// <param name="auditLogger">
    /// Records <see cref="AdoptAsync"/>/<see cref="ForgetAsync"/> to Servyx's accountability trail. Nullable,
    /// unlike every other collaborator here, purely so this constructor's large existing test suite (which
    /// predates the audit trail) keeps compiling without threading a fake through every call site; a caller
    /// that omits it simply gets no audit entries. The composition root always supplies one.
    /// </param>
    /// <param name="timeProvider">Clock used to stamp <see cref="Server.CreatedAt"/>. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ServerAdoptionService(
        IServerDiscovery discovery,
        IServerRepository repository,
        IServerDefinitionBindingStore bindingStore,
        IHostRepository hostRepository,
        IAdoptionDefinitionCatalog definitions,
        ILogger<ServerAdoptionService> logger,
        TimeProvider? timeProvider = null,
        IAuditLogger? auditLogger = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(bindingStore);
        ArgumentNullException.ThrowIfNull(hostRepository);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _repository = repository;
        _bindingStore = bindingStore;
        _hostRepository = hostRepository;
        _definitions = definitions;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditLogger = auditLogger;
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
        var hostsByName = await TryGetHostsByNameAsync(ct).ConfigureAwait(false);

        return CandidatesResult.Ok(byContainerId.Values
            .Where(entry => !alreadyAdoptedContainerIds.Contains(entry.Server.ServerId))
            .Select(entry => new AdoptionCandidate(
                entry.Server.ServerId,
                entry.Server.Name,
                entry.Server.Image,
                entry.Server.State,
                entry.DefinitionIds,
                ResolveHostNameForDisplay(entry.Server.HostKey, hostsByName)))
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

    /// <summary>
    /// Best-effort, same reasoning as <see cref="TryGetAdoptedContainerIdsAsync"/>: a failure reading the
    /// host table degrades to "no registered hosts known this cycle" — every candidate's display name then
    /// falls back to its raw <see cref="DiscoveredServer.HostKey"/> (see
    /// <see cref="ResolveHostNameForDisplay"/>) — rather than failing the whole candidate listing over what
    /// is, here, a purely cosmetic lookup.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Host>> TryGetHostsByNameAsync(CancellationToken ct)
    {
        try
        {
            var hosts = await _hostRepository.ListAsync(ct).ConfigureAwait(false);
            return hosts.ToDictionary(h => h.Name, StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to read registered hosts; adoption candidates will show their raw host key instead of a resolved host name.");
            return new Dictionary<string, Host>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Resolves a discovered container's <see cref="DiscoveredServer.HostKey"/> to a display name: the
    /// matching <see cref="Host"/> row's own <see cref="Host.Name"/> when <paramref name="hostKey"/> names a
    /// database-registered host, <paramref name="hostKey"/> itself when it names a configuration-declared
    /// host with no corresponding row (see <see cref="AdoptAsync"/>'s remarks for why that gap exists), or
    /// <see langword="null"/> when discovery has no host notion at all for this container.
    /// </summary>
    private static string? ResolveHostNameForDisplay(string? hostKey, IReadOnlyDictionary<string, Host> hostsByName)
    {
        if (hostKey is null)
        {
            return null;
        }

        return hostsByName.TryGetValue(hostKey, out var host) ? host.Name : hostKey;
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
    public async Task<AdoptionResult> AdoptAsync(
        string containerId, string gameDefinitionId, string? actor = null, CancellationToken ct = default)
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

        // Resolves match.HostKey to a real Host row when one exists. HostKey is NOT reliably backed by a
        // Host row: CompositeServerDiscovery tags every result with the name HostConnectionRegistry combined
        // configured hosts (Servyx:Hosts) and database-registered hosts under — a name that, for a
        // configuration-declared host, was never written to the Hosts table at all (see
        // RegisteredHostTargetFactory/HostConnectionRegistry's own remarks: configuration hosts are
        // authoritative but are never persisted as a Host row, only database registrations are). So
        // "HostKey is non-null" does NOT imply "a Host row exists for it" — only a lookup can tell the two
        // apart. A null HostKey (the local/non-SSH discovery source, which has no host notion at all) also
        // resolves to no row. Either way, Server.HostId is set ONLY when TryGetByNameAsync actually finds a
        // row — never fabricated — matching Server.HostId's own "honest, not-modeled-is-null" contract.
        // Unlike the same lookup in ListCandidatesAsync (a cosmetic display concern that degrades on its own
        // read failure), a genuine failure here is allowed to propagate as an exception, per this class's own
        // documented policy for AdoptAsync/ForgetAsync.
        Host? resolvedHost = match.HostKey is null
            ? null
            : await _hostRepository.TryGetByNameAsync(match.HostKey, ct).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var server = new Server
        {
            Id = ServerId.New(),
            Name = match.Name,
            ContainerId = containerId,
            GameDefinitionId = definitionRef.Id,
            DefinitionContentHash = definitionRef.ContentHash,
            HostId = resolvedHost?.Id,
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

        if (_auditLogger is not null)
        {
            await _auditLogger.RecordAsync(
                string.IsNullOrWhiteSpace(actor) ? AuditActors.System : actor,
                AuditActions.ServerAdopted,
                targetType: "server",
                targetId: containerId,
                details: $"definition {definitionRef.Id}",
                ct: ct).ConfigureAwait(false);
        }

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
    public async Task<ForgetResult> ForgetAsync(ServerId id, string? actor = null, CancellationToken ct = default)
    {
        var removed = await _repository.RemoveAsync(id, ct).ConfigureAwait(false);
        if (!removed)
        {
            return ForgetResult.NotFound(id);
        }

        if (_auditLogger is not null)
        {
            await _auditLogger.RecordAsync(
                string.IsNullOrWhiteSpace(actor) ? AuditActors.System : actor,
                AuditActions.ServerForgotten,
                targetType: "server",
                targetId: id.ToString(),
                ct: ct).ConfigureAwait(false);
        }

        return ForgetResult.Forgotten();
    }
}
