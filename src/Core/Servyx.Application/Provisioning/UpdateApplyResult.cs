using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// The outcome of <see cref="IProvisioningDashboard.ApplyUpdateAsync"/>. Exactly one case is returned, and
/// the cases are shaped differently on purpose so no caller can render one as another.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The counterpart of <see cref="ProvisioningApplyResult"/>, with the two refusals an update has
/// that a create does not.</strong> <see cref="Applied"/>, <see cref="Stale"/>, and <see cref="Failed"/>
/// carry the same meanings and the same discipline as on the create path — a refusal executed nothing, a
/// failure may have created something and always names the ledger row a sweep must resolve.
/// <see cref="RequiresAcknowledgement"/> and <see cref="NoChangeRequired"/> are new because an update, unlike
/// a create, can destroy state that already exists and can turn out to be unnecessary.
/// </para>
/// <para>
/// <strong>There is no case that means "apply anyway".</strong> As on the create path, <see cref="Stale"/>
/// is terminal for that attempt and there is no force/override parameter anywhere.
/// <see cref="RequiresAcknowledgement"/> is likewise not overridable by a flag: the only way past it is to
/// supply the correctly-typed <see cref="DataImpactAcknowledgement"/> for the impact the plan actually
/// states.
/// </para>
/// </remarks>
public abstract record UpdateApplyResult
{
    // Private so the case set is closed to this file.
    private UpdateApplyResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>
    /// The plan was revalidated, the required acknowledgement was present and matched, and the operation ran
    /// through <see cref="ProvisioningExecutor"/> — write-ahead ledger row first, provider call second.
    /// </summary>
    public sealed record Applied : UpdateApplyResult
    {
        /// <summary>Creates an applied result.</summary>
        /// <param name="resource">The resource the provider confirmed, as the operation returned it.</param>
        /// <param name="planHash">The update plan hash that was revalidated immediately before execution.</param>
        /// <param name="strategy">The strategy the revalidated plan stated.</param>
        /// <param name="dataImpact">The data impact the revalidated plan stated.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="planHash"/> is null, empty, or whitespace.</exception>
        public Applied(ProvisionedResource resource, string planHash, UpdateStrategy strategy, DataImpact dataImpact)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

            Resource = resource;
            PlanHash = planHash;
            Strategy = strategy;
            DataImpact = dataImpact;
        }

        /// <summary>The resource that now exists, including the transport target to reach it.</summary>
        public ProvisionedResource Resource { get; }

        /// <summary>The update plan hash that was still current when the operation ran.</summary>
        public string PlanHash { get; }

        /// <summary>How the applied plan said it would reach the desired state.</summary>
        public UpdateStrategy Strategy { get; }

        /// <summary>
        /// What the applied plan said it would do to persistent data. Carried on the result, not just checked
        /// before it, so an audit of what was applied does not have to re-derive it.
        /// </summary>
        public DataImpact DataImpact { get; }

        /// <inheritdoc />
        public override string Message =>
            $"Updated '{Resource.Handle.ProvisionerId}' by {Strategy}; the resource is now "
            + $"'{Resource.Handle.ProviderResourceId}' and persistent data was {DataImpact}.";
    }

    /// <summary>
    /// The plan recomputed from the live resource and the desired state no longer matches the hash the user
    /// was shown, so <strong>nothing was executed</strong>: no provider call, no ledger row.
    /// </summary>
    /// <remarks>
    /// An update plan hashes the observed live state as well as the desired state (see
    /// <see cref="UpdatePlan.PlanHash"/>), so this refusal fires when the resource itself changed under the
    /// operator as well as when the request did. Both are cases where the approval given no longer describes
    /// what would happen. The remedy is to preview again; there is no argument that skips the comparison.
    /// </remarks>
    public sealed record Stale : UpdateApplyResult
    {
        /// <summary>Creates a stale-plan refusal.</summary>
        /// <param name="expectedPlanHash">The hash the caller was shown at preview time and approved.</param>
        /// <param name="currentPlanHash">The hash the plan recomputes to now.</param>
        /// <param name="currentPlanId">The id of the plan as it recomputes now.</param>
        /// <exception cref="ArgumentException">Any argument is null, empty, or whitespace.</exception>
        public Stale(string expectedPlanHash, string currentPlanHash, string currentPlanId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlanHash);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentPlanHash);
            ArgumentException.ThrowIfNullOrWhiteSpace(currentPlanId);

            ExpectedPlanHash = expectedPlanHash;
            CurrentPlanHash = currentPlanHash;
            CurrentPlanId = currentPlanId;
        }

        /// <summary>The plan hash the caller approved.</summary>
        public string ExpectedPlanHash { get; }

        /// <summary>The plan hash the same resource and request produce now.</summary>
        public string CurrentPlanHash { get; }

        /// <summary>The id of the plan the same resource and request produce now.</summary>
        public string CurrentPlanId { get; }

        /// <inheritdoc />
        public override string Message =>
            "This update plan is stale: the live resource or the desired state changed since it was previewed, "
            + $"so the plan you approved ({ExpectedPlanHash}) is not the plan that would run ({CurrentPlanHash}). "
            + "Nothing was changed. Preview again and confirm the plan you are then shown.";
    }

    /// <summary>
    /// The plan does not leave persistent data <see cref="DataImpact.Preserved"/>, and the caller did not
    /// supply the matching <see cref="DataImpactAcknowledgement"/>. <strong>Nothing was executed.</strong>
    /// </summary>
    /// <remarks>
    /// Also returned when an acknowledgement was supplied that does not match the plan's actual impact —
    /// including an acknowledgement supplied for a <see cref="DataImpact.Preserved"/> plan. A mismatch in
    /// either direction means the caller approved something other than what would run, which is precisely
    /// what this parameter exists to catch.
    /// </remarks>
    public sealed record RequiresAcknowledgement : UpdateApplyResult
    {
        /// <summary>Creates an acknowledgement refusal.</summary>
        /// <param name="planDataImpact">The impact the revalidated plan actually states.</param>
        /// <param name="acknowledgedDataImpact">
        /// The impact the caller acknowledged, or <see langword="null"/> if it supplied no acknowledgement.
        /// </param>
        /// <param name="planHash">The hash of the plan that was refused.</param>
        /// <exception cref="ArgumentException"><paramref name="planHash"/> is null, empty, or whitespace.</exception>
        public RequiresAcknowledgement(DataImpact planDataImpact, DataImpact? acknowledgedDataImpact, string planHash)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

            PlanDataImpact = planDataImpact;
            AcknowledgedDataImpact = acknowledgedDataImpact;
            PlanHash = planHash;
        }

        /// <summary>What the plan says it would do to persistent data.</summary>
        public DataImpact PlanDataImpact { get; }

        /// <summary>What the caller acknowledged, or <see langword="null"/> if it acknowledged nothing.</summary>
        public DataImpact? AcknowledgedDataImpact { get; }

        /// <summary>The hash of the plan that was refused, so the caller can re-present the same plan.</summary>
        public string PlanHash { get; }

        /// <inheritdoc />
        public override string Message => AcknowledgedDataImpact is null
            ? $"This update states its impact on persistent data as {PlanDataImpact}, which must be acknowledged "
              + "explicitly and separately from approving the plan. Nothing was changed."
            : $"This update states its impact on persistent data as {PlanDataImpact}, but the acknowledgement "
              + $"supplied was for {AcknowledgedDataImpact}. Acknowledging one impact never authorises another. "
              + "Nothing was changed.";
    }

    /// <summary>
    /// The revalidated plan reported <see cref="UpdateStrategy.NoChangeRequired"/>, so
    /// <strong>nothing was executed</strong>.
    /// </summary>
    /// <remarks>
    /// This is a refusal, not a no-op success, and it is load-bearing. Such a plan carries no changes and no
    /// stages — it "would do nothing at all" per <see cref="UpdateStrategy.NoChangeRequired"/> — so running
    /// the provisioner's create operation for it would not update anything; it would stand up a second
    /// resource beside the one already matching the desired state. Reporting the plan's own verdict instead
    /// is the only honest outcome.
    /// </remarks>
    public sealed record NoChangeRequired : UpdateApplyResult
    {
        /// <summary>Creates a no-change result.</summary>
        /// <param name="planHash">The hash of the revalidated plan that reported nothing to do.</param>
        /// <exception cref="ArgumentException"><paramref name="planHash"/> is null, empty, or whitespace.</exception>
        public NoChangeRequired(string planHash)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

            PlanHash = planHash;
        }

        /// <summary>The hash of the plan that reported nothing to do.</summary>
        public string PlanHash { get; }

        /// <inheritdoc />
        public override string Message =>
            "The live resource already matches the desired state, so nothing was changed.";
    }

    /// <summary>
    /// The provisioner is registered but does not implement <see cref="IMaintainer"/>, so there is no update
    /// to apply. <strong>Nothing was executed.</strong>
    /// </summary>
    /// <remarks>
    /// Carried as a result rather than an exception for the same reason as
    /// <see cref="PlanUpdateResult.Unsupported"/>: capability is a question, not a fault. A caller that
    /// reached here without first calling <see cref="IProvisioningDashboard.PlanUpdateAsync"/> gets the same
    /// answer it would have got there, and still cannot execute anything.
    /// </remarks>
    public sealed record Unsupported : UpdateApplyResult
    {
        /// <summary>Creates an unsupported result.</summary>
        /// <param name="provisionerId">The provisioner that does not implement <see cref="IMaintainer"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="provisionerId"/> is null, empty, or whitespace.</exception>
        public Unsupported(string provisionerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

            ProvisionerId = provisionerId;
        }

        /// <summary>The provisioner that cannot answer maintenance questions.</summary>
        public string ProvisionerId { get; }

        /// <inheritdoc />
        public override string Message =>
            $"Provisioner '{ProvisionerId}' does not support maintenance, so there is no update to apply. "
            + "Nothing was changed.";
    }

    /// <summary>
    /// The maintainer looked and the provider no longer knows about the resource, so there is nothing to
    /// update. <strong>Nothing was executed.</strong>
    /// </summary>
    public sealed record ResourceGone : UpdateApplyResult
    {
        /// <summary>Creates a resource-gone result.</summary>
        /// <param name="provisionerId">The provisioner that was asked.</param>
        /// <param name="handle">The handle the provider no longer recognises.</param>
        /// <exception cref="ArgumentException"><paramref name="provisionerId"/> is null, empty, or whitespace.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        public ResourceGone(string provisionerId, ResourceHandle handle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
            ArgumentNullException.ThrowIfNull(handle);

            ProvisionerId = provisionerId;
            Handle = handle;
        }

        /// <summary>The provisioner that was asked.</summary>
        public string ProvisionerId { get; }

        /// <summary>The handle the provider no longer recognises.</summary>
        public ResourceHandle Handle { get; }

        /// <inheritdoc />
        public override string Message =>
            $"'{ProvisionerId}' no longer knows about resource '{Handle.ProviderResourceId}', so there was nothing "
            + "to update and nothing was changed. Reconcile before acting on it.";
    }

    /// <summary>
    /// Execution began — the write-ahead intent row was committed — and then failed. The row is deliberately
    /// still in <see cref="ResourceLifecycleState.Intended"/> so a reconciliation sweep can find whatever the
    /// provider may have kept.
    /// </summary>
    public sealed record Failed : UpdateApplyResult
    {
        /// <summary>Creates a failed result.</summary>
        /// <param name="message">The failure as the executor described it. Shown to the user verbatim.</param>
        /// <param name="ledgerRowId">The write-ahead ledger row the attempt was recorded against.</param>
        /// <param name="compensated">
        /// Whether removal of the partial resource completed without error. <see langword="false"/> means a
        /// resource may still exist — and may still be billing — at the provider.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="message"/> is null, empty, or whitespace.</exception>
        public Failed(string message, Guid ledgerRowId, bool compensated)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);

            Message = message;
            LedgerRowId = ledgerRowId;
            Compensated = compensated;
        }

        /// <inheritdoc />
        public override string Message { get; }

        /// <summary>The ledger row the failed attempt was recorded against.</summary>
        public Guid LedgerRowId { get; }

        /// <summary>Whether compensation completed. <see langword="false"/> means an orphan may remain.</summary>
        public bool Compensated { get; }
    }
}
