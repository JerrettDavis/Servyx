using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Provisioning;

/// <summary>
/// The durable <see cref="IProvisioningLedger"/>, backed by the <c>ProvisionedResources</c> table via
/// <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Intent before effect.</strong> <see cref="RecordIntentAsync"/> inserts the row in
/// <see cref="ResourceLifecycleState.Intended"/> — tags included, provider resource id deliberately null,
/// because the provider has not been asked yet — and does not return until
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> has committed it. Only then may the caller
/// issue the billable provider call, and only afterwards does <see cref="MarkCreatedAsync"/> stamp the row
/// with the real id. See <see cref="ProvisionedResourceRecord"/> for why the null id is the point rather
/// than a modelling accident.
/// </para>
/// <para>
/// <strong>One unit of work per call.</strong> Every method here commits, so this type should be resolved
/// into its own scope rather than sharing a context with unrelated pending work: a
/// <see cref="RecordIntentAsync"/> that also flushed a half-finished aggregate would turn the ledger's one
/// guarantee into "whatever else happened to be tracked also got written". Reads are untracked for the same
/// reason — the sweep only needs values.
/// </para>
/// </remarks>
public sealed class EfProvisioningLedger : IProvisioningLedger
{
    private readonly ServyxDbContext _context;

    /// <summary>Creates a ledger writing to <paramref name="context"/>.</summary>
    /// <param name="context">The Servyx control-plane database this ledger commits to.</param>
    public EfProvisioningLedger(ServyxDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public async Task RecordIntentAsync(ProvisioningIntent intent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        _context.ProvisionedResources.Add(new ProvisionedResourceRecord
        {
            Id = intent.LedgerRowId,
            ProvisionerId = intent.ProvisionerId,

            // Null on purpose: nothing has been created yet, so nothing has an id yet.
            ProviderResourceId = null,
            Region = intent.Region,

            // Copied rather than stored by reference so a caller mutating its own dictionary after the call
            // cannot retroactively change what the ledger claims was applied.
            Tags = new Dictionary<string, string>(intent.Tags, StringComparer.Ordinal),
            State = ResourceLifecycleState.Intended,
            JobId = intent.JobId,
            CreatedAt = intent.RecordedAt,
            UpdatedAt = intent.RecordedAt,
        });

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// No row exists for <paramref name="ledgerRowId"/>. That means a resource was created without its
    /// intent ever having been committed, which is the exact ordering violation this ledger exists to
    /// prevent, so it is surfaced rather than repaired by inserting a row after the fact.
    /// </exception>
    public async Task MarkCreatedAsync(
        Guid ledgerRowId,
        string providerResourceId,
        DateTimeOffset observedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerResourceId);

        var record = await _context.ProvisionedResources
            .SingleOrDefaultAsync(row => row.Id == ledgerRowId, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            throw new InvalidOperationException(
                $"No provisioning ledger row exists for {ledgerRowId}; intent must be recorded before creation.");
        }

        record.State = ResourceLifecycleState.Created;
        record.ProviderResourceId = providerResourceId;
        record.UpdatedAt = observedAt;

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProvisioningIntent>> ListIntendedAsync(
        string provisionerId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

        // Hits the standalone index on State (see ProvisionedResourceRecordConfiguration): this is the
        // orphan sweep's entry query and must not degrade into a full table scan as the ledger grows.
        var records = await _context.ProvisionedResources
            .AsNoTracking()
            .Where(row => row.State == ResourceLifecycleState.Intended && row.ProvisionerId == provisionerId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Ordered oldest-first here rather than in SQL: SQLite cannot ORDER BY a DateTimeOffset column, and
        // the model has to stay provider-agnostic (see the remarks on ServyxDbContext), so a server-side
        // OrderBy would be a PostgreSQL-only query. The result set is one provisioner's unresolved intents —
        // bounded by how many creates crashed mid-flight — so sorting it in memory costs nothing.
        return records
            .OrderBy(record => record.CreatedAt)
            .Select(ToIntent)
            .ToList();
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// A row is in <see cref="ResourceLifecycleState.Created"/> with no <c>ProviderResourceId</c>. Only
    /// <see cref="MarkCreatedAsync"/> writes that state and it refuses a blank id, so such a row is corrupt
    /// rather than merely incomplete — and the one thing this method must never do is answer with a handle
    /// naming some other resource.
    /// </exception>
    public async Task<IReadOnlyList<ProvisionedResourceRow>> ListCreatedAsync(
        string provisionerId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

        // Same standalone index on State that ListIntendedAsync relies on, filtered to the other end of the
        // lifecycle: the rows whose provider-assigned id Servyx actually learned.
        var records = await _context.ProvisionedResources
            .AsNoTracking()
            .Where(row => row.State == ResourceLifecycleState.Created && row.ProvisionerId == provisionerId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Ordered in memory for exactly the reason given on ListIntendedAsync above: SQLite cannot ORDER BY a
        // DateTimeOffset column, and the server-side workarounds are PostgreSQL-only, which this model must
        // not become. The set is one provisioner's confirmed resources, so sorting it here costs nothing.
        return records
            .OrderBy(record => record.CreatedAt)
            .Select(ToCreatedRow)
            .ToList();
    }

    private static ProvisioningIntent ToIntent(ProvisionedResourceRecord record) => new(
        LedgerRowId: record.Id,
        ProvisionerId: record.ProvisionerId,
        Region: record.Region,
        Tags: record.Tags,
        JobId: record.JobId,
        RecordedAt: record.CreatedAt);

    /// <summary>
    /// Projects a confirmed row into the domain shape, refusing rather than fabricating an identity if the
    /// provider id the state promises is not actually there.
    /// </summary>
    private static ProvisionedResourceRow ToCreatedRow(ProvisionedResourceRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ProviderResourceId))
        {
            // Loud, and specifically not "skip the row" or "fall back to a tag". A Created row with no
            // provider id is a broken invariant, and quietly dropping it would report a resource Servyx owns
            // as one it does not — the same class of silence the ledger exists to prevent.
            throw new InvalidOperationException(
                $"Provisioning ledger row {record.Id} is in {nameof(ResourceLifecycleState.Created)} but records no "
                + "ProviderResourceId. Only MarkCreatedAsync writes that state and it rejects a blank id, so this "
                + "row was not written by the ledger.");
        }

        return new ProvisionedResourceRow(
            LedgerRowId: record.Id,
            Handle: new ResourceHandle(
                record.ProvisionerId,
                record.ProviderResourceId,
                record.Region,
                record.Tags),
            JobId: record.JobId,
            RecordedAt: record.CreatedAt,
            ConfirmedAt: record.UpdatedAt);
    }
}
