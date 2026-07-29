using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// The default <see cref="IProvisioningDashboard"/>: a thin projection over whichever
/// <see cref="IProvisioner"/> instances the composition root chose to register, plus the ledger and the
/// plan executor if they are present.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every capability is exactly what the composition root handed in.</strong> No provisioners means
/// nothing to plan; no ledger means nothing is recording intent; no executor means nothing in this process
/// can apply a plan. Each of those is reported rather than papered over, and none of them is something this
/// class can conjure for itself.
/// </para>
/// <para>
/// <strong>The empty case is the normal case.</strong> When no <see cref="IProvisioner"/> is registered —
/// which is what a read-only host does — <c>IEnumerable&lt;IProvisioner&gt;</c> resolves to an empty
/// sequence and every member of this type answers honestly with nothing. There is no fallback, no default
/// provisioner, and no way for this class to conjure one: the set of provisioners is exactly what the
/// composition root put in the container.
/// </para>
/// <para>
/// <strong>The ledger is optional and its absence is visible.</strong> A host may register provisioners
/// for planning/preview purposes before it has a durable <see cref="IProvisioningLedger"/> wired up.
/// Rather than silently reporting "no ledger rows" — which reads identically to "nothing has been
/// provisioned" — <see cref="LedgerConfigured"/> lets the caller say which of the two it is looking at.
/// </para>
/// </remarks>
public sealed class ProvisioningDashboardService : IProvisioningDashboard
{
    private readonly IReadOnlyList<IProvisioner> _provisioners;
    private readonly IProvisioningLedger? _ledger;
    private readonly ProvisioningExecutor? _executor;

    /// <summary>
    /// Creates a dashboard over <paramref name="provisioners"/>.
    /// </summary>
    /// <param name="provisioners">
    /// Every registered provisioner. An empty sequence is valid and means provisioning is not available.
    /// </param>
    /// <param name="ledger">
    /// The provisioning ledger, or <see langword="null"/> if the host has not configured one. Passing
    /// <see langword="null"/> is a supported configuration, not an error, but it is reported through
    /// <see cref="LedgerConfigured"/> rather than hidden.
    /// </param>
    /// <param name="executor">
    /// The plan executor, or <see langword="null"/> if the host has not configured one — in which case this
    /// dashboard can plan and read but not apply, and says so through <see cref="ExecutionConfigured"/>.
    /// Defaulted so an existing read-and-plan composition (or test) keeps working unchanged and, more
    /// importantly, so gaining the ability to mutate is an explicit argument at the composition root rather
    /// than something that appears by default.
    /// </param>
    public ProvisioningDashboardService(
        IEnumerable<IProvisioner> provisioners,
        IProvisioningLedger? ledger = null,
        ProvisioningExecutor? executor = null)
    {
        ArgumentNullException.ThrowIfNull(provisioners);

        _provisioners = [.. provisioners];
        _ledger = ledger;
        _executor = executor;
    }

    /// <inheritdoc />
    public bool LedgerConfigured => _ledger is not null;

    /// <inheritdoc />
    public bool ExecutionConfigured => _executor is not null;

    /// <inheritdoc />
    public IReadOnlyList<ProvisionerDescriptor> ListProvisioners() =>
        [.. _provisioners.Select(p => new ProvisionerDescriptor(p.ProvisionerId, p.Capabilities))];

    /// <inheritdoc />
    public Task<ProvisioningPlan> PlanAsync(string provisionerId, ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(request);

        return Resolve(provisionerId).PlanAsync(request, ct);
    }

    /// <inheritdoc />
    public async Task<ProvisioningApplyResult> ApplyAsync(
        string provisionerId,
        ProvisioningRequest request,
        string approvedPlanHash,
        string? jobId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        var provisioner = Resolve(provisionerId);

        if (_executor is null)
        {
            // Loud, not silent, and not a Failed result: "this host has no execution path" is a composition
            // defect, not an outcome of this attempt. Returning a failure result would let a UI render it
            // beside genuine provider failures and imply something was tried.
            throw new InvalidOperationException(
                $"Cannot apply a plan for '{provisionerId}': no {nameof(ProvisioningExecutor)} is registered in this "
                + $"process. Call {nameof(ProvisioningServiceCollectionExtensions)}."
                + $"{nameof(ProvisioningServiceCollectionExtensions.AddServyxProvisioningExecution)}() and supply it "
                + "to this dashboard at the composition root.");
        }

        // Step 1 — revalidate. PlanAsync is required by IProvisioner to create and change nothing, so this
        // costs nothing but a recomputation, and it is what makes "the user approved this exact plan" true
        // at the moment of execution rather than at the moment of rendering.
        var current = await provisioner.PlanAsync(request, ct).ConfigureAwait(false);

        if (!string.Equals(current.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            // Refuse. There is no branch below this that can be reached with a mismatched hash, and no
            // parameter that skips it — mirroring PlanStaleException's discipline on the configuration side.
            return new ProvisioningApplyResult.Stale(approvedPlanHash, current.PlanHash, current.PlanId);
        }

        // Step 2 — build the provider-specific mutation. Still inert: CreateOperation commits nothing, per
        // IProvisioner's contract.
        var operation = provisioner.CreateOperation(request);

        try
        {
            // Step 3 — the one call that can create anything, and it goes through the executor so the
            // write-ahead ledger row is committed before the provider is contacted.
            var resource = await _executor.ExecuteAsync(operation, jobId, ct).ConfigureAwait(false);
            return new ProvisioningApplyResult.Applied(resource, current.PlanHash);
        }
        catch (ProvisioningExecutionException ex)
        {
            // Translated, not swallowed: the message and the ledger row id both travel to the caller so a
            // user can see what failed and an operator can find the row a sweep must resolve.
            return new ProvisioningApplyResult.Failed(ex.Message, ex.LedgerRowId, ex.Compensated);
        }
    }

    /// <inheritdoc />
    public async Task<PlanUpdateResult> PlanUpdateAsync(
        string provisionerId,
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        // The type test is the capability check. An adapter that does not implement IMaintainer is not asked
        // anything at all, so it never has to answer a maintenance question with a stub.
        if (Resolve(provisionerId) is not IMaintainer maintainer)
        {
            return new PlanUpdateResult.Unsupported(provisionerId);
        }

        var plan = await maintainer.PlanUpdateAsync(handle, desired, ct).ConfigureAwait(false);

        // Null means the provider no longer knows the resource — kept distinct from "nothing to change",
        // per IMaintainer.PlanUpdateAsync's contract.
        return plan is null
            ? new PlanUpdateResult.ResourceGone(provisionerId, handle)
            : new PlanUpdateResult.Planned(plan);
    }

    /// <inheritdoc />
    public async Task<UpdateApplyResult> ApplyUpdateAsync(
        string provisionerId,
        ResourceHandle handle,
        ProvisioningRequest desired,
        string approvedPlanHash,
        DataImpactAcknowledgement? dataImpactAcknowledgement,
        string? jobId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedPlanHash);

        var provisioner = Resolve(provisionerId);

        if (_executor is null)
        {
            // Same reasoning as ApplyAsync: a host with no execution path is a composition defect, not an
            // outcome of this attempt, so it is loud rather than a Failed result.
            throw new InvalidOperationException(
                $"Cannot apply an update for '{provisionerId}': no {nameof(ProvisioningExecutor)} is registered in "
                + $"this process. Call {nameof(ProvisioningServiceCollectionExtensions)}."
                + $"{nameof(ProvisioningServiceCollectionExtensions.AddServyxProvisioningExecution)}() and supply it "
                + "to this dashboard at the composition root.");
        }

        if (provisioner is not IMaintainer maintainer)
        {
            return new UpdateApplyResult.Unsupported(provisionerId);
        }

        // Step 1 — revalidate against the live resource. An update plan hashes what was observed as well as
        // what was asked for, so this catches the resource drifting under the operator between preview and
        // confirmation, not just the request changing.
        var current = await maintainer.PlanUpdateAsync(handle, desired, ct).ConfigureAwait(false);

        if (current is null)
        {
            return new UpdateApplyResult.ResourceGone(provisionerId, handle);
        }

        if (!string.Equals(current.PlanHash, approvedPlanHash, StringComparison.Ordinal))
        {
            // Refuse. No branch below is reachable with a mismatched hash and no parameter skips this.
            return new UpdateApplyResult.Stale(approvedPlanHash, current.PlanHash, current.PlanId);
        }

        // Step 2 — the second, independent approval. This lives here, in the Application layer, rather than
        // in whatever UI happens to call it, so a caller with a smaller surface cannot skip it: there is no
        // path to the executor below that does not pass through this check.
        if (!DataImpactAcknowledgement.Satisfies(dataImpactAcknowledgement, current.DataImpact))
        {
            return new UpdateApplyResult.RequiresAcknowledgement(
                current.DataImpact,
                dataImpactAcknowledgement?.Acknowledged,
                current.PlanHash);
        }

        if (current.Strategy == UpdateStrategy.NoChangeRequired)
        {
            // Such a plan carries no stages and would do nothing; running the create operation anyway would
            // stand up a second resource beside the one that already matches. Report the plan's own verdict.
            return new UpdateApplyResult.NoChangeRequired(current.PlanHash);
        }

        // Step 3 (destructive) — a plan that does not preserve data, executed by an adapter that says it can
        // execute one. This branch is reachable only with an acknowledgement in hand: the Satisfies check
        // above has already refused every non-preserving plan whose token is missing or names a different
        // impact, and the null test below is belt-and-braces so the impact handed on cannot be inferred from
        // anything but the token. Note what is *not* passed: current.DataImpact. Handing the adapter the
        // plan's own claim would be an acknowledgement of whatever the plan happens to say, which is no
        // acknowledgement at all — the value that travels is the one a human separately named.
        if (current.DataImpact != DataImpact.Preserved
            && dataImpactAcknowledgement is not null
            && provisioner is IDestructiveUpdateApplier destructiveApplier)
        {
            var destructiveExecution = await destructiveApplier
                .ApplyDestructiveUpdateAsync(
                    handle, current, approvedPlanHash, dataImpactAcknowledgement.Acknowledged, ct)
                .ConfigureAwait(false);

            // Reported exactly as the in-place branch below reports its own outcomes, and for the same
            // reasons: no write-ahead row is written on this path and no resource is created, so there is
            // nothing for a sweep to resolve and nothing that could have been orphaned.
            return destructiveExecution is UpdateExecutionResult.Completed destructiveCompleted
                ? new UpdateApplyResult.Applied(
                    destructiveCompleted.Resource, current.PlanHash, current.Strategy, current.DataImpact)
                : new UpdateApplyResult.Failed(
                    destructiveExecution.Message, ledgerRowId: Guid.Empty, compensated: true);
        }

        // Step 3 — if the adapter can genuinely apply an update to the resource that already exists, that is
        // what an update means and that is what runs. The type test is the capability check, exactly as it is
        // for IMaintainer above, and it sits *after* every refusal: there is no path to an IUpdateApplier that
        // skips the plan-hash revalidation or the acknowledgement, because both are above this line.
        if (provisioner is IUpdateApplier applier)
        {
            // The approved hash is handed on rather than the recomputed one, so the adapter re-checks the same
            // approval this method checked rather than a value derived from its own work.
            var execution = await applier
                .ApplyUpdateAsync(handle, current, approvedPlanHash, ct)
                .ConfigureAwait(false);

            // Every non-completed outcome — refused, failed, or accepted-but-not-confirmed — is reported as a
            // failure carrying the adapter's own message, which states which of the three it was and what to do
            // about it. The ledger row id is Guid.Empty and compensation is reported complete because both are
            // literally true here: this path writes no write-ahead row and creates no resource, so there is no
            // row for a sweep to resolve and nothing that could have been orphaned. That is the opposite of the
            // create path, where a failure means something may exist and may be billing.
            return execution is UpdateExecutionResult.Completed completed
                ? new UpdateApplyResult.Applied(completed.Resource, current.PlanHash, current.Strategy, current.DataImpact)
                : new UpdateApplyResult.Failed(execution.Message, ledgerRowId: Guid.Empty, compensated: true);
        }

        // Step 3 (fallback) — build the provider-specific mutation. Still inert: CreateOperation commits nothing.
        var operation = provisioner.CreateOperation(desired);

        try
        {
            // Step 4 — the one call that can change anything, through the same executor the create path uses,
            // so the write-ahead ledger row is committed before the provider is contacted.
            var resource = await _executor.ExecuteAsync(operation, jobId, ct).ConfigureAwait(false);
            return new UpdateApplyResult.Applied(resource, current.PlanHash, current.Strategy, current.DataImpact);
        }
        catch (ProvisioningExecutionException ex)
        {
            // Translated, not swallowed — the ledger row id travels to the caller so a sweep can find the row.
            return new UpdateApplyResult.Failed(ex.Message, ex.LedgerRowId, ex.Compensated);
        }
    }

    /// <inheritdoc />
    public async Task<DriftCheckResult> DetectDriftAsync(
        string provisionerId,
        ResourceHandle handle,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(handle);

        // Same type test as PlanUpdateAsync. An adapter that does not implement IMaintainer is not asked,
        // so it never has to answer a drift question with a stub that would read as "clean".
        if (Resolve(provisionerId) is not IMaintainer maintainer)
        {
            return new DriftCheckResult.Unsupported(provisionerId);
        }

        return new DriftCheckResult.Checked(await maintainer.DetectDriftAsync(handle, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProvisioningLedgerEntry>> ListLedgerEntriesAsync(CancellationToken ct = default)
    {
        if (_ledger is null)
        {
            return [];
        }

        var entries = new List<ProvisioningLedgerEntry>();
        foreach (var provisioner in _provisioners)
        {
            var intents = await _ledger.ListIntendedAsync(provisioner.ProvisionerId, ct).ConfigureAwait(false);
            foreach (var intent in intents)
            {
                // ListIntendedAsync's contract is "rows still in Intended", so the state is known exactly
                // rather than guessed — and so is the absence of a handle: the row was committed before the
                // provider was contacted, so there is no provider-assigned id to carry and none is invented.
                entries.Add(new ProvisioningLedgerEntry(intent, ResourceLifecycleState.Intended));
            }

            var created = await _ledger.ListCreatedAsync(provisioner.ProvisionerId, ct).ConfigureAwait(false);
            foreach (var row in created)
            {
                // The other end of the same lifecycle, and the reason this member reads both: these rows
                // carry the real provider-assigned id, so a caller can inspect the live resource instead of
                // scavenging an identifier out of the row's tags and hoping the provider agrees.
                entries.Add(new ProvisioningLedgerEntry(
                    new ProvisioningIntent(
                        LedgerRowId: row.LedgerRowId,
                        ProvisionerId: row.Handle.ProvisionerId,
                        Region: row.Handle.Region,
                        Tags: row.Handle.Tags,
                        JobId: row.JobId,
                        RecordedAt: row.RecordedAt),
                    ResourceLifecycleState.Created,
                    row.Handle));
            }
        }

        return [.. entries.OrderByDescending(e => e.Intent.RecordedAt)];
    }

    /// <summary>
    /// Finds the provisioner with <paramref name="provisionerId"/>, or throws.
    /// </summary>
    /// <remarks>
    /// Loud, not silent: substituting "whichever provisioner happens to be first" would plan — or, worse,
    /// create — against a provider the caller never chose.
    /// </remarks>
    private IProvisioner Resolve(string provisionerId)
    {
        var provisioner = _provisioners
            .FirstOrDefault(p => string.Equals(p.ProvisionerId, provisionerId, StringComparison.Ordinal));

        return provisioner ?? throw new InvalidOperationException(
            $"No provisioner is registered with id '{provisionerId}'. Registered ids: " +
            (_provisioners.Count == 0 ? "(none)" : string.Join(", ", _provisioners.Select(p => p.ProvisionerId))) + ".");
    }
}
