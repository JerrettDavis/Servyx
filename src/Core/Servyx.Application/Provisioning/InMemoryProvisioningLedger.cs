using System.Collections.Concurrent;
using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// A process-local, <strong>non-durable</strong> <see cref="IProvisioningLedger"/> for tests only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not a ledger, and it must never be used where crash-survival matters.</strong> The
/// entire value of the ledger is that an <see cref="ResourceLifecycleState.Intended"/> row survives a
/// crash between the create call and its acknowledgement. This type is a dictionary living inside the very
/// process that makes that call: the moment that process dies — the only moment the ledger exists for —
/// every row dies with it, and any resource the provider did create is left billing with no local trace at
/// all. It offers exactly zero of the guarantee its interface documents.
/// </para>
/// <para>
/// It exists so <see cref="ProvisioningExecutor"/> and its unit tests can exercise the write-ahead
/// ordering without a database. Do not register it in a composition root that provisions real, billable
/// resources: use the durable <c>EfProvisioningLedger</c> from
/// <c>Servyx.Infrastructure.Persistence</c> instead.
/// </para>
/// </remarks>
public sealed class InMemoryProvisioningLedger : IProvisioningLedger
{
    private readonly ConcurrentDictionary<Guid, Row> _rows = new();

    /// <inheritdoc />
    public Task RecordIntentAsync(ProvisioningIntent intent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ct.ThrowIfCancellationRequested();

        _rows[intent.LedgerRowId] = new Row(intent, ResourceLifecycleState.Intended, ProviderResourceId: null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkCreatedAsync(Guid ledgerRowId, string providerResourceId, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerResourceId);
        ct.ThrowIfCancellationRequested();

        if (!_rows.TryGetValue(ledgerRowId, out var row))
        {
            throw new InvalidOperationException($"No provisioning ledger row exists for {ledgerRowId}; intent must be recorded before creation.");
        }

        _rows[ledgerRowId] = row with
        {
            State = ResourceLifecycleState.Created,
            ProviderResourceId = providerResourceId,
            ConfirmedAt = observedAt,
        };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProvisioningIntent>> ListIntendedAsync(string provisionerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<ProvisioningIntent> result = _rows.Values
            .Where(r => r.State == ResourceLifecycleState.Intended
                && string.Equals(r.Intent.ProvisionerId, provisionerId, StringComparison.Ordinal))
            .Select(r => r.Intent)
            .ToList();

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers from the same dictionary everything else here answers from, so it inherits the same total
    /// absence of durability: these rows exist only until the process does. A confirmed resource listed from
    /// here is a confirmed resource this process happens to remember, not one that would survive a restart.
    /// </remarks>
    public Task<IReadOnlyList<ProvisionedResourceRow>> ListCreatedAsync(string provisionerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<ProvisionedResourceRow> result = _rows.Values
            .Where(r => r.State == ResourceLifecycleState.Created
                && r.ProviderResourceId is not null
                && string.Equals(r.Intent.ProvisionerId, provisionerId, StringComparison.Ordinal))
            .OrderBy(r => r.Intent.RecordedAt)
            .Select(r => new ProvisionedResourceRow(
                LedgerRowId: r.Intent.LedgerRowId,
                Handle: new ResourceHandle(
                    r.Intent.ProvisionerId,
                    r.ProviderResourceId!,
                    r.Intent.Region,
                    r.Intent.Tags),
                JobId: r.Intent.JobId,
                RecordedAt: r.Intent.RecordedAt,
                ConfirmedAt: r.ConfirmedAt ?? r.Intent.RecordedAt))
            .ToList();

        return Task.FromResult(result);
    }

    /// <summary>Reads back the recorded state of a row, for inspection by tests and diagnostics.</summary>
    public (ResourceLifecycleState State, string? ProviderResourceId)? TryGetRow(Guid ledgerRowId) =>
        _rows.TryGetValue(ledgerRowId, out var row) ? (row.State, row.ProviderResourceId) : null;

    private sealed record Row(
        ProvisioningIntent Intent,
        ResourceLifecycleState State,
        string? ProviderResourceId,
        DateTimeOffset? ConfirmedAt = null);
}
