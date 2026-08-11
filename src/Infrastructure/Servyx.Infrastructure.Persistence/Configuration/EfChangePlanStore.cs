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
    /// <strong>Two queries, projected, never entities.</strong> Following <see cref="TryGetAsync"/>'s own
    /// two-query shape (plans, then their actions) rather than a per-plan query — the whole point of a
    /// listing is that it can be for many plans, so an N+1 here would scale with <paramref name="limit"/>.
    /// Both queries use <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/> and end in a
    /// <c>Select</c> that names only the columns <see cref="ChangePlanSummary"/>/<see cref="ChangePlanActionSummary"/>
    /// need, so <see cref="ChangePlanActionRecord.PreImageContent"/>, <see cref="ChangePlanActionRecord.PostImageContent"/>,
    /// and <see cref="ChangePlanActionRecord.UnifiedDiff"/> are never read off disk for a list view, let alone
    /// materialized into a tracked entity first and discarded.
    /// </para>
    /// <para>
    /// <strong>The ordering and the <paramref name="limit"/> happen in SQL, over
    /// <see cref="ChangePlanRecord.CreatedAtTicks"/>.</strong> They used to happen in memory, after a
    /// <c>ToListAsync</c> that materialized every plan a server had ever had in order to return the newest
    /// twenty-five, because the SQLite provider refuses to translate an <c>ORDER BY</c> over a
    /// <see cref="DateTimeOffset"/> column at all (<c>NotSupportedException</c>). The workaround for that is a
    /// sortable twin column, not a client-side sort: plans accumulate until the retention sweep removes them —
    /// the sweep exists precisely because they do — so "short-lived and few per server" was never a property
    /// anything enforced. <c>(ServerId, CreatedAtTicks)</c> is indexed together, in that order, so the filter
    /// and the sort are one index range.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ChangePlanSummary>> ListRecentAsync(
        ServerId serverId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var plans = await context.ChangePlans.AsNoTracking()
            .Where(row => row.ServerId == serverId)
            .OrderByDescending(row => row.CreatedAtTicks)
            // The tiebreak for two plans previewed in the same instant, so a history view is deterministically
            // ordered rather than merely usually ordered. Ordered by the mapped Id column — the value
            // converter puts a ChangePlanId on disk as its underlying Guid, and it is that column the database
            // sorts, so the record struct never needs an IComparable of its own.
            .ThenByDescending(row => row.Id)
            .Take(limit)
            .Select(row => new
            {
                row.Id,
                row.ServerId,
                row.Status,
                row.CreatedAt,
                row.CreatedBy,
                row.AppliedAt,
                row.AppliedBy,
                row.RevertedAt,
                row.RevertedBy,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        if (plans.Count == 0)
        {
            return [];
        }

        var planIds = plans.ConvertAll(row => row.Id);

        var actions = await context.ChangePlanActions.AsNoTracking()
            .Where(row => planIds.Contains(row.ChangePlanId))
            .OrderBy(row => row.Ordinal)
            .Select(row => new
            {
                row.ChangePlanId,
                row.Id,
                row.Ordinal,
                row.SurfaceId,
                row.ResolvedPath,
                row.Kind,
                row.Status,
                row.WriteReachedServer,
                row.PostImageHash,
                row.ObservedPostImageHash,
                row.PostWriteVerification,
                row.FailureReason,
                row.AppliedAt,
                row.RevertedAt,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var actionsByPlan = actions
            .GroupBy(row => row.ChangePlanId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ChangePlanActionSummary>)group
                    .Select(row => new ChangePlanActionSummary(
                        row.Id,
                        row.Ordinal,
                        row.SurfaceId,
                        row.ResolvedPath,
                        row.Kind,
                        row.Status,
                        row.WriteReachedServer,
                        row.PostImageHash,
                        row.ObservedPostImageHash,
                        row.PostWriteVerification,
                        row.FailureReason,
                        row.AppliedAt,
                        row.RevertedAt))
                    .ToList());

        return plans
            .ConvertAll(row => new ChangePlanSummary(
                row.Id,
                row.ServerId,
                row.Status,
                row.CreatedAt,
                row.CreatedBy,
                row.AppliedAt,
                row.AppliedBy,
                row.RevertedAt,
                row.RevertedBy,
                actionsByPlan.TryGetValue(row.Id, out var list) ? list : []));
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
    /// <para>
    /// <strong>The two image columns are excluded from the <c>UPDATE</c>, and that exclusion is a safety
    /// property rather than an optimization.</strong> An action arrives here detached, carrying whatever
    /// <see cref="ChangePlanActionRecord.PreImageContent"/>/<see cref="ChangePlanActionRecord.PostImageContent"/>
    /// it held when the caller read it — and an apply or a revert holds that snapshot across many seconds of
    /// live I/O, during which <see cref="PurgeImagesAsync"/> can run and null those columns. A whole-row
    /// attach would then write the caller's stale, unmasked, possibly secret-bearing copy straight back over
    /// the purge, silently undoing the retention guarantee. Nothing legitimately writes those two columns
    /// through this method: they are captured once at preview time by <see cref="SaveAsync"/>'s insert, and
    /// cleared only by the sweep, which uses its own tracked read. Marking them unmodified makes the
    /// resurrection impossible by construction rather than by getting every sweep predicate right.
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
            var entry = context.ChangePlanActions.Attach(action);
            entry.State = EntityState.Modified;

            entry.Property(row => row.PreImageContent).IsModified = false;
            entry.Property(row => row.PostImageContent).IsModified = false;
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
    /// is <see cref="ChangePlanStatus.Previewed"/> and unexpired, <see cref="ChangePlanStatus.Applying"/>, or
    /// <see cref="ChangePlanStatus.Reverting"/> is never read here at all, so this sweep cannot interfere with
    /// an apply or a revert in flight.
    /// </para>
    /// </remarks>
    public async Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
        DateTimeOffset now,
        TimeSpan imageRetention,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageRetention, TimeSpan.Zero);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Everything except a plan whose apply or revert is IN FLIGHT. Both in-flight statuses are named
        // because both are non-terminal claims over a live server: Applying still needs its post-image to
        // write, and Reverting still needs its pre-image to restore from. Purging either mid-flight destroys
        // the bytes the operation is in the middle of using.
        //
        // Note the shape deliberately changed from "not Applying" to an explicit exclusion of both. The old
        // form was written as a single negation on the theory that a status added later would be swept by
        // default rather than silently exempted — and then Reverting was added and swept by default, which is
        // exactly the bug that theory was meant to prevent. Non-terminal statuses must be opted OUT by name;
        // there are two of them and adding a third is a decision, not an oversight.
        //
        // The expiry comparison is deliberately NOT in the SQL. ExpiresAt is a DateTimeOffset, whose ordering
        // comparison is provider-dependent (the SQLite provider refuses to translate it at all), and a sweep
        // that quietly matched nothing on one provider would look exactly like a sweep with nothing to do.
        var candidates = await context.ChangePlans
            .Where(row => row.Status != ChangePlanStatus.Applying
                && row.Status != ChangePlanStatus.Reverting)
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
