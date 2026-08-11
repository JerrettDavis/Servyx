using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Domain.Configuration;

/// <summary>
/// One persisted plan and its ordered actions, read back as a unit.
/// </summary>
/// <remarks>
/// A plan without its actions is not a useful answer to any question this store is asked — apply walks the
/// actions in <see cref="ChangePlanActionRecord.Ordinal"/> order, revert walks their recorded pre-images, and
/// a preview read back for display renders their diffs. Returning the pair together therefore also removes
/// the only way for a caller to forget the second query.
/// </remarks>
/// <param name="Plan">The plan row.</param>
/// <param name="Actions">Its actions, already ordered by <see cref="ChangePlanActionRecord.Ordinal"/>.</param>
public sealed record StoredChangePlan(ChangePlanRecord Plan, IReadOnlyList<ChangePlanActionRecord> Actions);

/// <summary>
/// Durable storage for the <see cref="ChangePlanRecord"/>/<see cref="ChangePlanActionRecord"/> pair a
/// <see cref="IPlanExecutor.PreviewAsync"/> call produces.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this interface exists rather than an <c>IPlanExecutor</c> that talks to EF directly.</strong>
/// The preview engine is a configuration concern — surface resolution, adapters, codecs, merging, diffing —
/// and lives alongside those in <c>Servyx.Config</c>, which references <c>Servyx.Domain</c> and nothing else.
/// Persisting the result is a storage concern owned by <c>Servyx.Infrastructure.Persistence</c>. This is the
/// same split <see cref="IServerSettingsService"/> already makes for desired values, and it is what lets the
/// preview engine be tested against an in-memory store without a database, while the durable
/// implementation is tested against the real migrated schema.
/// </para>
/// <para>
/// <strong>Deliberately narrow, and deliberately not a repository.</strong> There is no update, no delete,
/// and no query-by-server here. A later apply/revert phase needs status transitions guarded by
/// <see cref="ChangePlanRecord.RowVersion"/>, and a server page needs a "recent plans" listing; both are real
/// and both are absent, because adding an unused member now would mean guessing at the concurrency contract
/// the apply phase has not yet decided. Preview writes and reads back by id; that is the whole of it.
/// </para>
/// </remarks>
public interface IChangePlanStore
{
    /// <summary>
    /// Persists <paramref name="plan"/> together with <paramref name="actions"/> as one unit of work.
    /// </summary>
    /// <remarks>
    /// Atomic by contract: a plan row without its actions would be a plan an apply could read and then
    /// execute as a no-op, which is strictly worse than no plan at all. Every action's
    /// <see cref="ChangePlanActionRecord.ChangePlanId"/> must already be <paramref name="plan"/>'s id — this
    /// method stores what it is given and re-parents nothing.
    /// </remarks>
    /// <param name="plan">The plan row to insert.</param>
    /// <param name="actions">Its actions, in execution order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default);

    /// <summary>
    /// The stored plan with the given id and its ordered actions, or <see langword="null"/> when no such plan
    /// exists.
    /// </summary>
    /// <remarks>
    /// A missing plan is a supported answer, not an error: a plan id can outlive its row (its server was
    /// forgotten, and the cascade delete took the plan with it), and a caller holding a stale id from a
    /// browser tab needs to be told "gone" rather than handed an exception.
    /// </remarks>
    /// <param name="id">The plan id, as returned in <see cref="ConfigChangePlan.Id"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default);
}
