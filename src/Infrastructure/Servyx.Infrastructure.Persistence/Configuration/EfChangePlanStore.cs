using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configuration;

/// <summary>
/// The durable <see cref="IChangePlanStore"/>, backed by the <c>ChangePlans</c> and <c>ChangePlanActions</c>
/// tables via <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <see cref="EfServerSettingsService"/>'s pattern exactly and for the same reason: this type is
/// registered singleton (its consumer, <c>PlanExecutor</c>, is itself a singleton alongside the rest of the
/// configuration engine), and a singleton cannot hold a scoped context. The factory creates a short-lived
/// context per call, one unit of work each.
/// </para>
/// <para>
/// <strong>The save is one transaction, deliberately.</strong> A plan row and its action rows are added to a
/// single context and committed by a single <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>, so
/// there is no window in which a plan exists without the actions that give it meaning — a plan an apply could
/// read and execute as a silent no-op.
/// </para>
/// <para>
/// <strong>Reads are <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>.</strong> Nothing
/// in the preview path mutates a plan it read back; the apply phase that will needs its own tracked read with
/// the <see cref="ChangePlanRecord.RowVersion"/> concurrency token in play, and inheriting a tracked graph
/// from here would make that decision implicitly rather than explicitly.
/// </para>
/// </remarks>
public sealed class EfChangePlanStore : IChangePlanStore
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a store that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfChangePlanStore(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(actions);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.ChangePlans.Add(plan);
        if (actions.Count > 0)
        {
            context.ChangePlanActions.AddRange(actions);
        }

        // RowVersion is assigned by ServyxDbContext.SaveChangesAsync immediately before the write — an
        // application-computed concurrency token rather than a provider-specific store-generated one, so the
        // same code works on SQLite and PostgreSQL alike. See ChangePlanRecordConfiguration's own remarks.
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var plan = await context.ChangePlans.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);

        if (plan is null)
        {
            return null;
        }

        var actions = await context.ChangePlanActions.AsNoTracking()
            .Where(row => row.ChangePlanId == id)
            .OrderBy(row => row.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);

        return new StoredChangePlan(plan, actions);
    }
}
