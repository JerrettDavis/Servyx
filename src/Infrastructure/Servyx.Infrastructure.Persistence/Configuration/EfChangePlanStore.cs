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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The rows arrive detached (<see cref="TryGetAsync"/> reads them no-tracking) and are attached here,
    /// which snapshots their current values as EF's original values. <c>ServyxDbContext.SaveChangesAsync</c>
    /// then rotates <see cref="ChangePlanRecord.RowVersion"/> to a fresh <see cref="Guid"/> — changing the
    /// CURRENT value only — so the generated <c>UPDATE</c> carries the token the caller read in its
    /// <c>WHERE</c> clause and writes the new one. A racing attempt holding the same original token therefore
    /// matches zero rows and EF raises <see cref="DbUpdateConcurrencyException"/>, which is translated below.
    /// </para>
    /// <para>
    /// The rotation mutates the very instance the caller passed in, which is what satisfies this member's
    /// contract that the token is refreshed in place for a subsequent update.
    /// </para>
    /// </remarks>
    public async Task UpdateAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(actions);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.ChangePlans.Attach(plan).State = EntityState.Modified;
        foreach (var action in actions)
        {
            context.ChangePlanActions.Attach(action).State = EntityState.Modified;
        }

        try
        {
            await context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ChangePlanConcurrencyException(
                $"Change plan '{plan.Id}' was modified by another apply attempt since it was read, so this "
                + "transition was rejected and nothing was written. Re-read the plan before acting on it; if "
                + "it now reads as applied, it has already been applied once and must not be applied again.",
                plan.Id.ToString(),
                ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One context, one <c>SaveChanges</c>, tracked (not <c>ExecuteUpdate</c>): the retention decision is a
    /// per-plan judgement over its actions' states, which is a shape a set-based bulk update cannot express
    /// without duplicating the rule in provider-translatable form and letting the two drift.
    /// </para>
    /// <para>
    /// Only candidate plans are loaded — plans that are terminal, or expired and never applied. A plan that
    /// is <see cref="ChangePlanStatus.Previewed"/> and unexpired, or <see cref="ChangePlanStatus.Applying"/>,
    /// is never read here at all, so this sweep cannot interfere with an apply in flight.
    /// </para>
    /// </remarks>
    public async Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
        DateTimeOffset now,
        TimeSpan imageRetention,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageRetention, TimeSpan.Zero);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Everything except a plan whose apply is in flight. Expressed as "not Applying" rather than as a
        // list of terminal statuses so that a status added later is swept by default rather than silently
        // exempted; Previewed rows are then filtered by expiry below, in memory.
        //
        // The expiry comparison is deliberately NOT in the SQL. ExpiresAt is a DateTimeOffset, whose ordering
        // comparison is provider-dependent, and a sweep that quietly matched nothing on one provider would
        // look exactly like a sweep with nothing to do. Change plans are short-lived and few; loading them is
        // cheaper than a provider-specific predicate nobody would notice failing.
        var candidates = await context.ChangePlans
            .Where(row => row.Status != ChangePlanStatus.Applying)
            .ToListAsync(ct).ConfigureAwait(false);

        candidates.RemoveAll(row => row.Status == ChangePlanStatus.Previewed && row.ExpiresAt > now);

        if (candidates.Count == 0)
        {
            return ChangePlanImagePurgeResult.Nothing;
        }

        var ids = candidates.ConvertAll(row => row.Id);
        var actionsByPlan = (await context.ChangePlanActions
                .Where(row => ids.Contains(row.ChangePlanId))
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(row => row.ChangePlanId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var markedStale = 0;
        var plansPurged = 0;
        var actionsPurged = 0;

        foreach (var plan in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (plan.Status == ChangePlanStatus.Previewed)
            {
                // Its TTL elapsed and nothing ever applied it. ApplyAsync already refuses an expired plan;
                // recording that fact durably is what stops a stale approval from looking applicable in a
                // browser tab that has been open since before it expired.
                plan.Status = ChangePlanStatus.Stale;
                markedStale++;
            }

            var actions = actionsByPlan.TryGetValue(plan.Id, out var list) ? list : [];

            // THE SAFETY PREDICATE. Derived from what the ACTIONS record, never from plan.Status: a plan whose
            // status says "Failed" but which has a landed action really did change a live server, and its
            // pre-image is the only way back. Getting this backwards destroys data that cannot be recreated.
            //
            // WriteReachedServer, not Status, is the primary term, and the difference is the whole reason that
            // column exists. A post-write fidelity mismatch on action #0 leaves that action Failed and every
            // later one Skipped — NOTHING in the plan says Applied — while a live server holds bytes nobody
            // approved. That is precisely the case where the pre-image matters most, and a Status-only
            // predicate purges it on the next sweep. See ChangePlanActionRecord.WriteReachedServer.
            //
            // Status == Applied is kept as a second term purely as a belt: rows written before that column
            // existed default it to false, and an Applied action that somehow lacks the flag is a
            // contradiction that must resolve towards keeping the data, never towards destroying it.
            var somethingLanded = actions.Exists(a =>
                a.WriteReachedServer || a.Status == ChangePlanActionStatus.Applied);
            if (somethingLanded && now - EffectAnchor(plan) < imageRetention)
            {
                continue;
            }

            // NOTE FOR THE REVERT PHASE: once this runs, PreImageContent is gone and the plan is
            // unrevertable. RevertAsync must REFUSE such a plan with a message saying its images were purged
            // under the retention window — never silently succeed, never revert the subset that still has
            // images. A half-reverted server nobody was told about is worse than a refusal.
            var purgedHere = 0;
            foreach (var action in actions)
            {
                if (action.PreImageContent is null && action.PostImageContent is null)
                {
                    continue;
                }

                // Content only. PreImageHash/PostImageHash/ObservedPostImageHash stay — they are digests, not
                // secrets, and they are what lets an operator (or an audit) still say what the file was, and
                // whether what landed matched what was approved, long after the bytes go.
                action.PreImageContent = null;
                action.PostImageContent = null;
                purgedHere++;
            }

            if (purgedHere > 0)
            {
                plansPurged++;
                actionsPurged += purgedHere;
            }
        }

        if (markedStale == 0 && actionsPurged == 0)
        {
            return ChangePlanImagePurgeResult.Nothing;
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ChangePlanImagePurgeResult(markedStale, plansPurged, actionsPurged);
    }

    /// <summary>
    /// The moment a plan's effect on the server is dated from, for retention purposes.
    /// </summary>
    /// <remarks>
    /// A reverted plan is anchored at its revert (the later event), an applied one at its apply. The
    /// <see cref="ChangePlanRecord.ExpiresAt"/> fallback is defensive rather than expected: a row that landed
    /// a write without recording when must still age out eventually, rather than holding plaintext secrets
    /// forever because a timestamp was missed.
    /// </remarks>
    private static DateTimeOffset EffectAnchor(ChangePlanRecord plan) =>
        plan.RevertedAt ?? plan.AppliedAt ?? plan.ExpiresAt;
}
