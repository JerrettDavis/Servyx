using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Auditing;

/// <summary>
/// The durable <see cref="IAuditEntryRepository"/>, backed by the <c>AuditEntries</c> table via
/// <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <c>EfUserRepository</c>'s pattern exactly: this type is registered singleton (its consumer,
/// <c>Servyx.Application.Auditing.AuditLogger</c>, is registered the same way — see the composition root), and
/// <see cref="ServyxDbContext"/> is registered scoped, so a singleton cannot hold it directly. The factory is
/// itself singleton-safe and creates a short-lived context per call, one unit of work each.
/// </remarks>
public sealed class EfAuditEntryRepository : IAuditEntryRepository
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a repository that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfAuditEntryRepository(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task AddAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.AuditEntries.Add(entry);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Ordered, then materialized, client-side: EF Core's SQLite provider does not translate an ORDER BY
        // over a DateTimeOffset column to SQL (the same limitation ChangePlanRecord.CreatedAtTicks exists to
        // work around for its own "recent plans" listing — see that property's own remarks). Audit entries
        // are read far less often, and in far smaller pages, than change plans are, so a denormalized ticks
        // column was judged unwarranted complexity for this table; a client-side sort over an already-narrow
        // `Take` is fine here. Revisit if this table's read path ever needs to scale past that.
        var rows = await context.AuditEntries.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        return rows
            .OrderByDescending(row => row.TimestampUtc)
            .Take(limit)
            .ToList();
    }
}
