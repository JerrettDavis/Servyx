using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;

namespace Servyx.Config;

/// <summary>
/// The first <see cref="IPlanExecutor"/>: turns a set of desired setting values into a persisted, previewable
/// <see cref="ConfigChangePlan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="PreviewAsync"/> writes nothing to a game server, and that is a structural property
/// rather than a promise.</strong> The only members of <see cref="IExecutionTarget"/> this type calls are
/// <see cref="IExecutionTarget.OpenReadAsync"/> — no <see cref="IExecutionTarget.WriteFileAsync"/>, no
/// <see cref="IExecutionTarget.DeleteAsync"/>, no <see cref="IExecutionTarget.ExecuteAsync"/>, no control
/// channel. Everything a write would need is computed in memory and stored in Servyx's own database as a
/// <see cref="ChangePlanRecord"/>. The post-image is rendered here, once, and recorded verbatim precisely so
/// that a later apply writes the bytes the operator approved rather than re-deriving them from the desired
/// values a second time.
/// </para>
/// <para>
/// <strong>Every write binding is collected, not just the first.</strong>
/// <see cref="SettingDescriptor.WritableSurface"/> is deliberately unused here: it is
/// <c>Bindings.FirstOrDefault(b =&gt; b.Direction == Write)</c> and silently discards the rest. Four shipped
/// settings — Palworld's <c>PORT</c>, ARK's <c>ASA_PORT</c>, Minecraft's <c>SERVER_PORT</c> and Factorio's
/// <c>PORT</c> — declare two write bindings each (an <c>env</c> key and a <c>compose</c> pointer), so relying
/// on that property would produce a plan that quietly published a port to the container's environment and
/// never to the host network, with nothing anywhere saying so.
/// </para>
/// <para>
/// <strong>A change that cannot be made becomes a <see cref="BlockedChange"/>, never an omission and never an
/// exception.</strong> An unreachable surface, a missing adapter, an insufficient capability, a
/// <see cref="SurfaceRole.Derived"/> target, an unaddressable pointer, a write strategy that does not exist
/// yet — each yields a named refusal with a remediation hint, and
/// <see cref="ConfigChangePlan.Feasibility"/> reports the aggregate. The failure this guards against is
/// specific: a preview that showed an operator four green rows when only three of them could ever be written.
/// </para>
/// <para>
/// <strong>Addressability is decided before the write is attempted, not by catching its failure.</strong> A
/// pointer is writable exactly when the parsing adapter registered a <see cref="ConfigSpan"/> for it — that
/// is what <see cref="ConfigDocument.WithValue"/> requires, and what <c>YamlConfigAdapter</c>'s advisory
/// <c>YamlScalarValue.IsAddressable</c> is itself defined as. Consulting the span set rather than the
/// YAML-only property keeps this check format-agnostic: it is equally correct for dotenv, ini, properties and
/// json, and a future adapter inherits it for free. Compose port bindings land here: a sequence
/// <em>container</em> such as <c>/services/palworld/ports</c> has no span, because publishing a port means
/// adding a list element and changing the file's line count, which the one-line-splice fidelity contract
/// cannot express.
/// </para>
/// <para>
/// <strong>Consequences are read out of the definition, never hard-coded.</strong> Writing a surface
/// regenerates whatever is declared downstream of it, transitively:
/// <see cref="DeclaredConfigSurface.DerivedFrom"/> is walked breadth-first from every written surface and each
/// surface reached that carries a <see cref="RegenerationTrigger"/> contributes a
/// <see cref="Consequence"/> using that trigger's own description text. Nothing here knows that Minecraft's
/// <c>properties</c> is regenerated from its <c>env</c>, or that Palworld's <c>live</c> sits two hops
/// downstream of its <c>.env</c>; both fall out of the same walk.
/// </para>
/// <para>
/// <strong>It never asks <c>IServerQueryService</c> anything.</strong> See
/// <see cref="IServerPlanCatalogSource"/>'s remarks for the singleton re-entrancy deadlock that rule exists to
/// prevent. Every dependency here is at or below the layer <c>SettingStateResolverFactory</c> already sits on.
/// </para>
/// </remarks>
public sealed class PlanExecutor : IPlanExecutor
{
    /// <summary>The placeholder a secret's existing value is rendered as inside a unified diff.</summary>
    /// <remarks>
    /// Identical to <see cref="SettingStateResolver.SecretMask"/> on purpose: two different masks would let an
    /// operator infer which code path produced a given piece of text.
    /// </remarks>
    public const string SecretMask = SettingStateResolver.SecretMask;

    /// <summary>The placeholder a secret's <em>new</em> value is rendered as inside a unified diff.</summary>
    /// <remarks>
    /// A second mask is required, not decorative. Masking both sides of a changed secret to the same token
    /// would make the two lines compare equal, the diff would contain no hunk for them at all, and an
    /// operator rotating a password would be shown an empty diff — a masked value that hides the fact that
    /// anything changed is worse than no diff, because it reads as "nothing will happen". This token is
    /// applied only when the real values genuinely differ, so a secret rewritten with its current value still
    /// shows as unchanged.
    /// </remarks>
    public const string ChangedSecretMask = "******** (new value)";

    /// <summary>Servyx's one shared operator identity, recorded as <see cref="ChangePlanRecord.CreatedBy"/>.</summary>
    /// <remarks>
    /// Matches <c>OperatorAuthentication.OperatorNameClaimValue</c>, the value the settings tab already
    /// attributes a recorded desired value to. <see cref="IPlanExecutor.PreviewAsync"/> takes no actor
    /// parameter, so the identity is a construction-time fact here rather than a per-call one; it is
    /// injectable so a host with real multi-user identity can supply its own without changing this contract.
    /// </remarks>
    public const string DefaultActor = "operator";

    private readonly IServerConfigSessionSource _sessions;
    private readonly IServerPlanCatalogSource _catalogs;
    private readonly ISurfaceResolver _surfaceResolver;
    private readonly IServerSettingsService _serverSettings;
    private readonly IConfigMerger _merger;
    private readonly IChangePlanStore _store;
    private readonly IReadOnlyDictionary<string, IConfigAdapter> _adapters;
    private readonly IReadOnlyDictionary<string, IConfigValueCodec> _codecs;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanExecutor>? _logger;
    private readonly string _actor;

    /// <summary>Creates the previewer.</summary>
    /// <param name="sessions">Supplies the server's live read sessions and its declared surface set.</param>
    /// <param name="catalogs">Supplies the governing definition's id, version and settings catalogue.</param>
    /// <param name="surfaceResolver">Turns declared surfaces into concrete, capability-checked paths.</param>
    /// <param name="serverSettings">
    /// Resolves the container id <see cref="PreviewAsync"/> is called with to the tracked
    /// <see cref="ServerId"/> the plan row's foreign key needs, and supplies any recorded desired values.
    /// Used only for those two reads; this type never writes a desired value.
    /// </param>
    /// <param name="merger">Applies edits to a parsed document, honouring merge policy and codec-scoped pointers.</param>
    /// <param name="store">Durable storage for the produced plan and its actions.</param>
    /// <param name="adapters">The registered format adapters, injected as a set so a newly registered one is picked up here with no change.</param>
    /// <param name="codecs">The registered value codecs.</param>
    /// <param name="time">
    /// Supplies the clock <see cref="ChangePlanRecord.CreatedAt"/> and <see cref="ChangePlanRecord.ExpiresAt"/>
    /// are computed from. Optional; defaults to <see cref="TimeProvider.System"/>, matching
    /// <c>EfServerSettingsService</c>. Never <see cref="DateTimeOffset.UtcNow"/>.
    /// </param>
    /// <param name="logger">Optional; records a malformed definition's <c>derivedFrom</c> cycle.</param>
    /// <param name="actor">Who the plan is attributed to. Defaults to <see cref="DefaultActor"/>.</param>
    public PlanExecutor(
        IServerConfigSessionSource sessions,
        IServerPlanCatalogSource catalogs,
        ISurfaceResolver surfaceResolver,
        IServerSettingsService serverSettings,
        IConfigMerger merger,
        IChangePlanStore store,
        IEnumerable<IConfigAdapter> adapters,
        IEnumerable<IConfigValueCodec> codecs,
        TimeProvider? time = null,
        ILogger<PlanExecutor>? logger = null,
        string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(surfaceResolver);
        ArgumentNullException.ThrowIfNull(serverSettings);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(codecs);

        _sessions = sessions;
        _catalogs = catalogs;
        _surfaceResolver = surfaceResolver;
        _serverSettings = serverSettings;
        _merger = merger;
        _store = store;
        _adapters = adapters.ToDictionary(a => a.FormatId, StringComparer.OrdinalIgnoreCase);
        _codecs = codecs.ToDictionary(c => c.CodecId, StringComparer.OrdinalIgnoreCase);
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _actor = string.IsNullOrWhiteSpace(actor) ? DefaultActor : actor;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Servyx tracks no server for <paramref name="serverId"/>, or no game definition governs it. Both are
    /// refused loudly rather than degraded into a fully-blocked plan, because neither can be recorded: a
    /// <see cref="ChangePlanRecord"/> has a required foreign key to a <c>Server</c> row and required
    /// definition-identity columns, so there is nowhere to persist the refusal — and a
    /// <see cref="ConfigChangePlan"/> whose <see cref="ConfigChangePlan.Id"/> names no stored row would break
    /// the one guarantee this method's contract makes about that id.
    /// </exception>
    public async Task<ConfigChangePlan> PreviewAsync(
        string serverId,
        IReadOnlyDictionary<string, string> desiredValues,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(desiredValues);

        var catalog = await _catalogs.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No game definition governs server '{serverId}', so there is no settings catalogue to plan "
                + "against. Bind the server to a definition before previewing a configuration change.");

        var snapshot = await _serverSettings.LoadAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Servyx tracks no server for container id '{serverId}', so a change plan for it cannot be "
                + "recorded. Adopt the server first — a plan is stored against a tracked server row and is "
                + "discarded with it.");

        var context = await BindAsync(serverId, catalog.Settings, ct).ConfigureAwait(false);
        var settings = catalog.Settings
            .GroupBy(s => s.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var blocked = new List<BlockedChange>();
        var diagnostics = new List<PlanDiagnostic>();
        var edits = new Dictionary<string, List<PlannedEdit>>(StringComparer.Ordinal);
        var recreateReasons = new List<Consequence>();

        // Ordered so a plan's action list, and therefore its persisted ordinals, are deterministic for the
        // same input regardless of the caller's dictionary enumeration order.
        foreach (var key in desiredValues.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            if (!settings.TryGetValue(key, out var setting))
            {
                blocked.Add(new BlockedChange(
                    key,
                    string.Empty,
                    $"'{key}' is not a setting in definition '{catalog.DefinitionId}''s catalogue, so there "
                    + "is no binding saying where it would be written.",
                    "Use a key the governing definition declares, or add it to the definition's settings "
                    + "catalogue with a write binding."));
                continue;
            }

            var writeBindings = setting.Bindings.Where(b => b.Direction == BindingDirection.Write).ToList();
            if (writeBindings.Count == 0)
            {
                blocked.Add(new BlockedChange(
                    key,
                    string.Empty,
                    $"Setting '{key}' declares no writable binding: every binding it has is read-only.",
                    "Add a 'direction: write' binding for this setting to an authoritative surface in the "
                    + "governing definition."));
                continue;
            }

            var planned = false;
            foreach (var binding in writeBindings)
            {
                var outcome = await PlanBindingAsync(context, setting, binding, desiredValues[key], ct)
                    .ConfigureAwait(false);

                if (outcome.Blocked is { } refusal)
                {
                    blocked.Add(refusal);
                    continue;
                }

                if (!edits.TryGetValue(binding.SurfaceId, out var list))
                {
                    list = [];
                    edits[binding.SurfaceId] = list;
                }

                list.Add(outcome.Edit!);
                planned = true;
            }

            if (planned && setting.RequiresRecreate)
            {
                recreateReasons.Add(new Consequence(
                    ConsequenceKind.RecreateRequired,
                    $"Setting '{setting.Key}' is baked in when the workload's container is created, so the "
                    + "container must be recreated for the new value to take effect."));
            }
        }

        var actions = new List<PlannedAction>();
        var actionRows = new List<ChangePlanActionRecord>();
        var planId = ChangePlanId.New();

        foreach (var surfaceId in edits.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Render(context, planId, surfaceId, edits[surfaceId], actions, actionRows, blocked);
        }

        var written = actions.Select(a => a.SurfaceId).ToList();
        var consequences = DeriveConsequences(context, written, diagnostics);
        consequences.AddRange(Dedupe(recreateReasons));

        var surfaceHashes = context.Hashes.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        await PersistAsync(
                planId, snapshot.ServerId, catalog, consequences, surfaceHashes, blocked, diagnostics, actionRows, ct)
            .ConfigureAwait(false);

        return new ConfigChangePlan(planId.ToString(), actions, consequences, surfaceHashes)
        {
            Blocked = blocked,
            Diagnostics = diagnostics,
        };
    }

    /// <inheritdoc />
    public Task<ChangeReceipt> ApplyAsync(string planId, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "Applying a previewed plan is not implemented yet. PreviewAsync computes, masks and persists a "
            + "plan; nothing in Servyx writes a configuration surface to a game server. See "
            + "ChangePlanRecord.Status for the state machine a later phase will drive.");

    /// <inheritdoc />
    public Task RevertAsync(string planId, CancellationToken ct = default) =>
        throw new NotImplementedException(
            "Reverting an applied plan is not implemented yet, because nothing can apply one. The recorded "
            + "ChangePlanActionRecord.PreImageContent this will restore from is already written at preview "
            + "time.");

    // ── Binding ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the server's declared surfaces against every session it is reachable through, keeping the
    /// failures for the ones that are not.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="SettingStateResolverFactory.CreateAsync"/>'s own merge: a
    /// <see cref="SurfaceResolutionContext"/> names exactly one session root, so each session resolves the
    /// whole set and contributes the subset it can reach. Unlike that method this one does not treat a
    /// surface resolving on two sessions as fatal — a preview that threw would take a settings page down,
    /// and the honest downgrade is one blocked change per affected setting. The first session to resolve a
    /// surface wins and the second is recorded as a failure, so nothing is written against an ambiguous path.
    /// </remarks>
    private async Task<PlanContext> BindAsync(
        string serverId,
        IReadOnlyList<SettingDescriptor> settings,
        CancellationToken ct)
    {
        var sessions = await _sessions.GetAsync(serverId, ct).ConfigureAwait(false);
        var declared = (sessions?.Surfaces ?? []).ToDictionary(s => s.Id, StringComparer.Ordinal);

        var bound = new Dictionary<string, BoundSurface>(StringComparer.Ordinal);
        var failures = new Dictionary<string, SurfaceResolutionFailure>(StringComparer.Ordinal);

        // Tracked separately from `failures`, not folded into it. A surface routinely fails on the session
        // that cannot reach it and succeeds on the one that can — that failure is an artefact of asking the
        // wrong session, and treating a recorded failure as a reason to refuse a later success would leave
        // every '${COMPOSE_DIR}' surface unbound purely because the container session was asked first.
        var conflicted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var session in sessions?.Sessions ?? [])
        {
            var resolution = await _surfaceResolver
                .ResolveAsync(serverId, session.Target, sessions!.Surfaces, ct)
                .ConfigureAwait(false);

            foreach (var surface in resolution.Resolved)
            {
                if (!declared.TryGetValue(surface.Id, out var declaration) || conflicted.Contains(surface.Id))
                {
                    continue;
                }

                if (bound.TryGetValue(surface.Id, out var already))
                {
                    failures[surface.Id] = new SurfaceResolutionFailure(
                        surface.Id,
                        $"Surface '{surface.Id}' resolved on two different sessions "
                        + $"('{already.Surface.Path?.Value}' via {already.Session.Description}, and "
                        + $"'{surface.Path?.Value}' via {session.Description}), so there is no single file a "
                        + "write could unambiguously target.",
                        "Fix the ISurfaceResolutionContextSource so each session expands at most one of "
                        + "'${DATA_DIR}'/'${COMPOSE_DIR}'. Writing to either candidate would be a guess, and "
                        + "the wrong guess edits a real file on the wrong machine.");

                    bound.Remove(surface.Id);
                    conflicted.Add(surface.Id);
                    continue;
                }

                bound[surface.Id] = new BoundSurface(surface, declaration, session);
            }

            foreach (var failure in resolution.Unresolvable)
            {
                if (!conflicted.Contains(failure.SurfaceId))
                {
                    Record(failures, failure);
                }
            }
        }

        foreach (var id in bound.Keys)
        {
            failures.Remove(id);
        }

        return new PlanContext(sessions?.Surfaces ?? [], bound, failures, settings);
    }

    /// <summary>
    /// Collapses the per-session failures for one surface into a single entry, mirroring
    /// <see cref="SettingStateResolverFactory"/>'s own <c>Record</c>.
    /// </summary>
    /// <remarks>
    /// Combining rather than first-wins matters here in a way it does not for a read. A two-session
    /// deployment asks the container session about a <c>${COMPOSE_DIR}</c> surface first and gets back "no
    /// expansion is known for that variable" — true, but an artefact of asking the wrong session. Keeping
    /// only that one would hide the compose session's real answer ("this transport advertises no FileWrite"),
    /// which is the failure the operator can actually act on. Identical reasons dedupe, so the usual case
    /// stays a single sentence.
    /// </remarks>
    private static void Record(Dictionary<string, SurfaceResolutionFailure> failures, SurfaceResolutionFailure failure)
    {
        if (!failures.TryGetValue(failure.SurfaceId, out var existing))
        {
            failures[failure.SurfaceId] = failure;
            return;
        }

        if (string.Equals(existing.Reason, failure.Reason, StringComparison.Ordinal))
        {
            return;
        }

        failures[failure.SurfaceId] = existing with
        {
            Reason = $"{existing.Reason} On this server's other configuration session: {failure.Reason}",
            RemediationHint = string.Equals(existing.RemediationHint, failure.RemediationHint, StringComparison.Ordinal)
                ? existing.RemediationHint
                : $"{existing.RemediationHint} Or: {failure.RemediationHint}",
        };
    }

    /// <summary>Turns one write binding into either a pending edit or a named refusal.</summary>
    private async Task<BindingOutcome> PlanBindingAsync(
        PlanContext context,
        SettingDescriptor setting,
        SettingBinding binding,
        string desiredValue,
        CancellationToken ct)
    {
        if (!context.Bound.TryGetValue(binding.SurfaceId, out var surface))
        {
            var (reason, hint) = context.Failures.TryGetValue(binding.SurfaceId, out var failure)
                ? (failure.Reason, failure.RemediationHint)
                : ($"Surface '{binding.SurfaceId}' is not reachable on any session opened for this server.",
                    "Open a session that can reach this surface, or correct the definition's locator for it.");

            return BindingOutcome.Refused(setting.Key, binding.SurfaceId, reason, hint);
        }

        // The Derived/Runtime refusal, restated here rather than delegated. ISurfaceResolver already declines
        // to put FileWrite in such a surface's RequiredCapabilities, but that only means a write would be
        // unauthorized — this says the write must never be attempted at all, because the workload regenerates
        // the file and would discard or fight it. The two are different statements and an operator is owed
        // the second one.
        if (surface.Surface.Role != SurfaceRole.Authoritative)
        {
            return BindingOutcome.Refused(
                setting.Key,
                binding.SurfaceId,
                $"Surface '{binding.SurfaceId}' is {surface.Surface.Role}: it is generated by the workload "
                + "itself, and Servyx never writes a surface the workload regenerates. A write here would be "
                + "silently discarded the next time it is regenerated.",
                surface.Declaration.DerivedFrom.Count > 0
                    ? $"Write the upstream surface(s) it is derived from instead: "
                        + $"{string.Join(", ", surface.Declaration.DerivedFrom.Select(u => $"'{u}'"))}."
                    : "Bind this setting's write direction to an authoritative surface in the definition.");
        }

        if (!surface.Surface.RequiredCapabilities.HasFlag(TransportCapabilities.FileWrite))
        {
            return BindingOutcome.Refused(
                setting.Key,
                binding.SurfaceId,
                $"Surface '{binding.SurfaceId}' resolved without TransportCapabilities.FileWrite, so the "
                + "session it is reachable on cannot apply a write to it.",
                "Connect through a transport with file write access (SFTP for SSH, the Engine API for "
                + "Docker). An exec-only session cannot write this surface.");
        }

        var read = await ReadAsync(context, surface, ct).ConfigureAwait(false);
        if (read.Error is not null)
        {
            // The hint travels with the failure. It used to be hardcoded here, which was wrong rather than
            // merely vague: it told the operator to "make the surface readable" even for a file Servyx had
            // just read and parsed successfully and was declining only to REWRITE, sending them to debug a
            // problem that does not exist.
            return BindingOutcome.Refused(setting.Key, binding.SurfaceId, read.Error, read.Hint!);
        }

        var pointer = Pointer(setting, binding, surface, read);
        if (pointer.Refusal is { } refusal)
        {
            return BindingOutcome.Refused(setting.Key, binding.SurfaceId, refusal.Reason, refusal.Hint);
        }

        // Addressability, decided from the span set rather than by attempting the write and catching its
        // failure — see this type's own remarks. This is the check that turns a compose port binding into a
        // blocked change instead of an exception.
        if (!HasSpan(read.Document!, pointer.SpanPath!))
        {
            // Two genuinely different situations share one symptom (no span), and telling them apart matters:
            // an operator reading "collections are not addressable" about a key that is simply missing from
            // their .env would go looking for a collection that was never involved.
            var missingKey = binding is SettingBinding.ByKey;

            return BindingOutcome.Refused(
                setting.Key,
                binding.SurfaceId,
                missingKey
                    ? $"Surface '{binding.SurfaceId}' contains no '{pointer.SpanPath}' entry. Servyx replaces "
                        + "the value of a key that is already present and never adds a new one: a key the "
                        + "workload does not already write is not necessarily a key it reads, and inventing "
                        + "one would be a guess written into somebody's live configuration."
                    : $"'{pointer.SpanPath}' is not an addressable value in surface '{binding.SurfaceId}': the "
                        + $"{surface.Surface.FormatId} adapter registered no writable span for it. Either the "
                        + "pointer names nothing in this document, or it names something that cannot be "
                        + "written — a collection (only the scalars inside one are addressable), or a value "
                        + "spanning more than one source line, which cannot be spliced without changing the "
                        + "file's line count.",
                binding is SettingBinding.ByPointer { Strategy: not null } strategyBinding
                    ? $"Publishing this value needs the '{strategyBinding.Strategy}' strategy to resolve the "
                        + "pointer down to a concrete element first; that strategy layer does not exist yet. "
                        + "Edit this entry by hand in the meantime."
                    : missingKey
                        ? $"Add '{pointer.SpanPath}' to the file by hand once, then Servyx can maintain it — "
                            + "or correct the binding's key in the governing definition if it is misspelled."
                        : "Address a single-line scalar value, or edit this entry by hand.");
        }

        if (binding is SettingBinding.ByPointer { Strategy: { } strategy })
        {
            // Reached only when the pointer IS addressable. Writing the raw desired value would ignore the
            // declared transform entirely and put, say, a bare port number where a "8211:8211/udp" mapping
            // belongs — a silently wrong write, which is worse than a refusal.
            return BindingOutcome.Refused(
                setting.Key,
                binding.SurfaceId,
                $"Binding for '{setting.Key}' on surface '{binding.SurfaceId}' declares the write strategy "
                + $"'{strategy}', which transforms the value before it is written. No strategy layer is "
                + "implemented, and writing the raw value instead would write something the format does not "
                + "mean.",
                $"Implement the '{strategy}' write strategy, or edit this value by hand.");
        }

        return BindingOutcome.Planned(new PlannedEdit(
            setting,
            binding,
            new ConfigPointer(pointer.EditPath!),
            desiredValue));
    }

    /// <summary>
    /// Works out the pointer a binding addresses: the edit pointer handed to <see cref="IConfigMerger"/>, and
    /// the span pointer whose presence decides addressability. They differ only for a codec member, whose
    /// edit pointer is the merger's <c>path#codecId:member</c> form while its span is the enclosing scalar's.
    /// </summary>
    private PointerResolution Pointer(
        SettingDescriptor setting,
        SettingBinding binding,
        BoundSurface surface,
        SurfaceRead read)
    {
        switch (binding)
        {
            case SettingBinding.ByKey key:
                return PointerResolution.At(Normalize(read.Document!, key.Key), Normalize(read.Document!, key.Key));

            case SettingBinding.ByPointer pointer:
                return PointerResolution.At(
                    Normalize(read.Document!, pointer.Pointer),
                    Normalize(read.Document!, pointer.Pointer));

            case SettingBinding.ByMember member:
            {
                if (surface.Surface.CodecId is not { } codecId || surface.Surface.CodecPath is not { } codecPath)
                {
                    return PointerResolution.Refuse(
                        $"Setting '{setting.Key}' is bound to member '{member.Member}' of surface "
                        + $"'{binding.SurfaceId}', but that surface declares no codec, so there is no packed "
                        + "scalar for the member to live in.",
                        "Declare 'codec' and 'codecPath' on the surface, or bind this setting by key or "
                        + "pointer instead. Writing the member name as a bare key would create a key the "
                        + "workload does not read.");
                }

                if (!_codecs.ContainsKey(codecId))
                {
                    return PointerResolution.Refuse(
                        $"Surface '{binding.SurfaceId}' declares codec '{codecId}', but no IConfigValueCodec "
                        + "is registered for it, so the scalar holding this member cannot be decoded or "
                        + "re-encoded.",
                        $"Register an IConfigValueCodec whose CodecId is '{codecId}' — AddServyxConfig() is "
                        + "where the built-in codecs are wired.");
                }

                if (read.Members is null || !read.Members.ContainsKey(member.Member))
                {
                    return PointerResolution.Refuse(
                        $"Codec '{codecId}' decoded surface '{binding.SurfaceId}' but found no member "
                        + $"'{member.Member}' in it, and a codec write replaces an existing member rather "
                        + "than adding one.",
                        "Correct the binding's member name, or start the workload once so it writes a "
                        + "complete settings blob containing this member.");
                }

                var span = Normalize(read.Document!, codecPath);
                return PointerResolution.At($"{span}#{codecId}:{member.Member}", span);
            }

            default:
                return PointerResolution.Refuse(
                    $"Setting '{setting.Key}' uses binding kind '{binding.GetType().Name}', which this "
                    + "previewer does not know how to address.",
                    "Teach PlanExecutor how to address this binding kind, or use a by-key, by-member or "
                    + "by-pointer binding.");
        }
    }

    // ── Rendering ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies one surface's whole edit set, renders the post-image, masks both images for display, and emits
    /// the resulting action — or blocks the edits the merge refuses.
    /// </summary>
    private void Render(
        PlanContext context,
        ChangePlanId planId,
        string surfaceId,
        List<PlannedEdit> pending,
        List<PlannedAction> actions,
        List<ChangePlanActionRecord> rows,
        List<BlockedChange> blocked)
    {
        var surface = context.Bound[surfaceId];
        var read = context.Reads[surfaceId];
        var document = read.Document!;
        var policy = surface.Surface.MergePolicy;

        var (accepted, merged) = Accept(pending, document, policy, surfaceId, blocked);
        if (merged is null)
        {
            return;
        }

        // The pre-image is the file's ACTUAL content, not document.Render(): the read path already proved the
        // two agree (see ReadUncachedAsync's fidelity guard), and taking it from the read means a future
        // change to that guard cannot quietly turn the stored pre-image into an approximation.
        var before = read.Text!;
        var body = merged.Render();

        // The BOM never reaches an adapter — it would become part of the first key's name — so the post-image
        // has to put it back. Without this, applying a plan against a BOM-carrying file would silently strip
        // three bytes the operator never saw in the diff.
        var after = read.HasByteOrderMark ? '﻿' + body : body;

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            // Every requested value is already what the file says. Not blocked — there is nothing obstructing
            // it — and not an action either, because writing identical bytes to a game server's config file
            // is a mutation with no purpose.
            return;
        }

        var maskPointers = MaskPointers(context, surface, document);
        var maskedBefore = Mask(document, document, maskPointers);
        var maskedAfter = Mask(merged, document, maskPointers);

        var diff = UnifiedDiffWriter.Write(surface.Surface.Path?.Value ?? surfaceId, maskedBefore, maskedAfter);
        var containsSecrets = maskPointers.Count > 0;

        // ResolvedPath is a required column and must hold a real path. A bound, writable surface always has
        // one — the resolver only ever produces a path for a host-file locator, and a control-channel surface
        // never resolves at all — so a null here means an invariant broke upstream, and storing the surface
        // id in a path column would hand a later apply something that looks like a path and is not.
        var resolvedPath = surface.Surface.Path?.Value
            ?? throw new InvalidOperationException(
                $"Surface '{surfaceId}' produced a write action without a resolved path. A writable surface "
                + "always resolves to a concrete TargetPath; this is a bug in surface resolution, not a "
                + "deployment fact.");

        var action = new PlannedAction(
            surface.Surface.Locator is SurfaceLocator.ControlChannel
                ? PlannedActionKind.WriteControlChannel
                : PlannedActionKind.WriteSurface,
            surfaceId,
            diff,
            // Reversible because the exact pre-image is recorded below. A revert restores those literal bytes
            // rather than inverting a diff, which is what makes the guarantee unconditional here.
            Reversible: true,
            surface.Surface.RequiredCapabilities);

        actions.Add(action);
        rows.Add(new ChangePlanActionRecord
        {
            Id = Guid.NewGuid(),
            ChangePlanId = planId,
            Ordinal = rows.Count,
            Kind = action.Kind,
            SurfaceId = surfaceId,
            ResolvedPath = resolvedPath,
            RequiredCapabilities = surface.Surface.RequiredCapabilities,
            UnifiedDiff = diff,
            Reversible = true,

            // The digest of the bytes actually read, reused rather than recomputed from `before`, so this
            // column and the plan's SurfaceHashes entry for the same surface can never disagree.
            PreImageHash = read.Hash,
            PreImageContent = before,
            PostImageContent = after,
            PostImageHash = Hash(StrictUtf8.GetBytes(after)),

            // NOTE FOR THE APPLY PHASE: when ContainsSecrets is true these two columns hold the operator's
            // real secret values in plaintext. That is deliberate and load-bearing — an exact revert needs
            // the real bytes, see ChangePlanActionRecord's own remarks — but it is only harmless while
            // nothing applies a plan. Nothing currently reads ChangePlanRecord.ExpiresAt and no purge sweep
            // exists, so these rows accumulate forever. A retention/purge path that promotes expired
            // Previewed plans to Stale and discards their images is a PREREQUISITE for shipping ApplyAsync,
            // not a follow-up to it.
            ContainsSecrets = containsSecrets,
            Status = ChangePlanActionStatus.Pending,
        });
    }

    /// <summary>
    /// Applies an edit set, returning the merged document and the edits that made it in — blocking the rest
    /// individually.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole set is merged in one pass and <em>that pass's result is what the caller uses</em>, so the
    /// happy path performs exactly one merge and Palworld's ninety-member <c>OptionSettings</c> scalar is
    /// decoded and re-encoded exactly once per preview.
    /// </para>
    /// <para>
    /// Only if that pass throws is each edit re-tried alone, purely to attribute the failure to the specific
    /// setting responsible rather than blocking a whole surface's worth of changes over one bad pointer; the
    /// surviving set is then merged once more. That fallback costs n+2 merges, which is the price of naming
    /// the culprit and is paid only on a surface that already has a problem. It is a diagnostic path, not the
    /// addressability check — that one is non-exceptional and has already run.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The accepted edits and the document they produced, or a null document when nothing could be applied
    /// and there is therefore no action to emit.
    /// </returns>
    private (List<PlannedEdit> Accepted, ConfigDocument? Merged) Accept(
        List<PlannedEdit> pending,
        ConfigDocument document,
        MergePolicy policy,
        string surfaceId,
        List<BlockedChange> blocked)
    {
        if (pending.Count == 0)
        {
            return ([], null);
        }

        if (TryMerge(document, [.. pending.Select(e => e.Edit)], policy, out var wholeSet, out _))
        {
            return (pending, wholeSet);
        }

        var accepted = new List<PlannedEdit>(pending.Count);
        foreach (var edit in pending)
        {
            if (TryMerge(document, [edit.Edit], policy, out _, out var error))
            {
                accepted.Add(edit);
                continue;
            }

            blocked.Add(new BlockedChange(
                edit.Setting.Key,
                surfaceId,
                $"Surface '{surfaceId}' refused the write to '{edit.Edit.Target.Path}': {error}",
                policy == MergePolicy.ManagedBlock
                    ? "Move the value inside the '# >>> servyx:managed >>>' … '# <<< servyx:managed <<<' "
                        + "region of the file, or change the surface's merge policy in the definition."
                    : "Correct the binding's target in the governing definition so it names a value the "
                        + "surface actually contains."));
        }

        return accepted.Count == 0
            ? (accepted, null)
            : (accepted, _merger.MergeAll(document, [.. accepted.Select(e => e.Edit)], policy));
    }

    private bool TryMerge(
        ConfigDocument document,
        IReadOnlyList<ConfigEdit> edits,
        MergePolicy policy,
        out ConfigDocument? merged,
        out string? error)
    {
        try
        {
            merged = _merger.MergeAll(document, edits, policy);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            merged = null;
            error = ex.Message;
            return false;
        }
    }

    // ── Secrets ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every pointer on this surface whose value must not appear in a diff — not merely the ones being
    /// written.
    /// </summary>
    /// <remarks>
    /// Masking only the edited secrets would be a leak with a plausible-looking justification: a unified diff
    /// carries context lines either side of each change, so editing a harmless key three lines below
    /// <c>ADMIN_PASSWORD=</c> would print the password verbatim into a persisted database column. Every
    /// sensitive binding the catalogue declares on the surface is masked, in whichever direction it is
    /// declared, before the diff is rendered.
    /// </remarks>
    private IReadOnlyList<string> MaskPointers(PlanContext context, BoundSurface surface, ConfigDocument document)
    {
        var pointers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setting in context.Settings)
        {
            foreach (var binding in setting.Bindings)
            {
                if (!string.Equals(binding.SurfaceId, surface.Surface.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!setting.IsSecret && !binding.Sensitive)
                {
                    continue;
                }

                var path = binding switch
                {
                    SettingBinding.ByKey key => Normalize(document, key.Key),
                    SettingBinding.ByPointer pointer => Normalize(document, pointer.Pointer),
                    SettingBinding.ByMember member when surface.Surface is { CodecId: { } codecId, CodecPath: { } codecPath } =>
                        $"{Normalize(document, codecPath)}#{codecId}:{member.Member}",
                    _ => null,
                };

                if (path is not null && seen.Add(path))
                {
                    pointers.Add(path);
                }
            }
        }

        return pointers;
    }

    /// <summary>
    /// Renders <paramref name="document"/> to text with every masked pointer's value replaced by a
    /// placeholder, choosing the "changed" placeholder only where the value genuinely differs from
    /// <paramref name="original"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>EVERY span for a sensitive pointer is masked, not just the one a write would edit.</strong>
    /// <see cref="ConfigDocument.WithValue"/> — and therefore <see cref="IConfigMerger"/> — deliberately edits
    /// only the LAST span registered for a pointer, matching last-wins duplicate-key semantics. That is right
    /// for a write and catastrophic for masking: a <c>.env</c> containing two <c>ADMIN_PASSWORD=</c> lines
    /// would have the last one masked and the first one printed in plaintext into a persisted database
    /// column. Duplicate keys are not exotic in hand-edited config files, so the plain-pointer pass below
    /// splices every matching span directly instead of going through the merger.
    /// </para>
    /// <para>
    /// Codec-scoped pointers still go through the merger, because masking a member packed inside a scalar
    /// means decoding and re-encoding it, which only the codec can do. Those are merged under
    /// <see cref="MergePolicy.PreserveUnknown"/>, never the surface's own policy: this is a display transform
    /// over a throwaway copy, not a write, and a secret sitting outside a
    /// <see cref="MergePolicy.ManagedBlock"/> region must still be masked rather than refused and leaked.
    /// </para>
    /// </remarks>
    private string Mask(ConfigDocument document, ConfigDocument original, IReadOnlyList<string> pointers)
    {
        if (pointers.Count == 0)
        {
            return document.Render();
        }

        var codecEdits = new List<ConfigEdit>();
        var plain = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in pointers)
        {
            if (path.IndexOf('#', StringComparison.Ordinal) < 0)
            {
                plain.Add(path);
                continue;
            }

            var current = ValueAt(document, path);
            if (current is null)
            {
                continue;
            }

            var before = ValueAt(original, path);
            codecEdits.Add(new ConfigEdit(
                new ConfigPointer(path),
                string.Equals(current, before, StringComparison.Ordinal) ? SecretMask : ChangedSecretMask));
        }

        var masked = codecEdits.Count == 0
            ? document
            : _merger.MergeAll(document, codecEdits, MergePolicy.PreserveUnknown);

        return plain.Count == 0 ? masked.Render() : SpliceEverySpan(masked, original, plain);
    }

    /// <summary>
    /// Rewrites every span whose pointer is in <paramref name="paths"/> to a mask placeholder, in place, and
    /// renders the result.
    /// </summary>
    /// <remarks>
    /// Spans are compared position-for-position against <paramref name="original"/> by index rather than by
    /// pointer: <see cref="ConfigDocument.WithValue"/> maps its span list one-to-one, so index <c>i</c> names
    /// the same value in both documents, and comparing there decides per occurrence whether a duplicate key's
    /// individual line actually changed. Splices are applied right-to-left within each line so an earlier
    /// span's offsets stay valid while a later one on the same line is being replaced.
    /// </remarks>
    private static string SpliceEverySpan(ConfigDocument document, ConfigDocument original, HashSet<string> paths)
    {
        var lines = document.RawLines.ToArray();
        var targets = new List<(int Line, int Start, int Length, string Mask)>();

        for (var i = 0; i < document.Spans.Count; i++)
        {
            var span = document.Spans[i];
            if (!paths.Contains(span.Pointer.Path))
            {
                continue;
            }

            var current = SliceSpan(document, span);
            if (current is null)
            {
                continue;
            }

            var before = i < original.Spans.Count ? SliceSpan(original, original.Spans[i]) : null;
            targets.Add((
                span.LineIndex,
                span.ValueStart,
                span.ValueLength,
                string.Equals(current, before, StringComparison.Ordinal) ? SecretMask : ChangedSecretMask));
        }

        foreach (var group in targets.GroupBy(t => t.Line))
        {
            var line = lines[group.Key];
            foreach (var (_, start, length, mask) in group.OrderByDescending(t => t.Start))
            {
                line = string.Concat(line.AsSpan(0, start), mask, line.AsSpan(start + length));
            }

            lines[group.Key] = line;
        }

        // Spans are stale after this rewrite, which is fine: the result is rendered immediately and never
        // edited again. Nothing downstream of a masked document does anything but read its text.
        return (document with { RawLines = lines }).Render();
    }

    private static string? SliceSpan(ConfigDocument document, ConfigSpan span)
    {
        if (span.LineIndex < 0 || span.LineIndex >= document.RawLines.Count)
        {
            return null;
        }

        var line = document.RawLines[span.LineIndex];
        return span.ValueStart < 0 || span.ValueLength < 0 || span.ValueStart + span.ValueLength > line.Length
            ? null
            : line.Substring(span.ValueStart, span.ValueLength);
    }

    /// <summary>Reads a value through either a plain span or a codec member, matching how it will be written.</summary>
    private string? ValueAt(ConfigDocument document, string path)
    {
        var hash = path.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return Slice(document, path);
        }

        var remainder = path[(hash + 1)..];
        var colon = remainder.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return Slice(document, path);
        }

        var scalar = Slice(document, path[..hash]);
        if (scalar is null || !_codecs.TryGetValue(remainder[..colon], out var codec))
        {
            return null;
        }

        try
        {
            return codec.Decode(scalar).TryGetValue(remainder[(colon + 1)..], out var value) ? value : null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            // A scalar the codec cannot decode has no member to mask. Reporting it here would be noise: the
            // same failure already blocked every edit that tried to address a member inside it.
            return null;
        }
    }

    // ── Consequences ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks <see cref="DeclaredConfigSurface.DerivedFrom"/> downstream from every written surface and turns
    /// each regeneration trigger it reaches into a consequence.
    /// </summary>
    /// <remarks>
    /// Breadth-first with an explicit queue and a visited set: transitive by construction (Palworld's
    /// <c>live</c> is two hops below its <c>.env</c>), iterative so a long chain cannot exhaust the stack, and
    /// terminating even for a malformed graph. Cycles are detected separately and reported rather than
    /// silently absorbed — see <see cref="FindCycle"/>.
    /// </remarks>
    private List<Consequence> DeriveConsequences(
        PlanContext context,
        IReadOnlyList<string> written,
        List<PlanDiagnostic> diagnostics)
    {
        var downstream = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var surface in context.Declared)
        {
            foreach (var upstream in surface.DerivedFrom)
            {
                if (!downstream.TryGetValue(upstream, out var children))
                {
                    children = [];
                    downstream[upstream] = children;
                }

                children.Add(surface.Id);
            }
        }

        if (written.Count > 0 && FindCycle(context.Declared, downstream) is { Count: > 0 } cycle)
        {
            var named = string.Join(" -> ", cycle.Select(id => $"'{id}'"));
            diagnostics.Add(new PlanDiagnostic(
                PlanDiagnosticKind.DefinitionDefect,
                cycle[0],
                $"The governing definition's 'derivedFrom' graph contains a cycle ({named}). Consequences "
                + "were still derived — the cyclic edge was ignored, and every surface reachable without it "
                + "was visited — but a surface cannot be generated from something generated from itself, so "
                + "the regeneration consequences shown here may be incomplete. This is a defect in the "
                + "definition, not in this server."));

            _logger?.LogWarning(
                "The 'derivedFrom' graph of the definition governing this change plan contains a cycle "
                + "({Cycle}). The cyclic edge was ignored so consequence derivation could terminate; the "
                + "regeneration consequences on the plan may be incomplete until the definition is fixed.",
                named);
        }

        var declaredById = context.Declared.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var consequences = new List<Consequence>();
        var visited = new HashSet<string>(written, StringComparer.Ordinal);
        var queue = new Queue<string>(written);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!downstream.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!visited.Add(child))
                {
                    continue;
                }

                queue.Enqueue(child);

                if (!declaredById.TryGetValue(child, out var declaration) || declaration.Regeneration is not { } trigger)
                {
                    continue;
                }

                switch (trigger.Kind)
                {
                    case RegenerationKind.ContainerRestart:
                    case RegenerationKind.ProcessRestart:
                        consequences.Add(new Consequence(ConsequenceKind.RestartRequired, trigger.Description));
                        break;

                    case RegenerationKind.Manual:
                        // Deliberately a diagnostic and not a Consequence. RestartRequired would be a lie (no
                        // restart regenerates a manual surface) and ServiceInterruption would be a different
                        // lie (nothing is interrupted). Silence would be the worst of the three: it reads as
                        // "this change takes effect as soon as it is applied", which is exactly what a manual
                        // trigger means it will not do.
                        diagnostics.Add(new PlanDiagnostic(
                            PlanDiagnosticKind.ManualRegenerationRequired,
                            child,
                            $"Surface '{child}' is regenerated only by a manual, operator-triggered action "
                            + $"({trigger.Description}). Applying this plan writes the upstream surface, but "
                            + "the change will not reach the running workload until that surface is "
                            + "regenerated by hand — no restart will do it."));
                        break;

                    default:
                        break;
                }
            }
        }

        return Dedupe(consequences);
    }

    /// <summary>
    /// Finds one cycle in the <c>derivedFrom</c> graph, or <see langword="null"/> when there is none.
    /// </summary>
    /// <remarks>
    /// A plain visited set cannot answer this: revisiting a node is perfectly normal in a diamond (two
    /// surfaces both derived from <c>env</c>, a third derived from both), and reporting that as a definition
    /// defect would cry wolf. This is an iterative depth-first walk with three-colour marking — a node is
    /// on the current path, finished, or unseen — so only a genuine back edge is reported. Iterative rather
    /// than recursive for the same reason the consequence walk is: a definition is semi-trusted input and
    /// must not be able to overflow the stack.
    /// </remarks>
    private static List<string>? FindCycle(
        IReadOnlyList<DeclaredConfigSurface> declared,
        IReadOnlyDictionary<string, List<string>> downstream)
    {
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();

        // A cursor index per frame rather than an IEnumerator: the adjacency values are already indexable
        // lists, so this avoids holding a stack of disposable enumerators that an early return (finding a
        // cycle) would abandon undisposed.
        var stack = new List<(string Id, int Next)>();

        foreach (var root in declared)
        {
            if (finished.Contains(root.Id))
            {
                continue;
            }

            stack.Clear();
            stack.Add((root.Id, 0));
            onPath.Add(root.Id);
            path.Add(root.Id);

            while (stack.Count > 0)
            {
                var (id, next) = stack[^1];
                var children = downstream.TryGetValue(id, out var list) ? list : [];

                if (next >= children.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    onPath.Remove(id);
                    path.RemoveAt(path.Count - 1);
                    finished.Add(id);
                    continue;
                }

                stack[^1] = (id, next + 1);

                var child = children[next];
                if (onPath.Contains(child))
                {
                    var cycle = path[path.IndexOf(child)..];
                    cycle.Add(child);
                    return cycle;
                }

                if (finished.Contains(child))
                {
                    continue;
                }

                stack.Add((child, 0));
                onPath.Add(child);
                path.Add(child);
            }
        }

        return null;
    }

    private static List<Consequence> Dedupe(IEnumerable<Consequence> consequences)
    {
        var seen = new HashSet<(ConsequenceKind, string)>();
        var result = new List<Consequence>();
        foreach (var consequence in consequences)
        {
            if (seen.Add((consequence.Kind, consequence.Description)))
            {
                result.Add(consequence);
            }
        }

        return result;
    }

    // ── Reading ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads, hashes, parses and (when a codec is declared) decodes one surface — once per preview, however
    /// many settings live on it.
    /// </summary>
    private async Task<SurfaceRead> ReadAsync(PlanContext context, BoundSurface surface, CancellationToken ct)
    {
        if (context.Reads.TryGetValue(surface.Surface.Id, out var cached))
        {
            return cached;
        }

        var read = await ReadUncachedAsync(context, surface, ct).ConfigureAwait(false);
        context.Reads[surface.Surface.Id] = read;
        return read;
    }

    private async Task<SurfaceRead> ReadUncachedAsync(PlanContext context, BoundSurface surface, CancellationToken ct)
    {
        if (surface.Surface.Path is not { } path)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' resolved without a concrete path, so there is nothing to read.",
                "Check the definition's locator for this surface, and that the session it resolves on has a "
                + "root path. A surface with no path was never bound to a filesystem.");
        }

        if (!_adapters.TryGetValue(surface.Surface.FormatId, out var adapter))
        {
            return SurfaceRead.Failed(
                $"No IConfigAdapter is registered for format '{surface.Surface.FormatId}', so surface "
                + $"'{surface.Surface.Id}' cannot be parsed, let alone rendered back after an edit.",
                $"Register an IConfigAdapter whose FormatId is '{surface.Surface.FormatId}' — "
                + "AddServyxConfig() is where the built-in adapters are wired.");
        }

        // RAW BYTES, never a StreamReader. A StreamReader with BOM detection silently consumes a UTF-8 BOM
        // and transcodes anything else, so the text it hands back is not what is on disk — and a post-image
        // rendered from it would drop the BOM on write, a change the approved diff never showed because both
        // of its sides came from the same already-normalized text.
        byte[] rawBytes;
        try
        {
            var stream = await surface.Session.Target.OpenReadAsync(path, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                rawBytes = buffer.ToArray();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' could not be read via "
                + $"{surface.Session.Description}: {ex.Message}",

                // The one case where "make it readable" is the correct advice — which is exactly why it must
                // not be the blanket hint for every other failure in this method.
                $"Make '{path.Value}' readable through {surface.Session.Description}, then preview again. A "
                + "change plan records the exact pre-image it will overwrite, so there is no safe way to "
                + "plan an edit to a file that cannot be read.");
        }

        // Recorded for every surface actually opened, whether or not it ends up carrying an action: apply
        // must be able to tell that ANY bound surface drifted since preview, not merely the ones being
        // written, because a read surface is what several of the planned values were validated against.
        var hash = Hash(rawBytes);
        context.Hashes[surface.Surface.Id] = hash;

        if (ForeignByteOrderMark(rawBytes) is { } foreign)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' begins with a {foreign} byte-order mark. "
                + "Servyx models a configuration surface's content as UTF-8 text, so re-encoding this file "
                + "would rewrite every byte in it — a change no diff would show and no operator approved.",
                $"Re-save '{path.Value}' as UTF-8 (with or without a byte-order mark — both are supported) "
                + "and preview again. Most editors offer this as 'Save with Encoding'. The file reads "
                + "correctly as it stands; it is only rewriting it that Servyx will not do.");
        }

        var hasByteOrderMark = HasUtf8ByteOrderMark(rawBytes);

        string text;
        try
        {
            // Strict, not the replacing default. The replacement fallback would substitute U+FFFD for every
            // invalid sequence, and the post-image re-encoded from that text would overwrite real bytes with
            // question marks. Strict decoding also makes the round trip exact by construction: for input this
            // accepts, re-encoding the result reproduces the original bytes.
            text = StrictUtf8.GetString(rawBytes);
        }
        catch (DecoderFallbackException ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' is not valid UTF-8 ({ex.Message}), so its "
                + "content cannot be represented as text without corrupting it.",
                $"Repair the invalid byte sequence in '{path.Value}' and re-save it as UTF-8, then preview "
                + "again. Servyx will not guess a replacement character for it: doing so would overwrite a "
                + "real byte in the file with a question mark.");
        }

        var body = hasByteOrderMark ? text[1..] : text;

        ConfigDocument document;
        try
        {
            document = adapter.Parse(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' is not valid {surface.Surface.FormatId}: {ex.Message}",
                $"Correct the {surface.Surface.FormatId} syntax in '{path.Value}' and preview again. Servyx "
                + "will not edit a file it cannot parse, because it could not tell which bytes it was "
                + "replacing.");
        }

        // THE FIDELITY GUARD. IConfigAdapter's contract is Render(Parse(x)) == x byte-for-byte, but
        // ConfigDocument.Render joins RawLines with a single dominant line ending, so a file with MIXED line
        // endings renders back normalized — documented on ConfigDocument.LineEnding as a deliberate choice.
        // Applying a post-image derived from such a render would rewrite every line terminator in the file
        // alongside the one value the operator approved. Refusing is the only honest answer: a silent
        // whole-file reformat is exactly the class of write this engine exists to prevent.
        //
        // A CONSISTENT convention is NOT caught here, and must never become so. An all-CRLF file (the normal
        // Windows case) parses with LineEnding = "\r\n" and renders back identical, so it passes and stays
        // fully manageable; the same holds for all-LF. Only a genuinely mixed file fails. Over-refusal would
        // be its own outage — a Windows operator whose .env is simply CRLF being told Servyx cannot manage
        // it — so PreviewAsync_ForACrlfThroughoutFile_IsNotRefused pins that boundary explicitly rather than
        // leaving it to be inferred from the byte-fidelity theory happening to pass.
        //
        // Deliberately a general "does this adapter reproduce this file" check rather than a line-ending
        // check specifically: it then fails safe against any adapter fidelity gap, not only the one known
        // when it was written.
        var roundTrip = document.Render();
        if (!string.Equals(roundTrip, body, StringComparison.Ordinal))
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}' at '{path.Value}' does not survive a parse/render round trip "
                + $"byte-for-byte with the {surface.Surface.FormatId} adapter — most commonly because the file "
                + "mixes LF and CRLF line endings, which render back normalized to whichever dominates. "
                + "Writing it would silently reformat lines nobody asked to change.",
                $"Normalize '{path.Value}' to a single line-ending convention — all LF or all CRLF, either "
                + "is fully supported — and preview again. Most editors show the current convention in the "
                + "status bar and can convert the whole file in one action. The file reads correctly as it "
                + "stands; Servyx is declining only to rewrite it.");
        }

        if (surface.Surface.CodecId is not { } codecId)
        {
            return SurfaceRead.Parsed(document, null, text, hasByteOrderMark, hash);
        }

        if (!_codecs.TryGetValue(codecId, out var codec) || surface.Surface.CodecPath is not { } codecPath)
        {
            // Reported per binding by Pointer(...), which can name the setting affected. Here the document is
            // still perfectly usable for any non-codec binding on the same surface.
            return SurfaceRead.Parsed(document, null, text, hasByteOrderMark, hash);
        }

        var scalar = Slice(document, Normalize(document, codecPath));
        if (scalar is null)
        {
            return SurfaceRead.Parsed(document, null, text, hasByteOrderMark, hash);
        }

        try
        {
            return SurfaceRead.Parsed(document, codec.Decode(scalar), text, hasByteOrderMark, hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SurfaceRead.Failed(
                $"Surface '{surface.Surface.Id}''s '{codecId}' codec could not decode the scalar at "
                + $"'{codecPath}': {ex.Message}",
                $"Repair the '{codecPath}' value in '{path.Value}' so the '{codecId}' codec can read it, or "
                + "start the workload once so it rewrites the scalar itself. Servyx edits a member inside "
                + "that scalar by decoding and re-encoding the whole thing, which it cannot do here.");
        }
    }

    // ── Persistence ────────────────────────────────────────────────────────────────────────────────────

    private async Task PersistAsync(
        ChangePlanId planId,
        ServerId serverId,
        ServerPlanCatalog catalog,
        IReadOnlyList<Consequence> consequences,
        IReadOnlyDictionary<string, string> surfaceHashes,
        IReadOnlyList<BlockedChange> blocked,
        IReadOnlyList<PlanDiagnostic> diagnostics,
        IReadOnlyList<ChangePlanActionRecord> actions,
        CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        var record = new ChangePlanRecord
        {
            Id = planId,
            ServerId = serverId,
            Status = ChangePlanStatus.Previewed,
            CreatedAt = now,
            CreatedBy = _actor,

            // From the injected TimeProvider plus the entity's own declared TTL — never DateTimeOffset.UtcNow,
            // and never a locally re-invented interval.
            ExpiresAt = now + ChangePlanRecord.DefaultTtl,
            DefinitionId = catalog.DefinitionId,
            DefinitionVersion = catalog.DefinitionVersion,
            ConsequencesJson = JsonSerializer.Serialize(consequences, JsonOptions),
            SurfaceHashesJson = JsonSerializer.Serialize(surfaceHashes, JsonOptions),
            BlockedJson = JsonSerializer.Serialize(blocked, JsonOptions),
            DiagnosticsJson = JsonSerializer.Serialize(diagnostics, JsonOptions),
        };

        await _store.SaveAsync(record, actions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// How the plan's opaque JSON payloads are serialized.
    /// </summary>
    /// <remarks>
    /// Enums are written BY NAME, not by ordinal, matching the by-name convention every enum column in this
    /// schema already follows (see <c>ChangePlanRecordConfiguration.Status</c>'s own remarks). These payloads
    /// are durable rows an operator reads while diagnosing a failed apply, and an integer whose meaning
    /// depends on the declaration order of <see cref="ConsequenceKind"/> or <see cref="PlanDiagnosticKind"/>
    /// would be silently re-pointed at a different member the first time someone inserts an enum value in
    /// the middle.
    /// </remarks>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Pointers and hashing ───────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the document registered a writable span for <paramref name="path"/>.</summary>
    private static bool HasSpan(ConfigDocument document, string path)
    {
        for (var i = document.Spans.Count - 1; i >= 0; i--)
        {
            if (string.Equals(document.Spans[i].Pointer.Path, path, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the exact span path a definition-authored pointer names, accounting for a quoting difference
    /// the two spell differently.
    /// </summary>
    /// <remarks>
    /// Definition authors quote an INI section name inside a <c>codecPath</c> —
    /// <c>["/Script/…"].OptionSettings</c> — while <c>IniConfigAdapter</c>'s own pointers carry the section
    /// name bare. Both name the same value, and <c>SettingStateResolver.ValueAt</c> already normalizes the
    /// same way on read. A write must resolve to the identical span the read did, or the plan would edit a
    /// different value from the one it displayed.
    /// </remarks>
    private static string Normalize(ConfigDocument document, string path)
    {
        if (HasSpan(document, path))
        {
            return path;
        }

        var normalized = Unquoted(path);
        for (var i = document.Spans.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Unquoted(document.Spans[i].Pointer.Path), normalized, StringComparison.Ordinal))
            {
                return document.Spans[i].Pointer.Path;
            }
        }

        return path;
    }

    private static string Unquoted(string path) => path
        .Replace("\"", string.Empty, StringComparison.Ordinal)
        .Replace("'", string.Empty, StringComparison.Ordinal);

    private static string? Slice(ConfigDocument document, string path)
    {
        for (var i = document.Spans.Count - 1; i >= 0; i--)
        {
            var span = document.Spans[i];
            if (!string.Equals(span.Pointer.Path, path, StringComparison.Ordinal))
            {
                continue;
            }

            if (span.LineIndex < 0 || span.LineIndex >= document.RawLines.Count)
            {
                return null;
            }

            var line = document.RawLines[span.LineIndex];
            return span.ValueStart < 0 || span.ValueLength < 0 || span.ValueStart + span.ValueLength > line.Length
                ? null
                : line.Substring(span.ValueStart, span.ValueLength);
        }

        return null;
    }

    /// <summary>
    /// SHA-256 of a surface's RAW bytes, as bare lower-case hex.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The format is dictated by the transports, not chosen here.</strong> Every
    /// <see cref="IExecutionTarget"/> implementation computes a file digest as a bare, unprefixed, lower-case
    /// hex SHA-256 over the bytes it read or wrote, and compares them with an ordinal-ignore-case string
    /// equality. A <c>"sha256:"</c> prefix, or a digest taken over decoded text rather than bytes, would make
    /// a persisted <see cref="ChangePlanActionRecord.PreImageHash"/> fail to match the transport's own
    /// pre-image check on every single file — permanently, and worst on the BOM-carrying files where the two
    /// domains diverge most.
    /// </para>
    /// <para>
    /// <strong>Bytes, never text.</strong> Hashing decoded text silently normalizes away a BOM and any
    /// encoding detail, so two genuinely different files can hash the same and one file can hash two
    /// different ways depending on which code path read it. The whole point of this digest is to answer "is
    /// the file still exactly what preview saw", which only a hash of the bytes on disk can do.
    /// </para>
    /// </remarks>
    private static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>UTF-8 that refuses invalid input rather than substituting U+FFFD, and never emits a BOM of its own.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    private static bool HasUtf8ByteOrderMark(ReadOnlySpan<byte> content) =>
        content.StartsWith(Utf8ByteOrderMark);

    /// <summary>
    /// Names the byte-order mark of an encoding this engine will not rewrite, or <see langword="null"/> when
    /// the content is UTF-8 (with or without a BOM).
    /// </summary>
    /// <remarks>
    /// UTF-32's little-endian BOM starts with the same two bytes as UTF-16's, so it must be tested first or
    /// a UTF-32 file would be misreported as UTF-16.
    /// </remarks>
    private static string? ForeignByteOrderMark(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00]))
        {
            return "UTF-32 little-endian";
        }

        if (content.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF]))
        {
            return "UTF-32 big-endian";
        }

        if (content.StartsWith<byte>([0xFF, 0xFE]))
        {
            return "UTF-16 little-endian";
        }

        return content.StartsWith<byte>([0xFE, 0xFF]) ? "UTF-16 big-endian" : null;
    }

    // ── Local state ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Everything one <see cref="PreviewAsync"/> call resolves once and reuses.</summary>
    private sealed class PlanContext(
        IReadOnlyList<DeclaredConfigSurface> declared,
        IReadOnlyDictionary<string, BoundSurface> bound,
        IReadOnlyDictionary<string, SurfaceResolutionFailure> failures,
        IReadOnlyList<SettingDescriptor> settings)
    {
        public IReadOnlyList<DeclaredConfigSurface> Declared { get; } = declared;

        public IReadOnlyDictionary<string, BoundSurface> Bound { get; } = bound;

        public IReadOnlyDictionary<string, SurfaceResolutionFailure> Failures { get; } = failures;

        /// <summary>
        /// The whole settings catalogue, not merely the settings being written. Masking needs every
        /// sensitive binding declared on a surface, including ones this plan does not touch — a diff's
        /// context lines print them just as readably as its changed lines do.
        /// </summary>
        public IReadOnlyList<SettingDescriptor> Settings { get; } = settings;

        public Dictionary<string, SurfaceRead> Reads { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Hashes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One surface's parse result, or the reason it has none.</summary>
    /// <param name="Document">The parsed document, or null when <paramref name="Error"/> is set.</param>
    /// <param name="Members">The codec-decoded members, when the surface declares a codec.</param>
    /// <param name="Error">Why this surface could not be read or safely rewritten. Null on success.</param>
    /// <param name="Text">
    /// The file's exact content as text, <em>including</em> a leading U+FEFF when the file carries a UTF-8
    /// byte-order mark. This — not <see cref="ConfigDocument.Render"/>'s output — is what gets persisted as
    /// the pre-image, so a revert restores the real file rather than a normalized approximation of it.
    /// </param>
    /// <param name="HasByteOrderMark">
    /// Whether the file began with a UTF-8 BOM. Carried separately so the post-image can re-attach it: the
    /// adapters never see the BOM (it would become part of the first key's name) and would otherwise drop it
    /// on write.
    /// </param>
    /// <param name="Hash">The digest of the file's raw bytes — see <see cref="PlanExecutor.Hash"/>.</param>
    /// <param name="Hint">
    /// What an operator should actually do about <paramref name="Error"/>. Carried per failure rather than
    /// supplied once by the caller because these failures are not all the same kind of problem: a file that
    /// cannot be opened and a file that opened, parsed and validated fine but cannot be rewritten
    /// byte-faithfully need opposite advice, and a single shared hint necessarily misdescribes one of them.
    /// </param>
    private sealed record SurfaceRead(
        ConfigDocument? Document,
        IReadOnlyDictionary<string, string>? Members,
        string? Error,
        string? Text = null,
        bool HasByteOrderMark = false,
        string? Hash = null,
        string? Hint = null)
    {
        public static SurfaceRead Failed(string error, string hint) =>
            new(Document: null, Members: null, error, Hint: hint);

        public static SurfaceRead Parsed(
            ConfigDocument document,
            IReadOnlyDictionary<string, string>? members,
            string text,
            bool hasByteOrderMark,
            string hash) => new(document, members, Error: null, text, hasByteOrderMark, hash);
    }

    /// <summary>One accepted edit, kept alongside the setting and binding it came from so masking and blocking can name them.</summary>
    private sealed record PlannedEdit(SettingDescriptor Setting, SettingBinding Binding, ConfigPointer Pointer, string Value)
    {
        public ConfigEdit Edit { get; } = new(Pointer, Value);
    }

    /// <summary>Either a pending edit or the refusal that replaced it.</summary>
    private sealed record BindingOutcome(PlannedEdit? Edit, BlockedChange? Blocked)
    {
        public static BindingOutcome Planned(PlannedEdit edit) => new(edit, null);

        public static BindingOutcome Refused(string key, string surfaceId, string reason, string hint) =>
            new(null, new BlockedChange(key, surfaceId, reason, hint));
    }

    /// <summary>Where a binding writes, or why it cannot be addressed at all.</summary>
    private sealed record PointerResolution(string? EditPath, string? SpanPath, (string Reason, string Hint)? Refusal)
    {
        public static PointerResolution At(string editPath, string spanPath) => new(editPath, spanPath, null);

        public static PointerResolution Refuse(string reason, string hint) => new(null, null, (reason, hint));
    }
}
