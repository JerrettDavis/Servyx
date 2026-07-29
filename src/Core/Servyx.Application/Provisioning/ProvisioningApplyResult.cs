using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// The outcome of <see cref="IProvisioningDashboard.ApplyAsync"/>: exactly one of "the resource exists",
/// "the plan you were shown no longer describes these inputs", or "the attempt failed and here is the
/// ledger row it failed against".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a closed hierarchy rather than a nullable resource plus an error string.</strong> The three
/// outcomes are not the same shape and must not be renderable by accident as one another. A refusal
/// (<see cref="Stale"/>) created nothing and has no ledger row; a failure (<see cref="Failed"/>) may have
/// created something and always has a ledger row a sweep must resolve. Collapsing them into
/// <c>ProvisionedResource?</c> would let a caller show "nothing happened" for a case where a billable
/// resource may exist — the exact confusion the ledger exists to prevent. This mirrors the closed
/// hierarchies <c>Servyx.Domain</c> already uses for low-cardinality taxonomies (see <c>OrphanScope</c>).
/// </para>
/// <para>
/// <strong>There is no fourth case that means "apply anyway".</strong> <see cref="Stale"/> is terminal for
/// that attempt: the caller's only move is to preview again and confirm the plan it is then shown. No
/// force/override parameter exists anywhere on this path, matching
/// <see cref="Domain.Configuration.PlanStaleException"/>'s discipline on the configuration side — a stale
/// plan is refused, never overridden.
/// </para>
/// </remarks>
public abstract record ProvisioningApplyResult
{
    // Private so the case set is closed to this file. A new outcome is a deliberate, reviewable act.
    private ProvisioningApplyResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>
    /// The provider created the resource and the ledger row was advanced to <c>Created</c>.
    /// </summary>
    public sealed record Applied : ProvisioningApplyResult
    {
        /// <summary>Creates an applied result.</summary>
        /// <param name="resource">The resource the provider confirmed, as the operation returned it.</param>
        /// <param name="planHash">The plan hash that was revalidated immediately before execution.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="planHash"/> is null, empty, or whitespace.</exception>
        public Applied(ProvisionedResource resource, string planHash)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentException.ThrowIfNullOrWhiteSpace(planHash);

            Resource = resource;
            PlanHash = planHash;
        }

        /// <summary>The created resource, including the <see cref="Domain.Transport.TargetDescriptor"/> to reach it.</summary>
        public ProvisionedResource Resource { get; }

        /// <summary>The plan hash that was still current when the operation ran.</summary>
        public string PlanHash { get; }

        /// <inheritdoc />
        public override string Message =>
            $"Created '{Resource.Handle.ProviderResourceId}' at '{Resource.Handle.ProvisionerId}'.";
    }

    /// <summary>
    /// The plan recomputed from the request no longer matches the plan hash the user was shown, so
    /// <strong>nothing was executed</strong>. No provider call was made and no ledger row was written.
    /// </summary>
    /// <remarks>
    /// This is the provisioning-side counterpart of
    /// <see cref="Domain.Configuration.PlanStaleException"/>: the inputs drifted between preview and
    /// confirmation, so the approval the user gave no longer describes what would happen. It is reported
    /// rather than thrown because it is an expected answer to a UI question — "is what I showed still
    /// true?" — not an exceptional condition, and the caller needs the two hashes to explain the refusal.
    /// </remarks>
    public sealed record Stale : ProvisioningApplyResult
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

        /// <summary>The plan hash the same request produces now.</summary>
        public string CurrentPlanHash { get; }

        /// <summary>The id of the plan the same request produces now.</summary>
        public string CurrentPlanId { get; }

        /// <inheritdoc />
        public override string Message =>
            "This plan is stale: the inputs changed since it was previewed, so the plan you approved "
            + $"({ExpectedPlanHash}) is not the plan that would run ({CurrentPlanHash}). Nothing was created. "
            + "Preview again and confirm the plan you are then shown.";
    }

    /// <summary>
    /// Execution began — the write-ahead intent row was committed — and then failed. The row is
    /// deliberately still in <see cref="ResourceLifecycleState.Intended"/> so a reconciliation sweep can
    /// find whatever the provider may have kept.
    /// </summary>
    public sealed record Failed : ProvisioningApplyResult
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
