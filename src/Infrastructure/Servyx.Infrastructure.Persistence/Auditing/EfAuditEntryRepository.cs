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

    /// <inheritdoc />
    public async Task<AuditEntryPage> SearchAsync(
        AuditEntryFilter filter, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Actor equality and Action-prefix (StartsWith) are both plain string predicates over indexed-enough,
        // narrow columns, and EF Core's SQLite provider translates both to a real SQL WHERE clause — so a
        // filtered search (the common case once this table has any real history) narrows at the database
        // rather than pulling the whole table across for every request, the concern ListRecentAsync's own
        // remarks about a client-side sort do NOT excuse away for a search this reader UI calls on every
        // filter change and every page turn.
        IQueryable<AuditEntry> query = context.AuditEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            query = query.Where(row => row.Actor == filter.Actor);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionPrefix))
        {
            query = query.Where(row => row.Action.StartsWith(filter.ActionPrefix));
        }

        // Materialized here, BEFORE the TimestampUtc range filter and the newest-first sort — deliberately
        // consistent with ListRecentAsync's own remarks on why ORDER BY over this column is done client-side:
        // EF Core's SQLite provider does not reliably translate DateTimeOffset operations (comparison
        // included) either, so a range filter pushed into the same Where chain above risks the identical
        // translation failure for a subtly different reason each time it's touched. Actor/ActionPrefix already
        // did the load-bearing narrowing above; what's left here is small enough, for this table's current
        // scale, to sort, range-filter, and page in memory — exactly what ListRecentAsync already does for its
        // own, simpler read.
        var matches = await query.ToListAsync(ct).ConfigureAwait(false);

        IEnumerable<AuditEntry> ranged = matches;
        if (filter.FromUtc is { } from)
        {
            ranged = ranged.Where(row => row.TimestampUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            ranged = ranged.Where(row => row.TimestampUtc <= to);
        }

        var ordered = ranged.OrderByDescending(row => row.TimestampUtc).ToList();
        var page = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

        return new AuditEntryPage(page, ordered.Count);
    }
}
