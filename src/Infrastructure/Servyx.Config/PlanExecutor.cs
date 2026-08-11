using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;
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
    private readonly IServerRepository? _servers;

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
    /// <param name="servers">
    /// Resolves a stored plan's <see cref="ChangePlanRecord.ServerId"/> back to the container id every other
    /// dependency here is keyed by, which is what <see cref="ApplyAsync"/> needs and
    /// <see cref="PreviewAsync"/> does not (preview is handed the container id directly). Deliberately
    /// <see cref="IServerRepository"/> and not <c>IServerQueryService</c> — see this type's own remarks for
    /// the re-entrancy deadlock that rule exists to prevent; this repository is a leaf over Servyx's own
    /// database and cannot route back into the settings pipeline. Optional only so preview-only compositions
    /// and preview-only tests need not supply one; <see cref="ApplyAsync"/> refuses loudly without it.
    /// </param>
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
        string? actor = null,
        IServerRepository? servers = null)
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
        _servers = servers;
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

    // ── Apply ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The operation name <see cref="RequireWritesEnabled"/> phrases its refusal around.</summary>
    private const string ApplyOperation = "apply a configuration change";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Refusal happens before any side effect, deliberately and in that order.</strong> Write mode,
    /// plan status, expiry, control-channel scope, definition identity and surface drift are all checked
    /// while nothing has been written; only then is the plan claimed and the first byte sent. The one
    /// departure from "write mode first" is that the plan must be read out of the database before the server
    /// it targets — and therefore the session whose posture is being asked about — is even known. That read
    /// touches Servyx's own storage only.
    /// </para>
    /// <para>
    /// <strong>Two layers of staleness, not one.</strong> The pre-flight sweep re-reads EVERY bound surface
    /// the plan recorded a hash for — not merely the ones being written — and raises
    /// <see cref="PlanStaleException"/> before a single write is attempted, because several planned values
    /// were validated against surfaces this plan does not touch. Each individual write then carries the
    /// action's recorded <see cref="ChangePlanActionRecord.PreImageHash"/> as
    /// <see cref="FileWriteOptions.ExpectedPreImageHash"/>, which is the TOCTOU backstop for drift arriving
    /// between the sweep and that specific write; the transport refuses with
    /// <see cref="TargetDriftException"/> before touching the file.
    /// </para>
    /// <para>
    /// <strong>Exactly the previewed bytes are written.</strong> The post-image was rendered once, at
    /// preview, and is written verbatim from <see cref="ChangePlanActionRecord.PostImageContent"/>. Nothing
    /// is re-derived from the desired values here, so there is no way for what an operator approved and what
    /// reaches the disk to differ.
    /// </para>
    /// <para>
    /// <strong>Every write is <see cref="FileWriteStrategy.AtomicRename"/>, with no per-write branching, and
    /// that is a decision rather than an oversight.</strong> Three things force it. It is the only strategy
    /// more than one transport implements — <c>SftpFileChannel</c>, <c>ShellFileChannel</c> and
    /// <c>LocalExecutionTarget</c> all call
    /// <see cref="FileWriteOptions.ThrowIfBeyondPlainAtomicRename"/> and refuse anything else, and a
    /// <c>${COMPOSE_DIR}</c> surface routes over exactly those. It is the only correct strategy against a
    /// workload that is running, which is what a server under management normally is;
    /// <see cref="FileWriteStrategy.DirectPlacement"/> is explicitly non-atomic and a reader racing it can
    /// observe a partial file. And selecting <see cref="FileWriteStrategy.DirectPlacement"/> honestly would
    /// require knowing the container is NOT running, a fact no dependency available here reports —
    /// <see cref="IContainerLifecycle"/> is mutation-only by design and <c>IServerQueryService</c> is
    /// forbidden to this type. Guessing is the one thing <see cref="FileWriteStrategy"/>'s own contract
    /// forbids. The failure mode of this choice is loud and non-destructive: against a stopped container the
    /// finalizing rename fails, the transport removes its temporary sibling, and the target file is
    /// unchanged. A future phase that plumbs a read-only run-state fact in below
    /// <c>IServerQueryService</c> can revisit this; until one exists, there is nothing to branch on.
    /// </para>
    /// <para>
    /// <strong>Nothing is restarted or recreated.</strong> A plan carrying
    /// <see cref="ConsequenceKind.RestartRequired"/> or <see cref="ConsequenceKind.RecreateRequired"/> still
    /// has its file writes applied here, and the consequence is NOT acted on: this method never starts,
    /// stops, restarts or recreates a container or process. The returned <see cref="ChangeReceipt"/>
    /// therefore means "the bytes are on disk", not "the running workload has picked them up" — the operator
    /// (or a later phase) still has to perform the restart the plan's consequences named.
    /// </para>
    /// <para>
    /// <strong>Partial application is recorded, never hidden.</strong> If a write fails partway through, the
    /// plan lands in <see cref="ChangePlanStatus.PartiallyApplied"/> (or
    /// <see cref="ChangePlanStatus.Failed"/> when nothing at all landed — the honest reading of that member's
    /// own definition), each action carries its own outcome, and the exception propagates. A
    /// partially-applied plan can never read as a fully applied one, because
    /// <see cref="ChangePlanStatus.Applied"/> is only ever written after every action reported success.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="planId"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="planId"/> is not a plan id, names no stored plan, names a plan that is not
    /// <see cref="ChangePlanStatus.Previewed"/>, names a plan whose server is no longer tracked, or the plan
    /// contains an action this phase cannot carry out.
    /// </exception>
    /// <exception cref="PlanStaleException">
    /// The plan expired, its governing definition changed underneath it, or a bound surface drifted — either
    /// during the pre-flight sweep (before any write) or at an individual write's own pre-image check.
    /// </exception>
    /// <exception cref="WritesDisabledException">
    /// The server's write mode is not <see cref="WriteMode.Enabled"/>. Raised up front for the whole plan,
    /// and possible again mid-plan if the grant is revoked while the apply is running — see
    /// <c>WriteGuardedExecutionTarget</c>, which re-resolves the grant per call by design.
    /// </exception>
    /// <exception cref="ChangePlanConcurrencyException">
    /// Another attempt claimed this plan first. The double-apply guard.
    /// </exception>
    /// <exception cref="PlanApplyFidelityException">
    /// Raised from two places with very different force. Before anything is written, a stored action whose
    /// post-image content and recorded digest disagree. After a write, either the transport's own receipt
    /// disagreeing with the approved digest (which attests only that the transport agrees about the bytes it
    /// was handed) or — the one that speaks to the file itself — a read-back finding different content on the
    /// server. In the post-write cases the write already happened and is deliberately NOT undone or retried;
    /// the action is Failed carrying both digests and the plan is left
    /// <see cref="ChangePlanStatus.PartiallyApplied"/>.
    /// </exception>
    public async Task<ChangeReceipt> ApplyAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!ChangePlanId.TryParse(planId, out var id))
        {
            throw new InvalidOperationException(
                $"'{planId}' is not a change plan identifier, so there is no plan to apply.");
        }

        var stored = await _store.TryGetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No change plan '{planId}' is stored. A plan id can outlive its row — forgetting a server "
                + "discards its plans — so preview the change again to get a fresh plan.");

        var plan = stored.Plan;
        var actions = stored.Actions;

        // A plan is applicable exactly once, out of exactly one state. Anything else — already applied,
        // mid-flight, stale, superseded — is refused here; the RowVersion claim further down is what makes
        // this check race-proof rather than merely usually right.
        if (plan.Status != ChangePlanStatus.Previewed)
        {
            throw new InvalidOperationException(
                $"Change plan '{planId}' is {plan.Status}, not {ChangePlanStatus.Previewed}, so it cannot be "
                + "applied. A plan is applicable exactly once; preview the change again to get a fresh plan.");
        }

        var now = _time.GetUtcNow();
        if (plan.ExpiresAt <= now)
        {
            // Security-relevant, not housekeeping: an approval an operator gave fifteen minutes ago was given
            // against a picture of the server that is no longer being verified. Recording Stale durably is
            // what stops a browser tab left open since before the expiry from ever becoming applicable again.
            await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

            throw new PlanStaleException(
                $"Change plan '{planId}' expired at {plan.ExpiresAt:u} and it is now {now:u}, so it can no "
                + "longer be applied. It has been marked stale. Preview the change again to get a fresh plan.",
                planId);
        }

        // Out of scope for this phase, and refused for the WHOLE plan rather than skipped within it: applying
        // the file half of a plan whose control-channel half silently did not happen would leave the server
        // in a state no operator approved and no diff described.
        if (actions.Any(a => a.Kind == PlannedActionKind.WriteControlChannel))
        {
            var offending = actions
                .Where(a => a.Kind == PlannedActionKind.WriteControlChannel)
                .Select(a => $"#{a.Ordinal} ('{a.SurfaceId}')");

            throw new InvalidOperationException(
                $"Change plan '{planId}' contains control-channel action(s) {string.Join(", ", offending)}, "
                + "which Servyx cannot yet carry out. The whole plan is refused and NOTHING was written: "
                + "applying only its file actions would leave the server half-changed in a way the approved "
                + "diff never described. Apply the file-only part as a separate plan, or wait for "
                + "control-channel support.");
        }

        var servers = _servers
            ?? throw new InvalidOperationException(
                $"This {nameof(PlanExecutor)} was constructed without an {nameof(IServerRepository)}, so a "
                + "stored plan's server cannot be resolved back to the container id its sessions are keyed "
                + "by. Applying a plan is unavailable in this composition; previewing one is not.");

        var server = await servers.TryGetAsync(plan.ServerId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Change plan '{planId}' targets a server Servyx no longer tracks, so there is nothing to "
                + "apply it to. Nothing was written.");

        var serverId = server.ContainerId;

        var sessions = await _sessions.GetAsync(serverId, ct).ConfigureAwait(false);
        if (sessions is null || sessions.Sessions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No configuration session is open for server '{serverId}', so change plan '{planId}' cannot "
                + "be applied. Nothing was written.");
        }

        // THE WRITE-MODE GATE, ahead of every side effect — the house idiom, for the reason SshBackupProvider
        // states at its own call site: the guard on each session would refuse the first write anyway, but by
        // then a plan could already be half-applied, and the operator would read a failure about a file
        // instead of about the server's posture. Every session is checked, not only the ones this plan
        // happens to write through, because the posture is one per-server fact and a plan that can only be
        // half-permitted must not start.
        foreach (var session in sessions.Sessions)
        {
            RequireWritesEnabled(session.Target, serverId);
        }

        var catalog = await _catalogs.GetAsync(serverId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No game definition governs server '{serverId}' any more, so change plan '{planId}' cannot "
                + "be verified against the catalogue it was planned from. Nothing was written.");

        if (!string.Equals(catalog.DefinitionId, plan.DefinitionId, StringComparison.Ordinal)
            || !string.Equals(catalog.DefinitionVersion, plan.DefinitionVersion, StringComparison.Ordinal))
        {
            await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

            throw new PlanStaleException(
                $"Change plan '{planId}' was planned against definition '{plan.DefinitionId}' at version "
                + $"'{plan.DefinitionVersion}', but server '{serverId}' is now governed by "
                + $"'{catalog.DefinitionId}' at '{catalog.DefinitionVersion}'. The rules the plan was built "
                + "from changed underneath it, so it has been marked stale and nothing was written.",
                planId);
        }

        // Surfaces are re-resolved, never taken from ChangePlanActionRecord.ResolvedPath: an IExecutionTarget
        // is a live connection that cannot be persisted, and the stored path names no session. The stored
        // path is the cross-check on this resolution, applied in the pre-flight sweep below.
        var context = await BindAsync(serverId, catalog.Settings, ct).ConfigureAwait(false);

        await PreflightAsync(plan, actions, context, planId, serverId, ct).ConfigureAwait(false);

        // THE CLAIM. Write-ahead and RowVersion-guarded: durable "an apply is starting" before the first
        // mutating call, and the point at which a second concurrent attempt that read the same Previewed row
        // loses. Two attempts can both reach here; exactly one gets past it.
        plan.Status = ChangePlanStatus.Applying;
        await _store.UpdateAsync(plan, [], ct).ConfigureAwait(false);

        return await WriteAsync(plan, actions, context, planId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses the operation when <paramref name="target"/> carries a write guard that is not
    /// <see cref="WriteMode.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// Delegated to <see cref="ExecutionTargetWriteMode"/> rather than re-derived here, exactly as the SSH
    /// and local-process backup providers do, so those three and this one cannot drift into different
    /// answers to the same question. A target with no guard anywhere in it answers <see langword="null"/> and
    /// is allowed through: this method surfaces a refusal the guard would make anyway, earlier and with a
    /// message about the operation rather than about a file. Anything that slips past still meets the guard
    /// at the first real write — see <see cref="ApplyAsync"/>'s handling of a mid-flight revocation, which is
    /// a real possibility because <c>WriteGuardedExecutionTarget</c> re-resolves the grant per call.
    /// </remarks>
    private static void RequireWritesEnabled(IExecutionTarget target, string serverId) =>
        ExecutionTargetWriteMode.RequireWritesEnabled(
            target,
            ApplyOperation,
            serverId,
            "Previewing a change, reading current values, and inspecting an already-recorded plan all remain "
            + "available.");

    /// <summary>
    /// The pre-flight sweep: proves every action is still carryable and every bound surface still hashes to
    /// what preview saw, before a single byte is written.
    /// </summary>
    /// <exception cref="PlanStaleException">A surface drifted, vanished, or moved. The plan is marked stale.</exception>
    /// <exception cref="PlanApplyFidelityException">
    /// A stored action's post-image content and its recorded digest describe different files, or content was
    /// recorded with no digest at all. The ledger row disagrees with itself, so there is no trustworthy
    /// statement of what the operator approved to check a write against — and checking a write against a
    /// digest that was never its content's is worse than not checking, because it passes. Nothing is written.
    /// </exception>
    /// <exception cref="InvalidOperationException">A stored action is not carryable at all (a missing post-image).</exception>
    private async Task PreflightAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        PlanContext context,
        string planId,
        string serverId,
        CancellationToken ct)
    {
        foreach (var action in actions)
        {
            // A row that records no bytes to write cannot be applied and cannot be reasoned about. Refused as
            // a caller/storage bug rather than treated as "write an empty file", which would truncate a real
            // configuration file to nothing.
            if (action.PostImageContent is null)
            {
                throw new InvalidOperationException(
                    $"Action #{action.Ordinal} of change plan '{planId}' (surface '{action.SurfaceId}') "
                    + "records no post-image content, so there is nothing to write. Nothing was written. "
                    + "Preview the change again to get a plan with a rendered post-image.");
            }

            // The stored content and the stored digest must agree BEFORE anything is written. This is what
            // makes PostImageHash usable as "the digest the operator approved" later: without it, the
            // post-write comparisons would be checking the bytes against a number that might itself be wrong,
            // and a corrupted row would sail through both of them.
            var renderedHash = Hash(StrictUtf8.GetBytes(action.PostImageContent));
            if (action.PostImageHash is not { } recordedHash)
            {
                throw new PlanApplyFidelityException(
                    $"Action #{action.Ordinal} of change plan '{planId}' (surface '{action.SurfaceId}') "
                    + "records post-image content but no digest for it, so there is nothing to verify the "
                    + "write against. Nothing was written. Preview the change again.",
                    planId,
                    action.Ordinal,
                    action.SurfaceId,
                    renderedHash,
                    null);
            }

            if (!string.Equals(renderedHash, recordedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanApplyFidelityException(
                    $"Action #{action.Ordinal} of change plan '{planId}' (surface '{action.SurfaceId}') has a "
                    + $"stored post-image whose content hashes to {renderedHash} but whose recorded digest is "
                    + $"{recordedHash}. The stored plan disagrees with itself, so there is no way to tell "
                    + "which of the two the operator approved. Nothing was written. Preview the change again.",
                    planId,
                    action.Ordinal,
                    action.SurfaceId,
                    recordedHash,
                    renderedHash);
            }

            if (!context.Bound.TryGetValue(action.SurfaceId, out var surface) || surface.Surface.Path is null)
            {
                await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

                var reason = context.Failures.TryGetValue(action.SurfaceId, out var failure)
                    ? " " + failure.Reason
                    : string.Empty;

                throw new PlanStaleException(
                    $"Surface '{action.SurfaceId}', which change plan '{planId}' writes, no longer resolves "
                    + $"to a reachable file on server '{serverId}'.{reason} The plan has been marked stale "
                    + "and nothing was written.",
                    planId);
            }

            // ResolvedPath's only job — see its own remarks. A freshly resolved path that differs means the
            // deployment moved underneath the plan, and writing the approved bytes to a path the operator
            // never saw is exactly the mistake this column exists to catch.
            if (!string.Equals(surface.Surface.Path.Value.Value, action.ResolvedPath, StringComparison.Ordinal))
            {
                await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

                throw new PlanStaleException(
                    $"Surface '{action.SurfaceId}' resolved to '{action.ResolvedPath}' when change plan "
                    + $"'{planId}' was previewed and resolves to '{surface.Surface.Path.Value.Value}' now, so "
                    + "the deployment moved underneath the plan. It has been marked stale and nothing was "
                    + "written.",
                    planId);
            }
        }

        var expected = DeserializeHashes(plan.SurfaceHashesJson);

        foreach (var (surfaceId, hash) in expected.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            // Every surface the preview READ, not merely the ones it writes. Several planned values were
            // validated against surfaces this plan does not touch, and a change to one of those invalidates
            // the plan just as surely as a change to a written one.
            if (!context.Bound.TryGetValue(surfaceId, out var surface))
            {
                await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

                throw new PlanStaleException(
                    $"Surface '{surfaceId}', which change plan '{planId}' was validated against, is no longer "
                    + $"reachable on server '{serverId}'. The plan has been marked stale and nothing was "
                    + "written.",
                    planId);
            }

            var (bytes, error) = await ReadRawAsync(surface, ct).ConfigureAwait(false);
            if (error is not null)
            {
                await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

                throw new PlanStaleException(
                    $"Surface '{surfaceId}' could not be re-read to check change plan '{planId}' for drift: "
                    + $"{error} The plan has been marked stale and nothing was written.",
                    planId);
            }

            var actual = Hash(bytes!);
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            {
                await TryMarkStaleAsync(plan, ct).ConfigureAwait(false);

                throw new PlanStaleException(
                    $"Surface '{surfaceId}' has changed since change plan '{planId}' was previewed (expected "
                    + $"content hash {hash}, found {actual}), so the plan no longer describes this server. It "
                    + "has been marked stale and NOTHING was written. Preview the change again to see the "
                    + "current state.",
                    planId);
            }
        }
    }

    /// <summary>
    /// Walks the plan's actions in ordinal order, writing each one write-ahead-logged, and returns the
    /// receipt once every one has landed.
    /// </summary>
    private async Task<ChangeReceipt> WriteAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        PlanContext context,
        string planId,
        CancellationToken ct)
    {
        var applied = new List<PlannedAction>(actions.Count);

        foreach (var action in actions)
        {
            var surface = context.Bound[action.SurfaceId];
            var path = surface.Surface.Path!.Value;

            // Write-ahead, exactly as ProvisioningExecutor commits its intent before asking a provider to
            // create anything: the row says "this write is being attempted" BEFORE it is, so a process that
            // dies mid-write leaves a row that names the file to go and look at rather than no trace at all.
            action.Status = ChangePlanActionStatus.Applying;
            await _store.UpdateAsync(plan, [action], ct).ConfigureAwait(false);

            // Captured before anything can overwrite it. PreflightAsync has already proved this digest agrees
            // with the bytes about to be sent, so it is a trustworthy statement of what the operator
            // approved rather than a second guess at the same thing.
            var approved = action.PostImageHash!;

            try
            {
                var bytes = StrictUtf8.GetBytes(action.PostImageContent!);
                using var content = new MemoryStream(bytes, writable: false);

                var receipt = await surface.Session.Target.WriteFileAsync(
                        path,
                        content,

                        // The persisted pre-image hash goes straight through as the expectation: both are a
                        // bare lower-case hex SHA-256 over the file's RAW bytes, which is what every
                        // transport computes and compares. Null (a file that did not exist at preview) is a
                        // supported "no expectation".
                        new FileWriteOptions(action.PreImageHash)
                        {
                            Strategy = FileWriteStrategy.AtomicRename,
                        },
                        ct)
                    .ConfigureAwait(false);

                // Set the moment the write call returns anything at all, and BEFORE any verification runs. A
                // receipt means the transport did something to the server, so a failure after this point must
                // not be reported as "nothing happened" — not by RecordFailureAsync below, and not by the
                // retention sweep, which reads this same column off the persisted row to decide whether the
                // pre-image may be discarded. Both of the throws below leave it set, deliberately: the write
                // landed, it was just the wrong bytes.
                action.WriteReachedServer = true;

                // CHECK 1 — THE TRANSPORT AGREES ABOUT THE BYTES IT WAS GIVEN.
                //
                // Precisely that, and no more. Every transport in this repo computes PostImageSha256 over the
                // buffer it drained from the stream, before or independently of placing it
                // (DockerExecutionTarget, SftpFileChannel, ShellFileChannel, LocalExecutionTarget all do), so
                // a matching receipt says NOTHING about what is on disk. Against today's transports this can
                // only fire for one that miscomputes or misreports its own receipt. It is kept because it is
                // free, cannot false-positive (both sides are bare lower-case hex SHA-256 over raw bytes),
                // and would catch a future transport that transforms content. The check that actually speaks
                // to the file is CHECK 2 below.
                if (!string.Equals(approved, receipt.PostImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    // The observed column, never PostImageHash: that one is what the operator approved and
                    // stays so for the life of the row (PreflightAsync above depends on it agreeing with
                    // PostImageContent). PostWriteVerification is left at NotAttempted here and that is
                    // accurate — nothing has read the file; this failure is the transport contradicting
                    // itself about its own input.
                    action.ObservedPostImageHash = receipt.PostImageSha256;

                    throw new PlanApplyFidelityException(
                        $"Action #{action.Ordinal} of change plan '{planId}' wrote surface "
                        + $"'{action.SurfaceId}' at '{action.ResolvedPath}', but the transport reported a "
                        + $"post-image digest of {receipt.PostImageSha256} for content that was approved as "
                        + $"{approved}. The transport does not agree about the bytes it was handed, so what "
                        + "it placed cannot be trusted to be what the operator approved. The write was NOT "
                        + "undone and NOT retried; inspect the file directly.",
                        planId,
                        action.Ordinal,
                        action.SurfaceId,
                        approved,
                        receipt.PostImageSha256);
                }

                // CHECK 2 — WHAT IS ACTUALLY ON THE SERVER.
                var (verification, observed) = await VerifyWrittenAsync(surface, approved, ct)
                    .ConfigureAwait(false);

                // Recorded on every arm, including the one that is about to throw, and null exactly when
                // nothing was read. PostImageHash is NOT touched: approved and observed are two different
                // facts and the row has a column for each.
                action.ObservedPostImageHash = observed;
                action.PostWriteVerification = verification;

                // Ordered mismatch-first, and that ordering is load-bearing rather than stylistic. A read-back
                // that DISAGREED is the opposite of one that could not be performed, so emitting the "nothing
                // has looked at the file" warning before this check would log a flat falsehood on the single
                // path where the log matters most: something did look, and it found the wrong bytes.
                if (verification == PostWriteVerification.Mismatched)
                {
                    throw new PlanApplyFidelityException(
                        $"Action #{action.Ordinal} of change plan '{planId}' wrote surface "
                        + $"'{action.SurfaceId}' at '{action.ResolvedPath}', but reading it back found "
                        + $"content hashing to {observed} where {approved} was approved. The bytes on the "
                        + "server are NOT the bytes the operator approved. The write was NOT undone and NOT "
                        + "retried — a second write chasing a bad first one risks damaging the file further. "
                        + "Inspect it directly.",
                        planId,
                        action.Ordinal,
                        action.SurfaceId,
                        approved,
                        observed);
                }

                if (verification == PostWriteVerification.Unverifiable)
                {
                    _logger?.LogWarning(
                        "Action #{Ordinal} of change plan {PlanId} wrote surface {SurfaceId}, but the write "
                        + "could not be confirmed by reading it back. The change is believed to have landed; "
                        + "nothing has looked at the file.",
                        action.Ordinal,
                        planId,
                        action.SurfaceId);
                }

                action.Status = ChangePlanActionStatus.Applied;
                action.AppliedAt = _time.GetUtcNow();

                await _store.UpdateAsync(plan, [action], ct).ConfigureAwait(false);

                applied.Add(new PlannedAction(
                    action.Kind,
                    action.SurfaceId,
                    action.UnifiedDiff,
                    action.Reversible,
                    action.RequiredCapabilities));
            }
            catch (Exception ex)
            {
                // CancellationToken.None: the ledger write must happen even when the reason we are here is
                // that the caller's token was cancelled. Losing the record of a write that already landed is
                // strictly worse than honouring a cancellation promptly.
                //
                // action.WriteReachedServer is what stops a fidelity failure on action #0 from being recorded
                // as Failed ("no action applied"): a receipt came back, so the server WAS changed — wrongly,
                // which is the single most important case in this method to report accurately. It is
                // deliberately not set for a TargetDriftException or a WritesDisabledException: both are
                // contractually refused before any I/O, so nothing was touched on those paths. Reading the
                // property rather than a local also means the plan's status and the row the retention sweep
                // reads cannot drift apart — they are the same fact.
                await RecordFailureAsync(
                        plan,
                        actions,
                        action,
                        ex,
                        applied.Count > 0 || action.WriteReachedServer,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                _logger?.LogError(
                    ex,
                    "Applying change plan {PlanId} failed at action #{Ordinal} (surface {SurfaceId}); "
                    + "{Applied} of {Total} action(s) landed. The plan is recorded as {Status}.",
                    planId,
                    action.Ordinal,
                    action.SurfaceId,
                    applied.Count,
                    actions.Count,
                    plan.Status);

                // Drift found by the transport's own pre-image check — the TOCTOU backstop behind the
                // pre-flight sweep. Restated as the staleness the contract promises, naming the action so an
                // operator knows precisely where the plan stopped agreeing with the server.
                if (ex is TargetDriftException drift)
                {
                    throw new PlanStaleException(
                        $"Action #{action.Ordinal} of change plan '{planId}' could not be written: surface "
                        + $"'{action.SurfaceId}' at '{action.ResolvedPath}' drifted between this plan's "
                        + $"pre-flight check and the write itself (expected content hash "
                        + $"{drift.ExpectedHash ?? "<none>"}, found {drift.ActualHash ?? "<none>"}). "
                        + $"{applied.Count} of {actions.Count} action(s) had already been written and were "
                        + "NOT rolled back; the plan is recorded as "
                        + $"{plan.Status} and each action's own row says whether it landed.",
                        planId,
                        drift);
                }

                // Everything else — including a WritesDisabledException from a grant revoked mid-plan —
                // propagates unchanged. Wrapping it would hide which layer refused.
                throw;
            }
        }

        var appliedAt = _time.GetUtcNow();
        plan.Status = ChangePlanStatus.Applied;
        plan.AppliedAt = appliedAt;
        plan.AppliedBy = _actor;
        await _store.UpdateAsync(plan, [], ct).ConfigureAwait(false);

        return new ChangeReceipt(planId, appliedAt, applied);
    }

    /// <summary>
    /// Reads a just-written surface back off the server and hashes it, so the ledger can say what is
    /// actually there rather than only what a write call returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only check in the apply path that speaks to bytes on disk.</strong> A
    /// <see cref="FileWriteReceipt"/> is computed by every transport over the buffer it was handed, not by
    /// re-reading the file, so a transport that reflowed, re-encoded or truncated the content would still
    /// return a receipt matching what it was given. Re-reading is what closes that gap.
    /// </para>
    /// <para>
    /// <strong>An unreadable surface is never a failure.</strong> The write succeeded; only the confirmation
    /// did not. Failing the action here would report a change that really did land as one that did not, which
    /// is a worse lie than an unverified success — so this returns
    /// <see cref="PostWriteVerification.Unverifiable"/> and lets the action stand, with the ledger saying
    /// plainly that nobody looked. In practice the capability arm is unreachable for a surface that resolved
    /// at all: <c>SurfaceResolver</c> puts <see cref="TransportCapabilities.FileRead"/> in every resolved
    /// surface's requirements and refuses the surface when the session lacks it. It is checked anyway rather
    /// than assumed, because "a resolved surface is always readable" is a property of another class that
    /// nothing here would notice changing.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <para>
    /// One of exactly three outcomes. <see cref="PostWriteVerification.Verified"/> and
    /// <see cref="PostWriteVerification.Mismatched"/> both mean a read really happened and both carry the
    /// digest of what was read, so the caller can record the observed value either way — for a mismatch it is
    /// the evidence, and for a match it is the confirmation. <see cref="PostWriteVerification.Unverifiable"/>
    /// means no bytes were obtained and the hash is <see langword="null"/>; the hash is non-null in exactly
    /// the other two cases.
    /// </para>
    /// <para>
    /// <see cref="PostWriteVerification.NotAttempted"/> is never returned: reaching this method IS the
    /// attempt.
    /// </para>
    /// </returns>
    private static async Task<(PostWriteVerification Verification, string? ObservedHash)> VerifyWrittenAsync(
        BoundSurface surface,
        string approvedHash,
        CancellationToken ct)
    {
        if (!surface.Surface.RequiredCapabilities.HasFlag(TransportCapabilities.FileRead))
        {
            return (PostWriteVerification.Unverifiable, null);
        }

        var (bytes, error) = await ReadRawAsync(surface, ct).ConfigureAwait(false);
        if (error is not null || bytes is null)
        {
            return (PostWriteVerification.Unverifiable, null);
        }

        var actual = Hash(bytes);
        return string.Equals(actual, approvedHash, StringComparison.OrdinalIgnoreCase)
            ? (PostWriteVerification.Verified, actual)
            : (PostWriteVerification.Mismatched, actual);
    }

    /// <summary>
    /// Records a failed action, marks every action after it as never-attempted, and settles the plan's own
    /// status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ChangePlanActionStatus.Skipped"/> is written explicitly onto the actions that never ran,
    /// rather than leaving them at <see cref="ChangePlanActionStatus.Pending"/>. "We never got here" then
    /// reads as an assertion in the data instead of being inferred from the absence of one, which is the
    /// difference between a ledger an operator can act on and one they have to guess at.
    /// </para>
    /// <para>
    /// This method writes <see cref="ChangePlanActionStatus.Failed"/> and the reason, and deliberately touches
    /// nothing else on the failed row. <c>WriteReachedServer</c>, <c>ObservedPostImageHash</c> and
    /// <c>PostWriteVerification</c> were already set by the caller at the moment each became true, and it is
    /// this call that persists them — so a mismatch row lands in storage saying Failed AND that a write
    /// reached the server AND what was found there, rather than only the first of the three.
    /// </para>
    /// <para>
    /// The plan becomes <see cref="ChangePlanStatus.PartiallyApplied"/> only when something really did land,
    /// and <see cref="ChangePlanStatus.Failed"/> otherwise — matching those members' own definitions rather
    /// than overstating the damage. Either way it is not <see cref="ChangePlanStatus.Applied"/>, and the
    /// per-action rows remain the authoritative account of what happened.
    /// </para>
    /// <para>
    /// A ledger write that itself fails is swallowed and logged, never rethrown: the caller is already about
    /// to surface the real failure, and replacing it with a storage error would lose the reason the apply
    /// stopped. The action stays at <see cref="ChangePlanActionStatus.Applying"/> and the plan at
    /// <see cref="ChangePlanStatus.Applying"/>, which is the honest "outcome unknown, go and look" state —
    /// the same non-terminal shape <c>ProvisioningExecutor</c> leaves a ledger row in.
    /// </para>
    /// </remarks>
    private async Task RecordFailureAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        ChangePlanActionRecord failed,
        Exception failure,
        bool anythingLanded,
        CancellationToken ct)
    {
        failed.Status = ChangePlanActionStatus.Failed;
        failed.FailureReason = failure.Message;

        var touched = new List<ChangePlanActionRecord> { failed };
        foreach (var action in actions)
        {
            if (action.Ordinal > failed.Ordinal && action.Status == ChangePlanActionStatus.Pending)
            {
                action.Status = ChangePlanActionStatus.Skipped;
                touched.Add(action);
            }
        }

        plan.Status = anythingLanded ? ChangePlanStatus.PartiallyApplied : ChangePlanStatus.Failed;
        plan.AppliedAt = _time.GetUtcNow();
        plan.AppliedBy = _actor;

        try
        {
            await _store.UpdateAsync(plan, touched, ct).ConfigureAwait(false);
        }
        catch (Exception ledgerFailure)
        {
            _logger?.LogError(
                ledgerFailure,
                "Change plan {PlanId} failed at action #{Ordinal} AND the ledger could not be updated to say "
                + "so. The plan and that action remain in Applying: their real outcome is unknown from "
                + "storage alone and the file must be inspected directly.",
                plan.Id,
                failed.Ordinal);
        }
    }

    /// <summary>
    /// Records a plan as <see cref="ChangePlanStatus.Stale"/>, tolerating a lost race to do so.
    /// </summary>
    /// <remarks>
    /// A concurrency failure here means someone else already moved this plan on, which is the outcome this
    /// call wanted anyway. Swallowing it keeps the caller's real refusal — the staleness the operator needs
    /// to read about — instead of replacing it with a storage error about a bookkeeping write.
    /// </remarks>
    private async Task TryMarkStaleAsync(ChangePlanRecord plan, CancellationToken ct)
    {
        plan.Status = ChangePlanStatus.Stale;

        try
        {
            await _store.UpdateAsync(plan, [], ct).ConfigureAwait(false);
        }
        catch (ChangePlanConcurrencyException ex)
        {
            _logger?.LogInformation(
                ex,
                "Change plan {PlanId} was already transitioned by someone else while it was being marked "
                + "stale. Nothing was written to the server either way.",
                plan.Id);
        }
    }

    /// <summary>Reads one bound surface's raw bytes, or the reason it could not be read.</summary>
    /// <remarks>
    /// Bytes, not text, and no parsing: the only question the pre-flight sweep asks is "does this file still
    /// hash to what preview saw", and that is only answerable over the bytes on disk. Deliberately not
    /// <see cref="ReadUncachedAsync"/>, which additionally parses, round-trip-checks and codec-decodes — work
    /// preview needed and this does not.
    /// </remarks>
    private static async Task<(byte[]? Bytes, string? Error)> ReadRawAsync(BoundSurface surface, CancellationToken ct)
    {
        if (surface.Surface.Path is not { } path)
        {
            return (null, $"surface '{surface.Surface.Id}' resolved without a concrete path.");
        }

        try
        {
            var stream = await surface.Session.Target.OpenReadAsync(path, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                return (buffer.ToArray(), null);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"{path.Value} could not be read via {surface.Session.Description}: {ex.Message}");
        }
    }

    /// <summary>Reads back the <see cref="ChangePlanRecord.SurfaceHashesJson"/> written at preview time.</summary>
    private static Dictionary<string, string> DeserializeHashes(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "A stored change plan's surface hashes could not be read back, so there is no way to tell "
                + "whether the server drifted since it was previewed. Nothing was written.",
                ex);
        }
    }

    // ── Revert ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The operation name <see cref="RequireRevertWritesEnabled"/> phrases its refusal around.</summary>
    private const string RevertOperation = "revert a configuration change";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Every failure-prone check runs across the WHOLE revert set before the first byte is
    /// written.</strong> A live server offers no transaction, so all-or-nothing has to be bought rather than
    /// declared, and the only currency available is front-loading. <see cref="PreflightRevertAsync"/> proves
    /// each action reversible, each pre-image present and agreeing with its own recorded digest, each surface
    /// still resolving to the path the plan recorded, and each session reachable — performing exactly two
    /// read-only calls per action and no write at all. Any failure raises
    /// <see cref="PlanRevertException"/> naming every offending ordinal, with nothing written.
    /// </para>
    /// <para>
    /// <strong>The revert set is <c>WriteReachedServer</c>, never <c>Status == Applied</c>.</strong> The
    /// action that most needs undoing is precisely the one apply left <see cref="ChangePlanActionStatus.Failed"/>
    /// after its bytes had already landed — a read-back fidelity mismatch has exactly that shape. Keying on
    /// status would skip it and report a clean revert over a server still holding content nobody approved.
    /// </para>
    /// <para>
    /// <strong>A purged pre-image is a refusal, not a skip.</strong>
    /// <c>IChangePlanStore.PurgeImagesAsync</c> nulls <see cref="ChangePlanActionRecord.PreImageContent"/>
    /// while deliberately keeping <see cref="ChangePlanActionRecord.PreImageHash"/>, so a swept row still
    /// claims a digest it can no longer produce the bytes for.
    /// <see cref="ChangePlanActionRecord.PreImageExisted"/> is what tells that row apart from one whose file
    /// genuinely did not exist (whose revert is a delete) — without it every file-creating plan would be
    /// permanently unrevertible, or every purged one would be silently deleted off a live server.
    /// </para>
    /// <para>
    /// <strong>Expiry and definition drift are NOT checked, and both omissions are deliberate.</strong> Apply
    /// refuses an expired plan because the operator's approval was given against a picture of the server that
    /// is no longer being verified, and refuses a changed definition because the rules the plan was derived
    /// from moved. A revert derives nothing: it writes literal recorded bytes back. Every applied plan is by
    /// definition long past its 15-minute preview TTL, so enforcing expiry would make this method unreachable,
    /// and refusing on a definition change would strand exactly the server whose definition change is the
    /// reason an operator wants the old file back.
    /// </para>
    /// <para>
    /// <strong>Nothing is restarted or recreated</strong>, for the same reason
    /// <see cref="ApplyAsync"/> restarts nothing: the receipt means the bytes are back on disk, not that the
    /// running workload has re-read them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="planId"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="planId"/> is not a plan id, names no stored plan, names a plan that has already been
    /// reverted or is being reverted or applied, names a plan whose server is no longer tracked, or names a
    /// plan containing an action this phase cannot carry out.
    /// </exception>
    /// <exception cref="PlanRevertException">
    /// The revert was refused by the pre-flight sweep (nothing written), or a restoring write failed partway
    /// through (in which case <see cref="PlanRevertException.Actions"/> says which ones landed).
    /// </exception>
    /// <exception cref="WritesDisabledException">The server's write mode is not <see cref="WriteMode.Enabled"/>.</exception>
    /// <exception cref="ChangePlanConcurrencyException">Another attempt claimed this plan's revert first.</exception>
    public async Task<RevertReceipt> RevertAsync(string planId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        if (!ChangePlanId.TryParse(planId, out var id))
        {
            throw new InvalidOperationException(
                $"'{planId}' is not a change plan identifier, so there is no plan to revert.");
        }

        var stored = await _store.TryGetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No change plan '{planId}' is stored, so there is nothing to revert — and a discarded plan "
                + "takes its recorded pre-images with it, so there is no way back through this route. Nothing "
                + "was written.");

        var plan = stored.Plan;
        var actions = stored.Actions;

        // A revert already in flight. The RowVersion claim further down is what makes this race-proof rather
        // than merely usually right; this check is what stops the far more common sequential case with a
        // message about the plan instead of a storage error about a token.
        if (plan.Status == ChangePlanStatus.Reverting)
        {
            throw new InvalidOperationException(
                $"Change plan '{planId}' is already being reverted, so a second revert must not start: two "
                + "sweeps writing the same pre-images over each other would leave the server in a state "
                + "neither of them can describe. Nothing was written. If no revert is actually running, this "
                + "plan's previous attempt did not survive to record its outcome — inspect the files it "
                + "names directly.");
        }

        // An APPLY in flight, refused for the same reason and with more urgency. Apply claims the plan the
        // instant its pre-flight passes and then writes post-images action by action, persisting
        // WriteReachedServer as each one lands — so by the time a second action is being written, action #0
        // already satisfies every condition below and this revert would sail through them all and start
        // restoring pre-images onto surfaces the apply is still writing. Two writers on one live game
        // server's files, interleaved, with no ordering between them.
        //
        // RowVersion does not cover this: it makes the APPLY fail at its next ledger write, which is after
        // the conflicting bytes have already reached the server. The check has to be here, before the
        // pre-flight sweep, and it mirrors ApplyAsync's own symmetric refusal of anything that is not
        // Previewed.
        if (plan.Status == ChangePlanStatus.Applying)
        {
            throw new InvalidOperationException(
                $"Change plan '{planId}' is being applied right now, so it must not be reverted: the revert "
                + "would write recorded pre-images onto the very surfaces the apply is still writing "
                + "post-images to, and nothing orders those two writers against each other. Nothing was "
                + "written. Wait for the apply to finish — it records its own outcome, including a partial "
                + "one — and revert then. If no apply is actually running, its attempt did not survive to "
                + "record an outcome; inspect the files the plan names directly.");
        }

        // RevertedAt rather than Status alone, because it is set on a PARTIALLY reverted plan too. A second
        // sweep over one of those would rewrite pre-images onto surfaces whose current content nobody has
        // re-examined since the first attempt stopped.
        if (plan.RevertedAt is { } revertedAt)
        {
            throw new InvalidOperationException(
                $"Change plan '{planId}' was already reverted at {revertedAt:u} by "
                + $"'{plan.RevertedBy ?? "an unrecorded actor"}' and is recorded as {plan.Status}, so it "
                + "cannot be reverted again. Nothing was written. Preview a new plan to change this server.");
        }

        // WriteReachedServer, NOT Status == Applied — see this method's own remarks for why the distinction
        // decides whether the most damaged action in the plan gets undone at all.
        var revertSet = actions.Where(a => a.WriteReachedServer).OrderBy(a => a.Ordinal).ToList();

        if (revertSet.Count == 0)
        {
            throw new PlanRevertException(
                $"No action of change plan '{planId}' recorded a write that reached the server, so there is "
                + "nothing to undo and nothing was written. A plan that was refused, that expired, or whose "
                + "every write was rejected before any I/O leaves the server exactly as it was; reverting it "
                + "would write pre-images over files this plan never touched.",
                planId,
                []);
        }

        // Out of scope for this phase and refused for the WHOLE revert, mirroring ApplyAsync's own refusal:
        // undoing the file half of a change whose control-channel half stays in force would leave the server
        // in a state neither the plan nor its reversal describes.
        if (revertSet.Exists(a => a.Kind == PlannedActionKind.WriteControlChannel))
        {
            var offending = revertSet
                .Where(a => a.Kind == PlannedActionKind.WriteControlChannel)
                .Select(a => $"#{a.Ordinal} ('{a.SurfaceId}')");

            throw new InvalidOperationException(
                $"Change plan '{planId}' contains control-channel action(s) {string.Join(", ", offending)} "
                + "that reached the server, and Servyx cannot yet undo one. The whole revert is refused and "
                + "NOTHING was written: restoring only its file actions would leave the server half-reverted "
                + "in a way no plan describes.");
        }

        var servers = _servers
            ?? throw new InvalidOperationException(
                $"This {nameof(PlanExecutor)} was constructed without an {nameof(IServerRepository)}, so a "
                + "stored plan's server cannot be resolved back to the container id its sessions are keyed "
                + "by. Reverting a plan is unavailable in this composition; previewing one is not.");

        var server = await servers.TryGetAsync(plan.ServerId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Change plan '{planId}' targets a server Servyx no longer tracks, so there is nothing to "
                + "revert it on. Nothing was written.");

        var serverId = server.ContainerId;

        var sessions = await _sessions.GetAsync(serverId, ct).ConfigureAwait(false);
        if (sessions is null || sessions.Sessions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No configuration session is open for server '{serverId}', so change plan '{planId}' cannot "
                + "be reverted. Nothing was written.");
        }

        // THE WRITE-MODE GATE, ahead of every side effect and across every session — the same house idiom
        // ApplyAsync states its own reasoning for. A revert is a write like any other and a read-only posture
        // refuses it; saying so up front, about the server's posture, beats a failure about a file halfway
        // through.
        foreach (var session in sessions.Sessions)
        {
            RequireRevertWritesEnabled(session.Target, serverId);
        }

        // The catalogue is fetched only because BindAsync takes a settings list; its identity is deliberately
        // NOT compared against the plan's — see this method's remarks. A server whose definition has since
        // been unbound entirely can still have its files put back, which is the point.
        var catalog = await _catalogs.GetAsync(serverId, ct).ConfigureAwait(false);

        // Surfaces are re-resolved rather than taken from ChangePlanActionRecord.ResolvedPath, exactly as
        // apply does: an IExecutionTarget is a live connection that cannot be persisted. The stored path is
        // the cross-check on this resolution, applied in the sweep below.
        var context = await BindAsync(serverId, catalog?.Settings ?? [], ct).ConfigureAwait(false);

        var targets = await PreflightRevertAsync(revertSet, context, planId, serverId, ct).ConfigureAwait(false);

        // THE CLAIM. Write-ahead and RowVersion-guarded, the same shape ApplyAsync uses: durable "a revert is
        // starting" before the first restoring write, and the point at which a second concurrent attempt that
        // read the same row loses.
        plan.Status = ChangePlanStatus.Reverting;
        await _store.UpdateAsync(plan, [], ct).ConfigureAwait(false);

        return await RestoreAsync(plan, revertSet, targets, planId, ct).ConfigureAwait(false);
    }

    /// <summary>Refuses a revert when the target carries a write guard that is not <see cref="WriteMode.Enabled"/>.</summary>
    /// <remarks>
    /// Delegated to <see cref="ExecutionTargetWriteMode"/> for the same reason
    /// <see cref="RequireWritesEnabled"/> is, and phrased around reverting rather than applying so an
    /// operator is told which operation their posture refused.
    /// </remarks>
    private static void RequireRevertWritesEnabled(IExecutionTarget target, string serverId) =>
        ExecutionTargetWriteMode.RequireWritesEnabled(
            target,
            RevertOperation,
            serverId,
            "Previewing a change, reading current values, and inspecting an already-recorded plan all remain "
            + "available.");

    /// <summary>
    /// The revert pre-flight sweep: proves every action in the revert set can be put back, and captures each
    /// surface's CURRENT digest, before a single byte is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Refusals are accumulated, not thrown at the first one.</strong> The whole set is examined even
    /// once one action has already failed, so an operator whose plan has three purged pre-images learns that
    /// once rather than three times — and, more importantly, so the message names every ordinal that will
    /// still be a problem after they fix the first.
    /// </para>
    /// <para>
    /// <strong>Nothing here writes.</strong> The only I/O is an existence probe (which is what proves the
    /// session is actually reachable, rather than assuming it from the surface having resolved) and, where
    /// the surface advertises <see cref="TransportCapabilities.FileRead"/>, one read to capture the current
    /// digest.
    /// </para>
    /// </remarks>
    /// <returns>One <see cref="RevertTarget"/> per action, keyed by ordinal.</returns>
    /// <exception cref="PlanRevertException">Any action cannot be reverted. Nothing was written.</exception>
    private async Task<Dictionary<int, RevertTarget>> PreflightRevertAsync(
        IReadOnlyList<ChangePlanActionRecord> revertSet,
        PlanContext context,
        string planId,
        string serverId,
        CancellationToken ct)
    {
        var refusals = new List<string>();
        var targets = new Dictionary<int, RevertTarget>();

        foreach (var action in revertSet)
        {
            ct.ThrowIfCancellationRequested();

            var named = $"Action #{action.Ordinal} (surface '{action.SurfaceId}')";

            // The plan's own verdict, recorded at preview. Honoured for the whole revert rather than for this
            // action alone: a partial restore is the outcome this phasing exists to make impossible.
            if (!action.Reversible)
            {
                refusals.Add(
                    $"{named} was recorded as NOT reversible when the plan was previewed, so the bytes it "
                    + "overwrote were never captured in a form this method can restore.");
                continue;
            }

            if (PreImageUnavailable(action, named) is { } unavailable)
            {
                refusals.Add(unavailable);
                continue;
            }

            if (!context.Bound.TryGetValue(action.SurfaceId, out var surface)
                || surface.Surface.Path is not { } path)
            {
                var reason = context.Failures.TryGetValue(action.SurfaceId, out var failure)
                    ? " " + failure.Reason
                    : string.Empty;

                refusals.Add(
                    $"{named} targets a surface that no longer resolves to a reachable file on server "
                    + $"'{serverId}'.{reason}");
                continue;
            }

            // ResolvedPath's one job, applied here for the same reason apply applies it: a deployment that
            // moved underneath the plan would have this revert write a pre-image into a file the operator
            // never saw, which is strictly worse than leaving the applied change in place.
            if (!string.Equals(path.Value, action.ResolvedPath, StringComparison.Ordinal))
            {
                refusals.Add(
                    $"{named} was applied to '{action.ResolvedPath}' and that surface resolves to "
                    + $"'{path.Value}' now, so the deployment moved underneath the plan.");
                continue;
            }

            if (!surface.Surface.RequiredCapabilities.HasFlag(TransportCapabilities.FileWrite))
            {
                refusals.Add(
                    $"{named} resolved without TransportCapabilities.FileWrite, so the session it is "
                    + "reachable on cannot put the file back.");
                continue;
            }

            // Reachability, proved rather than inferred. A surface resolves from cached deployment facts; a
            // session that has since dropped its connection still resolves and would fail at the write, by
            // which time earlier actions in the set would already have landed.
            try
            {
                await surface.Session.Target.ExistsAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                refusals.Add(
                    $"{named} could not be reached at '{path.Value}' via {surface.Session.Description}: "
                    + $"{ex.Message}");
                continue;
            }

            targets[action.Ordinal] = new RevertTarget(
                surface,
                path,
                await CurrentDigestAsync(surface, ct).ConfigureAwait(false),
                Delete: !action.PreImageExisted);
        }

        if (refusals.Count > 0)
        {
            throw new PlanRevertException(
                $"Change plan '{planId}' cannot be reverted on server '{serverId}' and NOTHING was written. "
                + string.Join(" ", refusals)
                + " A revert is all-or-nothing: restoring only the actions whose pre-images survive would "
                + "leave the server in a state no plan describes and no operator ever approved.",
                planId,

                // Every action in the set, each saying plainly that its write did not happen. This is the
                // pre-flight refusal's whole guarantee, stated in data rather than left to the prose.
                [.. revertSet.Select(a => new RevertedAction(a.Ordinal, a.SurfaceId, false, null))]);
        }

        return targets;
    }

    /// <summary>
    /// Names why an action's pre-image cannot be restored from, or <see langword="null"/> when it can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The digest is re-computed, not merely checked for presence.</strong> A purged row is the
    /// obvious failure and the easy one; a row whose <see cref="ChangePlanActionRecord.PreImageContent"/> was
    /// truncated by a botched migration, a half-committed write or a column-width change is the dangerous one,
    /// because it is fully present and looks restorable right up until it is written over a live
    /// configuration file. Hashing it here is the only way to tell the two apart, and it costs one SHA-256
    /// over content already in memory.
    /// </para>
    /// <para>
    /// <see cref="ChangePlanActionRecord.PreImageExisted"/> is consulted FIRST and short-circuits everything
    /// else: an action whose file did not exist has no content and no digest by construction, and demanding
    /// them would refuse exactly the revert that is simplest to perform correctly.
    /// </para>
    /// </remarks>
    private static string? PreImageUnavailable(ChangePlanActionRecord action, string named)
    {
        if (!action.PreImageExisted)
        {
            return null;
        }

        if (action.PreImageContent is not { } preImage)
        {
            return $"{named} records that a file was there before the write but holds no pre-image content "
                + "for it, so the bytes to restore are gone — most often because the retention sweep "
                + "(IChangePlanStore.PurgeImagesAsync) discarded them once the window for reverting had "
                + "passed.";
        }

        if (action.PreImageHash is not { } recorded)
        {
            return $"{named} records pre-image content but no digest for it, so there would be no way to "
                + "check that restoring it actually landed.";
        }

        var rendered = Hash(StrictUtf8.GetBytes(preImage));

        return string.Equals(rendered, recorded, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{named} has a stored pre-image whose content hashes to {rendered} but whose recorded digest "
                + $"is {recorded}. The stored row disagrees with itself, so neither can be trusted as the "
                + "file's original content — writing it would overwrite a live file with bytes that are "
                + "provably not what was there.";
    }

    /// <summary>
    /// The digest of what is on the server RIGHT NOW, or <see langword="null"/> when it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This — not <see cref="ChangePlanActionRecord.PostImageHash"/> and not
    /// <see cref="ChangePlanActionRecord.PreImageHash"/> — is what the restoring write carries as its
    /// <see cref="FileWriteOptions.ExpectedPreImageHash"/>, and the choice matters in both directions.</strong>
    /// The pre-image hash is what the file should hold AFTER the revert, so expecting it would refuse every
    /// revert there was ever any point in performing. The post-image hash looks right — it is what apply put
    /// there — and fails on the single most important case: an apply whose read-back found a mismatch left
    /// content that is NOT the post-image, and that corrupted file is precisely the one an operator reverts.
    /// </para>
    /// <para>
    /// What the expectation is actually for is the TOCTOU window between this sweep and the write moments
    /// later — the same job it does in apply — so the honest value is whatever the file holds now. A surface
    /// that cannot be read yields no expectation rather than a guessed one: a wrong expectation refuses every
    /// write, which would turn an unreadable-but-writable surface into a permanently unrevertible one.
    /// </para>
    /// </remarks>
    private static async Task<string?> CurrentDigestAsync(BoundSurface surface, CancellationToken ct)
    {
        if (!surface.Surface.RequiredCapabilities.HasFlag(TransportCapabilities.FileRead))
        {
            return null;
        }

        var (bytes, error) = await ReadRawAsync(surface, ct).ConfigureAwait(false);
        return error is not null || bytes is null ? null : Hash(bytes);
    }

    /// <summary>
    /// Walks the revert set in ordinal order, restoring each surface write-ahead-logged, and returns the
    /// receipt once every one has been attempted.
    /// </summary>
    private async Task<RevertReceipt> RestoreAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> revertSet,
        IReadOnlyDictionary<int, RevertTarget> targets,
        string planId,
        CancellationToken ct)
    {
        var outcomes = new List<RevertedAction>(revertSet.Count);

        foreach (var action in revertSet)
        {
            var target = targets[action.Ordinal];

            // Write-ahead, exactly as WriteAsync claims each action before attempting it: the row says "this
            // restore is being attempted" BEFORE it is, so a process that dies mid-revert leaves a row naming
            // the file to go and look at rather than no trace at all.
            action.Status = ChangePlanActionStatus.Reverting;
            await _store.UpdateAsync(plan, [action], ct).ConfigureAwait(false);

            try
            {
                if (target.Delete)
                {
                    // The file did not exist before this plan created it, so putting it back means REMOVING
                    // it. Writing zero bytes instead would leave the workload reading a valid, empty
                    // configuration surface it never had — a different state from the one being restored, and
                    // one that looks like a successful revert from every column in this table.
                    //
                    // The drift re-check first, because IExecutionTarget.DeleteAsync takes no expectation of
                    // its own. The write branch below closes the sweep-to-write TOCTOU window by handing the
                    // sweep's digest to the transport as FileWriteOptions.ExpectedPreImageHash and letting it
                    // refuse; a delete has no such parameter, so the same window has to be closed here or a
                    // file somebody edited since the sweep gets removed on the strength of a stale reading.
                    // Deleting is the one restore that cannot be inspected afterwards.
                    await RequireUndriftedForDeleteAsync(target, ct).ConfigureAwait(false);

                    await target.Surface.Session.Target.DeleteAsync(target.Path, ct).ConfigureAwait(false);
                }
                else
                {
                    var bytes = StrictUtf8.GetBytes(action.PreImageContent!);
                    using var content = new MemoryStream(bytes, writable: false);

                    await target.Surface.Session.Target.WriteFileAsync(
                            target.Path,
                            content,
                            new FileWriteOptions(target.ExpectedHash)
                            {
                                Strategy = FileWriteStrategy.AtomicRename,
                            },
                            ct)
                        .ConfigureAwait(false);
                }

                // Set the moment the call returns anything at all, and BEFORE any verification — the same
                // rule WriteReachedServer follows on the apply path, and for the same reason: a failure after
                // this point must never be reported as "nothing happened".
                action.RevertWriteReachedServer = true;

                // READ-BACK, the only check here that speaks to the server rather than to a return value. A
                // FileWriteReceipt is computed by every transport over the buffer it was HANDED, so comparing
                // one to the digest of the bytes we handed it is a tautology that cannot catch a transport
                // mangling them on the way to disk — see VerifyWrittenAsync's own remarks.
                var (verification, observed) = target.Delete
                    ? await VerifyDeletedAsync(target, ct).ConfigureAwait(false)
                    : await VerifyWrittenAsync(target.Surface, action.PreImageHash!, ct).ConfigureAwait(false);

                // The observed digest goes in its OWN column. PreImageHash stays what it always was — the
                // digest of the bytes this revert restored from — so the two can be compared long after the
                // images themselves are purged.
                action.RevertVerification = verification;
                action.RevertObservedImageHash = observed;

                if (verification == PostWriteVerification.Mismatched)
                {
                    // RECORDED, NOT THROWN, and this is the one place the revert path deliberately diverges
                    // from apply's. Apply throws on a mismatch because it must stop before writing further
                    // unapproved bytes. A revert has no such reason to abort: the remaining actions each
                    // restore a DIFFERENT surface whose own pre-image is still perfectly good, and refusing to
                    // attempt them would leave more of the server holding applied content than continuing
                    // does. The failure is not softened — the action is Failed, the plan lands
                    // PartiallyReverted, and the receipt says which surface was not restored.
                    action.Status = ChangePlanActionStatus.Failed;
                    action.RevertFailureReason =
                        $"Reverting action #{action.Ordinal} wrote surface '{action.SurfaceId}' at "
                        + $"'{action.ResolvedPath}', but reading it back found content hashing to "
                        + $"{observed ?? "<nothing>"} where the recorded pre-image "
                        + $"{action.PreImageHash ?? "<none>"} was expected. The surface was NOT restored.";

                    _logger?.LogError(
                        "Reverting action #{Ordinal} of change plan {PlanId} wrote surface {SurfaceId}, but "
                        + "reading it back found different content. The surface was NOT restored and is "
                        + "recorded as such; nothing was rewritten or retried.",
                        action.Ordinal,
                        planId,
                        action.SurfaceId);
                }
                else
                {
                    action.Status = ChangePlanActionStatus.Reverted;
                    action.RevertedAt = _time.GetUtcNow();

                    if (verification == PostWriteVerification.Unverifiable)
                    {
                        _logger?.LogWarning(
                            "Reverting action #{Ordinal} of change plan {PlanId} restored surface "
                            + "{SurfaceId}, but the restore could not be confirmed by reading it back. The "
                            + "change is believed to have landed; nothing has looked at the file.",
                            action.Ordinal,
                            planId,
                            action.SurfaceId);
                    }
                }

                await _store.UpdateAsync(plan, [action], ct).ConfigureAwait(false);

                outcomes.Add(new RevertedAction(action.Ordinal, action.SurfaceId, true, verification));
            }
            catch (Exception ex)
            {
                // This action's own outcome first, reading its state off the row rather than off a local so
                // the account cannot drift from what storage says, then every action after it — none of which
                // was attempted, stated as a positive fact rather than left absent.
                outcomes.Add(new RevertedAction(
                    action.Ordinal,
                    action.SurfaceId,
                    action.RevertWriteReachedServer,
                    action.RevertVerification));

                foreach (var later in revertSet.Where(a => a.Ordinal > action.Ordinal))
                {
                    outcomes.Add(new RevertedAction(later.Ordinal, later.SurfaceId, false, null));
                }

                // CancellationToken.None: the ledger write must happen even when the reason we are here is a
                // cancelled token. Losing the record of a restore that already landed is strictly worse than
                // honouring a cancellation promptly.
                await RecordRevertFailureAsync(plan, action, ex, outcomes, CancellationToken.None)
                    .ConfigureAwait(false);

                var landed = outcomes.Count(o => o.WriteReachedServer);

                _logger?.LogError(
                    ex,
                    "Reverting change plan {PlanId} failed at action #{Ordinal} (surface {SurfaceId}); "
                    + "{Landed} of {Total} restore(s) reached the server. The plan is recorded as {Status}.",
                    planId,
                    action.Ordinal,
                    action.SurfaceId,
                    landed,
                    revertSet.Count,
                    plan.Status);

                // Deliberately PlanRevertException even for a TargetDriftException, where ApplyAsync restates
                // the drift as PlanStaleException. Two reasons. "Stale" is a statement about a plan that was
                // never applied and must not be — this one WAS applied and its record is accurate; and
                // PlanStaleException carries no per-action account, which is the one thing an operator staring
                // at a half-reverted server actually needs. The drift travels as the inner exception with its
                // hashes intact, and the message says plainly which surface moved.
                throw new PlanRevertException(
                    $"Reverting change plan '{planId}' stopped at action #{action.Ordinal} (surface "
                    + $"'{action.SurfaceId}' at '{action.ResolvedPath}')"
                    + (ex is TargetDriftException drift
                        ? $": that file drifted between this revert's pre-flight sweep and the restoring "
                            + $"write itself (expected content hash {drift.ExpectedHash ?? "<none>"}, found "
                            + $"{drift.ActualHash ?? "<none>"}), so nothing was written for it. "
                        : $": {ex.Message} ")
                    + $"{landed} of {revertSet.Count} restore(s) reached the server and were NOT rolled back "
                    + "— a revert is no more undoable than the apply it undoes. The plan is recorded as "
                    + $"{plan.Status} and each action's own row says whether its restore landed.",
                    planId,
                    outcomes,
                    ex);
            }
        }

        var revertedAt = _time.GetUtcNow();

        // PartiallyReverted when any surface came back holding something other than its pre-image. Every
        // write returned successfully on that path, so "Reverted" would be defensible from the return values
        // alone and would be a lie about the server — the whole reason the read-back exists.
        plan.Status = outcomes.Exists(o => o.Verification == PostWriteVerification.Mismatched)
            ? ChangePlanStatus.PartiallyReverted
            : ChangePlanStatus.Reverted;

        plan.RevertedAt = revertedAt;
        plan.RevertedBy = _actor;
        await _store.UpdateAsync(plan, [], ct).ConfigureAwait(false);

        return new RevertReceipt(planId, revertedAt, outcomes);
    }

    /// <summary>
    /// Refuses a revert-by-delete whose target no longer holds what the pre-flight sweep saw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The delete branch's stand-in for <see cref="FileWriteOptions.ExpectedPreImageHash"/>.</strong>
    /// <see cref="IExecutionTarget.DeleteAsync"/> accepts no expectation, so the optimistic-concurrency check
    /// the write branch delegates to the transport has to be performed here instead — re-reading the file
    /// immediately before removing it and comparing against
    /// <see cref="RevertTarget.ExpectedHash"/>, the digest the sweep captured moments earlier.
    /// </para>
    /// <para>
    /// <strong>A null expectation is no expectation, exactly as it is on the write branch.</strong>
    /// <see cref="CurrentDigestAsync"/> yields null for a surface that cannot be read at all, and inventing a
    /// check there would turn an unreadable-but-writable surface into a permanently unrevertible one. A
    /// surface that COULD be read at sweep time and cannot be read now is a mismatch, not an exemption: the
    /// honest reading is "this is no longer the file the sweep looked at".
    /// </para>
    /// <para>
    /// Raised as <see cref="TargetDriftException"/> rather than a bespoke type so it travels the identical
    /// path a transport-refused write already takes — <see cref="RestoreAsync"/>'s catch records the failure,
    /// leaves the plan <see cref="ChangePlanStatus.PartiallyReverted"/> or
    /// <see cref="ChangePlanStatus.RevertFailed"/>, and restates it as a
    /// <see cref="PlanRevertException"/> naming the drifted surface and its hashes.
    /// </para>
    /// </remarks>
    /// <exception cref="TargetDriftException">The file changed since the pre-flight sweep. Nothing was deleted.</exception>
    private static async Task RequireUndriftedForDeleteAsync(RevertTarget target, CancellationToken ct)
    {
        if (target.ExpectedHash is not { } expected)
        {
            return;
        }

        var actual = await CurrentDigestAsync(target.Surface, ct).ConfigureAwait(false);

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetDriftException(
                $"Content at '{target.Path.Value}' has drifted since this revert's pre-flight sweep read it, "
                + "so it was NOT deleted. A revert-by-delete removes a file outright; doing that on the "
                + "strength of a reading somebody has already overwritten destroys content no plan recorded.",
                target.Path,
                expected,
                actual);
        }
    }

    /// <summary>
    /// Confirms a revert-by-delete by asking whether the file is still there.
    /// </summary>
    /// <remarks>
    /// The delete path's counterpart to <see cref="VerifyWrittenAsync"/>, and it reports through the same
    /// enum on purpose: "the file is gone" is <see cref="PostWriteVerification.Verified"/>, "it is still
    /// there" is <see cref="PostWriteVerification.Mismatched"/> — a delete that returned successfully and left
    /// the file in place is exactly the read-back contradiction that member exists for — and a probe that
    /// throws is <see cref="PostWriteVerification.Unverifiable"/> rather than a failure, because the delete
    /// itself already succeeded and only the confirmation did not. The observed hash is always
    /// <see langword="null"/>: an absent file has no content to digest, and echoing one would read as a
    /// measurement nobody took.
    /// </remarks>
    private static async Task<(PostWriteVerification Verification, string? ObservedHash)> VerifyDeletedAsync(
        RevertTarget target,
        CancellationToken ct)
    {
        try
        {
            var exists = await target.Surface.Session.Target.ExistsAsync(target.Path, ct).ConfigureAwait(false);
            return (exists ? PostWriteVerification.Mismatched : PostWriteVerification.Verified, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (PostWriteVerification.Unverifiable, null);
        }
    }

    /// <summary>Records a failed restore and settles the plan's own status.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately NOT the <see cref="ChangePlanActionStatus.Skipped"/> sweep
    /// <see cref="RecordFailureAsync"/> performs.</strong> The actions after this one are sitting at
    /// <see cref="ChangePlanActionStatus.Applied"/> — a true statement about an apply that really happened —
    /// and overwriting it to record a fact about the revert would destroy the apply ledger to describe its
    /// failed undo. "This restore was never attempted" belongs to the revert, and travels on
    /// <see cref="PlanRevertException.Actions"/> where it can say so without lying about anything else.
    /// </para>
    /// <para>
    /// <see cref="ChangePlanRecord.RevertedAt"/> is set even though the revert failed, which is what makes the
    /// already-reverted guard refuse a blind retry. That is the intent: after a partial revert the server
    /// holds a mixture of pre-apply and applied content, and a second sweep would write pre-images over
    /// surfaces whose current state nobody has looked at since. Recovery is a human's decision, exactly as it
    /// is after a partial apply.
    /// </para>
    /// <para>
    /// A ledger write that itself fails is swallowed and logged, never rethrown — the caller is already about
    /// to surface the real failure, and replacing it with a storage error would lose the reason the revert
    /// stopped.
    /// </para>
    /// </remarks>
    private async Task RecordRevertFailureAsync(
        ChangePlanRecord plan,
        ChangePlanActionRecord failed,
        Exception failure,
        IReadOnlyList<RevertedAction> outcomes,
        CancellationToken ct)
    {
        failed.Status = ChangePlanActionStatus.Failed;
        failed.RevertFailureReason = failure.Message;

        // PartiallyReverted only when something really was put back, RevertFailed otherwise — matching those
        // members' own definitions rather than overstating either the damage or the progress.
        plan.Status = outcomes.Any(o => o.WriteReachedServer)
            ? ChangePlanStatus.PartiallyReverted
            : ChangePlanStatus.RevertFailed;

        plan.RevertedAt = _time.GetUtcNow();
        plan.RevertedBy = _actor;

        try
        {
            await _store.UpdateAsync(plan, [failed], ct).ConfigureAwait(false);
        }
        catch (Exception ledgerFailure)
        {
            _logger?.LogError(
                ledgerFailure,
                "Reverting change plan {PlanId} failed at action #{Ordinal} AND the ledger could not be "
                + "updated to say so. The plan and that action remain in their in-flight state: their real "
                + "outcome is unknown from storage alone and the file must be inspected directly.",
                plan.Id,
                failed.Ordinal);
        }
    }

    /// <summary>Everything one action's restore needs, resolved once by the pre-flight sweep.</summary>
    /// <param name="Surface">The re-resolved surface and the session it is reachable on.</param>
    /// <param name="Path">Its concrete path, already cross-checked against the recorded one.</param>
    /// <param name="ExpectedHash">
    /// What the file held when the sweep looked — the optimistic-concurrency expectation for whichever restore
    /// this target gets. The write branch hands it to the transport as
    /// <see cref="FileWriteOptions.ExpectedPreImageHash"/>; the delete branch, whose transport call takes no
    /// such parameter, checks it itself in <see cref="RequireUndriftedForDeleteAsync"/>. See
    /// <see cref="CurrentDigestAsync"/> for why it is neither recorded digest.
    /// </param>
    /// <param name="Delete">Whether restoring means removing the file rather than writing bytes to it.</param>
    private sealed record RevertTarget(BoundSurface Surface, TargetPath Path, string? ExpectedHash, bool Delete);

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

            // Recorded at the one place a pre-image is ever captured, and derived rather than assumed: an
            // action only reaches this point for a surface this previewer READ and PARSED, and a file that
            // was read existed. Writing it explicitly is what stops a later revert having to guess whether a
            // null PreImageContent means "there was no file" or "the retention sweep took it" — see
            // ChangePlanActionRecord.PreImageExisted. A future create-if-absent write path records false here
            // and inherits a correct revert (a delete) for free.
            PreImageExisted = true,
            PostImageContent = after,
            PostImageHash = Hash(StrictUtf8.GetBytes(after)),

            // When ContainsSecrets is true these two columns hold the operator's real secret values in
            // plaintext. That is deliberate and load-bearing — an exact revert needs the real bytes, see
            // ChangePlanActionRecord's own remarks — and it used to be an unbounded accumulation, because
            // nothing read ChangePlanRecord.ExpiresAt and no purge existed. Both halves shipped with
            // ApplyAsync, as its stated prerequisite: expiry is enforced at the point of use (ApplyAsync
            // refuses an expired plan and records it Stale) and IChangePlanStore.PurgeImagesAsync sweeps the
            // rest, discarding these two columns once no revert can need them. See
            // ChangePlanRetentionOptions for the window and what raising or lowering it trades away.
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
