using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// One registered <see cref="IProvisioner"/> as the UI sees it: its stable id and the capabilities it
/// actually implements. Deliberately a projection rather than the live <see cref="IProvisioner"/> itself,
/// so a presentation layer holding this cannot reach a provider call by accident.
/// </summary>
/// <param name="ProvisionerId">The provisioner's stable <see cref="IProvisioner.ProvisionerId"/>.</param>
/// <param name="Capabilities">The capabilities the provisioner advertises.</param>
public sealed record ProvisionerDescriptor(string ProvisionerId, ProvisioningCapabilities Capabilities);

/// <summary>
/// One row of the provisioning ledger, paired with the lifecycle state that row is actually in and — for a
/// confirmed row only — the provider-assigned identity that state guarantees.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="State"/> is carried explicitly rather than assumed by the caller because the two enumerations
/// behind this projection answer very different questions, and a reader must be able to see on the row
/// itself which it is looking at. A row in <see cref="ResourceLifecycleState.Intended"/> is one that may be
/// billing with no confirmation; a row in <see cref="ResourceLifecycleState.Created"/> is one Servyx owns
/// and can name.
/// </para>
/// <para>
/// <strong><see cref="Handle"/> is non-<see langword="null"/> exactly when <see cref="State"/> is
/// <see cref="ResourceLifecycleState.Created"/>.</strong> That correspondence is not decoration: it is the
/// difference between a row that can be inspected against a live resource and one that provably cannot,
/// because the provider had not been contacted when it was written. A caller must therefore branch on the
/// handle rather than fabricate one — deriving an identifier from <see cref="ProvisioningIntent.Tags"/> is a
/// guess, and a drift check against the wrong resource is worse than no drift check.
/// </para>
/// </remarks>
/// <param name="Intent">
/// The row's intent-time facts as the ledger recorded them — id, provisioner, region, tags, job, timestamp.
/// These are recorded before the provider call and never rewritten, so they remain accurate for a confirmed
/// row too.
/// </param>
/// <param name="State">The lifecycle state this row is in.</param>
/// <param name="Handle">
/// The confirmed resource's complete provider-specific reference, or <see langword="null"/> when the row is
/// still <see cref="ResourceLifecycleState.Intended"/> and therefore identifies nothing at the provider yet.
/// </param>
public sealed record ProvisioningLedgerEntry(
    ProvisioningIntent Intent,
    ResourceLifecycleState State,
    ResourceHandle? Handle = null);

/// <summary>
/// The surface a dashboard needs in order to show what provisioning <em>would</em> do, and — as one
/// separate, explicitly named member — to make it happen after a user has confirmed it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mutating members are named for it, and there are exactly two.</strong>
/// <see cref="PlanAsync"/> delegates to <see cref="IProvisioner.PlanAsync"/>, which the
/// <see cref="IProvisioner"/> contract already requires to create and change nothing at the provider;
/// <see cref="PlanUpdateAsync"/> delegates to <see cref="IMaintainer.PlanUpdateAsync"/>, which reads the
/// live resource but has no member that can change it; <see cref="ListProvisioners"/> and
/// <see cref="ListLedgerEntriesAsync"/> read. <see cref="ApplyAsync"/> and <see cref="ApplyUpdateAsync"/>
/// are the members that can spend money, and neither can do so on its own: both drive
/// <see cref="ProvisioningExecutor"/>, which the composition root must have supplied (see
/// <see cref="ProvisioningServiceCollectionExtensions.AddServyxProvisioningExecution"/>). A host that
/// registers no executor gets an implementation whose <see cref="ExecutionConfigured"/> is
/// <see langword="false"/> and whose apply members refuse loudly rather than silently doing nothing.
/// </para>
/// <para>
/// <strong>Apply is not a second preview.</strong> <see cref="ApplyAsync"/> and
/// <see cref="ApplyUpdateAsync"/> each take the plan hash the caller was shown and refuse to execute if the
/// plan recomputed from the same inputs no longer matches it. There is no parameter that skips that check.
/// <see cref="ApplyUpdateAsync"/> additionally requires a separately-typed
/// <see cref="DataImpactAcknowledgement"/> before it will run a plan that does not preserve persistent data.
/// </para>
/// <para>
/// It depends only on <c>Servyx.Domain</c> abstractions, so no infrastructure project is referenced or
/// implied — which provisioners exist at all is decided entirely by the composition root.
/// </para>
/// </remarks>
public interface IProvisioningDashboard
{
    /// <summary>
    /// Whether an <see cref="IProvisioningLedger"/> is available at all.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can tell "no rows" apart from "nothing is recording rows". Those are very
    /// different answers, and silently rendering the second as the first would present an unmonitored
    /// system as a clean one.
    /// </remarks>
    bool LedgerConfigured { get; }

    /// <summary>
    /// Whether this process can actually apply a plan — i.e. whether a <see cref="ProvisioningExecutor"/>
    /// was supplied.
    /// </summary>
    /// <remarks>
    /// Exposed so a UI can tell "you may apply this" apart from "there is no execution path in this
    /// process" and say which, instead of rendering a control that would throw. When this is
    /// <see langword="false"/>, <see cref="ApplyAsync"/> throws rather than returning a failure result: a
    /// misconfigured host must be visible at the composition root, not reported as a per-attempt error.
    /// </remarks>
    bool ExecutionConfigured { get; }

    /// <summary>Lists every registered provisioner with the capabilities it advertises.</summary>
    IReadOnlyList<ProvisionerDescriptor> ListProvisioners();

    /// <summary>
    /// Computes — and only computes — the plan <paramref name="provisionerId"/> would follow to satisfy
    /// <paramref name="request"/>. Nothing is created, started, or changed.
    /// </summary>
    /// <param name="provisionerId">The provisioner to plan with.</param>
    /// <param name="request">The provisioning request to plan for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No provisioner is registered with that id.</exception>
    Task<ProvisioningPlan> PlanAsync(string provisionerId, ProvisioningRequest request, CancellationToken ct = default);

    /// <summary>
    /// Recomputes the plan for <paramref name="request"/>, refuses to proceed if it no longer hashes to
    /// <paramref name="approvedPlanHash"/>, and otherwise runs the provisioner's operation through
    /// <see cref="ProvisioningExecutor"/> — write-ahead ledger row first, provider call second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the only mutating member in the provisioning UI's reach, and it is a confirmation,
    /// not a preview.</strong> A caller is expected to have shown the user a
    /// <see cref="ProvisioningPlan"/> from <see cref="PlanAsync"/> and to pass that plan's
    /// <see cref="ProvisioningPlan.PlanHash"/> back here. If the recomputed hash differs — because the
    /// user edited an input, or because the provisioner would now do something else — the attempt is
    /// refused with <see cref="ProvisioningApplyResult.Stale"/> and <em>nothing is executed</em>: no
    /// provider call, no ledger row. There is deliberately no argument that overrides this. The remedy is
    /// to preview again and confirm what is then shown.
    /// </para>
    /// <para>
    /// A failure after execution began is returned as <see cref="ProvisioningApplyResult.Failed"/>
    /// carrying the ledger row id, never swallowed and never downgraded to "nothing happened": the row is
    /// left in <see cref="ResourceLifecycleState.Intended"/> precisely so a later
    /// <see cref="IProvisioner.ReconcileAsync"/> sweep can find whatever the provider may have kept.
    /// </para>
    /// </remarks>
    /// <param name="provisionerId">The provisioner to apply with.</param>
    /// <param name="request">The request the plan was previewed from. Must be the same inputs.</param>
    /// <param name="approvedPlanHash">The <see cref="ProvisioningPlan.PlanHash"/> the user was shown and approved.</param>
    /// <param name="jobId">The provisioning job this execution belongs to, recorded on the ledger row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No provisioner is registered with that id, or no <see cref="ProvisioningExecutor"/> is configured in
    /// this process (see <see cref="ExecutionConfigured"/>).
    /// </exception>
    Task<ProvisioningApplyResult> ApplyAsync(
        string provisionerId,
        ProvisioningRequest request,
        string approvedPlanHash,
        string? jobId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Computes — and only computes — the plan for bringing the already-provisioned resource behind
    /// <paramref name="handle"/> to the state <paramref name="desired"/> asks for. Nothing is changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Support is established by a type test, not by a promise.</strong> This member checks whether
    /// the resolved <see cref="IProvisioner"/> is also an <see cref="IMaintainer"/> and returns
    /// <see cref="PlanUpdateResult.Unsupported"/> if it is not — which is exactly why
    /// <see cref="IMaintainer"/> is a separate interface rather than extra members on
    /// <see cref="IProvisioner"/> (see the remarks on <see cref="IMaintainer"/>). No provisioner is asked to
    /// stub an answer it cannot honour, and no caller has to trust that it did not.
    /// </para>
    /// <para>
    /// <strong>This reads, unlike <see cref="PlanAsync"/> which computes.</strong> An honest
    /// <see cref="DataImpact"/> requires looking at the live resource, so this issues provider read calls.
    /// It still mutates nothing: <see cref="IMaintainer"/> has no member that can.
    /// </para>
    /// </remarks>
    /// <param name="provisionerId">The provisioner that owns the resource.</param>
    /// <param name="handle">The already-provisioned resource to plan an update for.</param>
    /// <param name="desired">The state the resource should end up in.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No provisioner is registered with that id.</exception>
    Task<PlanUpdateResult> PlanUpdateAsync(
        string provisionerId,
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default);

    /// <summary>
    /// Recomputes the update plan, refuses to proceed if it no longer hashes to
    /// <paramref name="approvedPlanHash"/> or if its <see cref="DataImpact"/> was not separately
    /// acknowledged, and otherwise runs the provisioner's operation through
    /// <see cref="ProvisioningExecutor"/> — write-ahead ledger row first, provider call second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two independent approvals are required, and they are different parameters of different
    /// types.</strong> <paramref name="approvedPlanHash"/> says "this is the plan I was shown", exactly as on
    /// the create path, and a mismatch is refused with <see cref="UpdateApplyResult.Stale"/> before anything
    /// executes. <paramref name="dataImpactAcknowledgement"/> says "and I accept that it will not preserve my
    /// data" — it is required, has no default, and cannot be constructed at all for
    /// <see cref="DataImpact.Preserved"/>, so there is no way for a caller written against safe updates to
    /// authorise a destructive one by passing the argument it already passes. A missing or mismatched
    /// acknowledgement is refused with <see cref="UpdateApplyResult.RequiresAcknowledgement"/> and nothing
    /// executes.
    /// </para>
    /// <para>
    /// <strong>Neither check is overridable.</strong> There is no force parameter here, on
    /// <see cref="ApplyAsync"/>, or anywhere else on this path. The only route past a stale plan is to
    /// preview again; the only route past the acknowledgement is to supply the token naming the impact the
    /// plan actually states.
    /// </para>
    /// <para>
    /// A failure after execution began is returned as <see cref="UpdateApplyResult.Failed"/> carrying the
    /// ledger row id, never swallowed and never downgraded to "nothing happened", exactly as on the create
    /// path.
    /// </para>
    /// </remarks>
    /// <param name="provisionerId">The provisioner that owns the resource.</param>
    /// <param name="handle">The already-provisioned resource being updated.</param>
    /// <param name="desired">The desired state the plan was previewed from. Must be the same inputs.</param>
    /// <param name="approvedPlanHash">The <see cref="UpdatePlan.PlanHash"/> the user was shown and approved.</param>
    /// <param name="dataImpactAcknowledgement">
    /// The caller's separate acknowledgement of a non-preserving <see cref="DataImpact"/>, or
    /// <see langword="null"/> to approve only a <see cref="DataImpact.Preserved"/> plan. Deliberately has no
    /// default value, so every caller states its position.
    /// </param>
    /// <param name="jobId">The provisioning job this execution belongs to, recorded on the ledger row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// No provisioner is registered with that id, or no <see cref="ProvisioningExecutor"/> is configured in
    /// this process (see <see cref="ExecutionConfigured"/>).
    /// </exception>
    Task<UpdateApplyResult> ApplyUpdateAsync(
        string provisionerId,
        ResourceHandle handle,
        ProvisioningRequest desired,
        string approvedPlanHash,
        DataImpactAcknowledgement? dataImpactAcknowledgement,
        string? jobId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Compares the live resource behind <paramref name="handle"/> against what that handle records Servyx
    /// provisioned, and reports every divergence by name. Changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thin pass-through to <see cref="IMaintainer.DetectDriftAsync"/>, gated by the same type test
    /// <see cref="PlanUpdateAsync"/> uses: a provisioner that is not an <see cref="IMaintainer"/> is never
    /// asked, and the answer is <see cref="DriftCheckResult.Unsupported"/> rather than a fabricated match.
    /// </para>
    /// <para>
    /// This reads the live resource, exactly as <see cref="PlanUpdateAsync"/> does, and mutates nothing —
    /// <see cref="IMaintainer"/> has no member that can.
    /// </para>
    /// </remarks>
    /// <param name="provisionerId">The provisioner that owns the resource.</param>
    /// <param name="handle">The recorded handle to compare the live resource against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No provisioner is registered with that id.</exception>
    Task<DriftCheckResult> DetectDriftAsync(
        string provisionerId,
        ResourceHandle handle,
        CancellationToken ct = default);

    /// <summary>
    /// Lists what the ledger knows about: the rows still unresolved (in
    /// <see cref="ResourceLifecycleState.Intended"/>, which an orphan sweep must account for) and the rows
    /// the provider has confirmed (in <see cref="ResourceLifecycleState.Created"/>, each carrying its real
    /// <see cref="ResourceHandle"/>). Returns an empty list when <see cref="LedgerConfigured"/> is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One list, not two calls.</strong> Both states are answers to "what has this process recorded",
    /// and a caller that had to merge two sequences itself would be free to render one without the other —
    /// showing only confirmed resources hides exactly the rows that may be leaking, and showing only intents
    /// hides everything Servyx actually owns. Which of the two any given row is stays visible on the row, in
    /// <see cref="ProvisioningLedgerEntry.State"/> and <see cref="ProvisioningLedgerEntry.Handle"/>.
    /// </para>
    /// <para>
    /// Rows in <see cref="ResourceLifecycleState.Destroying"/> and
    /// <see cref="ResourceLifecycleState.Destroyed"/> are not returned: <see cref="IProvisioningLedger"/>
    /// exposes no enumeration for them, and inventing one here would mean guessing at rows this layer cannot
    /// read.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ProvisioningLedgerEntry>> ListLedgerEntriesAsync(CancellationToken ct = default);
}
