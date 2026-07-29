using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// Applies a single <see cref="IProvisioningOperation"/>, guaranteeing the write-ahead ordering the
/// provisioning subsystem depends on: intent is durable before the provider is asked to create anything,
/// and the ledger only advances to <c>Created</c> once the provider has confirmed an id.
/// </summary>
/// <remarks>
/// <para>
/// This is the "plan execution layer" the remarks on <see cref="IProvisioner"/> refer to. It exists so
/// <see cref="IProvisioner"/> never needs an <c>ApplyAsync</c>: provisioners describe and read, this type
/// mutates. It depends only on <c>Servyx.Domain</c> abstractions and therefore has no knowledge of Docker
/// or any other provider — the Docker container provisioner supplies an
/// <see cref="IProvisioningOperation"/> and this class never learns what is on the other side of it.
/// </para>
/// <para>
/// <strong>Failure is always surfaced.</strong> On any failure the executor attempts compensation and
/// then throws <see cref="ProvisioningExecutionException"/> carrying the ledger row id. It never returns
/// a null/empty result and never logs-and-continues: a partially created resource that the caller does
/// not hear about is exactly the failure mode the ledger exists to prevent. The ledger row is
/// deliberately left in <see cref="ResourceLifecycleState.Intended"/> rather than being deleted or marked
/// destroyed, so a later <see cref="IProvisioner.ReconcileAsync"/> sweep still has something to find.
/// </para>
/// </remarks>
public sealed class ProvisioningExecutor
{
    private readonly IProvisioningLedger _ledger;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProvisioningExecutor> _logger;

    /// <summary>
    /// Creates a <see cref="ProvisioningExecutor"/> writing to <paramref name="ledger"/>.
    /// </summary>
    /// <param name="ledger">The write-ahead ledger intent is committed to.</param>
    /// <param name="timeProvider">Clock used for ledger timestamps. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Optional logger. Logging never replaces surfacing a failure to the caller.</param>
    public ProvisioningExecutor(
        IProvisioningLedger ledger,
        TimeProvider? timeProvider = null,
        ILogger<ProvisioningExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        _ledger = ledger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProvisioningExecutor>.Instance;
    }

    /// <summary>
    /// Executes <paramref name="operation"/>, writing the write-ahead intent row first and advancing it
    /// to <c>Created</c> with the provider-assigned resource id afterwards.
    /// </summary>
    /// <param name="operation">The already-decided provider mutation to carry out.</param>
    /// <param name="jobId">The provisioning job this execution belongs to, recorded on the ledger row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created resource, including the transport target the rest of Servyx uses to reach it.</returns>
    /// <exception cref="ProvisioningExecutionException">
    /// Creation failed (compensation was attempted), or the ledger could not be advanced after a
    /// successful creation.
    /// </exception>
    public async Task<ProvisionedResource> ExecuteAsync(
        IProvisioningOperation operation,
        string? jobId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var intent = new ProvisioningIntent(
            LedgerRowId: Guid.NewGuid(),
            ProvisionerId: operation.ProvisionerId,
            Region: operation.Region,
            Tags: operation.Tags,
            JobId: jobId,
            RecordedAt: _timeProvider.GetUtcNow());

        // Step 1 — durable intent, including the tags about to be applied, BEFORE any mutating call.
        // If this throws, nothing has been created and the caller sees the storage failure unchanged.
        await _ledger.RecordIntentAsync(intent, ct).ConfigureAwait(false);

        ProvisionedResource resource;
        try
        {
            // Step 2 — the single mutating provider call.
            resource = await operation.CreateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception createException)
        {
            // Step 5 — compensate, then surface. Never swallow: the row stays Intended so a later
            // ReconcileAsync sweep can still find whatever the provider may have kept.
            var compensated = true;
            Exception failure = createException;
            try
            {
                await operation.CompensateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception compensationException)
            {
                compensated = false;
                failure = new AggregateException(createException, compensationException);
            }

            _logger.LogError(
                createException,
                "Provisioning create failed for {ProvisionerId}; ledger row {LedgerRowId} left in Intended (compensated: {Compensated}).",
                operation.ProvisionerId,
                intent.LedgerRowId,
                compensated);

            throw new ProvisioningExecutionException(
                compensated
                    ? $"Provisioning failed for '{operation.ProvisionerId}'. The partial resource was removed; ledger row {intent.LedgerRowId} remains Intended for reconciliation."
                    : $"Provisioning failed for '{operation.ProvisionerId}' and compensation also failed. A resource may still exist at the provider; ledger row {intent.LedgerRowId} remains Intended for reconciliation.",
                intent.LedgerRowId,
                compensated,
                failure);
        }

        try
        {
            // Steps 3 & 4 — record the real provider-assigned id, then hand the resource back.
            await _ledger
                .MarkCreatedAsync(intent.LedgerRowId, resource.Handle.ProviderResourceId, _timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ledgerException)
        {
            // The resource exists and is healthy, so compensation would be wrong here — but the caller
            // must still be told, because the row is now stale and only a sweep can repair it.
            throw new ProvisioningExecutionException(
                $"Resource '{resource.Handle.ProviderResourceId}' was created at '{operation.ProvisionerId}' but ledger row {intent.LedgerRowId} could not be advanced to Created. The row remains Intended for reconciliation.",
                intent.LedgerRowId,
                compensated: false,
                ledgerException);
        }

        return resource;
    }
}
