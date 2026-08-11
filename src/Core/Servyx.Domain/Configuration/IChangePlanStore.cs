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
/// <strong>Still deliberately narrow, and still not a repository.</strong> There is no delete and no
/// query-by-server here. <see cref="UpdateAsync"/> and <see cref="PurgeImagesAsync"/> were added by the apply
/// phase, which is when the concurrency contract this interface previously declined to guess at was actually
/// decided; a "recent plans" listing for a server page remains absent because nothing needs it yet.
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

    /// <summary>
    /// Persists mutations to an already-stored <paramref name="plan"/> and, optionally, to some of its
    /// actions, as one unit of work guarded by <see cref="ChangePlanRecord.RowVersion"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the member that makes a double-apply impossible.</strong> The concurrency token the
    /// caller read with <see cref="TryGetAsync"/> is the one the update is conditioned on, so two attempts
    /// that both observed the same <see cref="ChangePlanStatus.Previewed"/> row race here and exactly one
    /// wins; the loser gets a <see cref="ChangePlanConcurrencyException"/> rather than silently applying the
    /// plan a second time. A caller that checks <see cref="ChangePlanRecord.Status"/> before calling this is
    /// doing something useful but not sufficient — the check and the write are not atomic, and this is.
    /// </para>
    /// <para>
    /// <strong><paramref name="plan"/>'s token is rotated in place on success.</strong> An implementation
    /// must leave the passed instance carrying the freshly written
    /// <see cref="ChangePlanRecord.RowVersion"/>, so an apply that transitions the same plan several times
    /// (Applying, then Applied) can keep using the object it already holds instead of re-reading between
    /// every step.
    /// </para>
    /// <para>
    /// <strong>Actions are not concurrency-guarded individually, and do not need to be.</strong>
    /// <see cref="ChangePlanActionRecord"/> carries no token: the plan row is the gate every writer must pass
    /// through first, so whoever holds the plan's current token is by construction the only writer for its
    /// actions.
    /// </para>
    /// </remarks>
    /// <param name="plan">The plan row to update. Must already exist; its current <see cref="ChangePlanRecord.RowVersion"/> is the expected token.</param>
    /// <param name="actions">
    /// The action rows to update alongside it. May be empty when only the plan row changed. Every entry must
    /// already belong to <paramref name="plan"/> — this method re-parents nothing.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ChangePlanConcurrencyException">
    /// The stored row no longer carries <paramref name="plan"/>'s <see cref="ChangePlanRecord.RowVersion"/>:
    /// someone else transitioned it first. Nothing was written.
    /// </exception>
    Task UpdateAsync(
        ChangePlanRecord plan,
        IReadOnlyList<ChangePlanActionRecord> actions,
        CancellationToken ct = default);

    /// <summary>
    /// One retention sweep: promotes expired, never-applied plans to <see cref="ChangePlanStatus.Stale"/> and
    /// discards the recorded pre-/post-image content of every plan that no longer needs it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists at all.</strong> <see cref="ChangePlanActionRecord.PreImageContent"/> and
    /// <see cref="ChangePlanActionRecord.PostImageContent"/> hold whole configuration files verbatim and
    /// UNMASKED — including, when <see cref="ChangePlanActionRecord.ContainsSecrets"/> is set, the operator's
    /// real passwords in plaintext. That is load-bearing for an exact revert, and it is also an
    /// ever-growing plaintext secret store if nothing ever removes it. Shipping
    /// <see cref="IPlanExecutor.ApplyAsync"/> without this sweep was explicitly ruled out.
    /// </para>
    /// <para>
    /// <strong>The retention decision is made from ACTION state, not from
    /// <see cref="ChangePlanRecord.Status"/>.</strong> A plan none of whose actions ever sent a write is a
    /// plan that changed nothing on the server, so its images can never be needed to undo anything and are
    /// discarded as soon as the plan is terminal (or expired and never applied) — no window at all. A plan
    /// with even one action that reached the server keeps its images for <paramref name="imageRetention"/>
    /// past the moment it took effect. Deriving that from the actions rather than from the plan's summary
    /// status is deliberate: it means a future bug in status assignment degrades to "images kept longer than
    /// strictly necessary" instead of "revert capability irrecoverably destroyed for a change that really did
    /// reach a live server".
    /// </para>
    /// <para>
    /// <strong>"Reached the server" is <see cref="ChangePlanActionRecord.WriteReachedServer"/>, NOT
    /// <see cref="ChangePlanActionStatus.Applied"/>,</strong> and the distinction is not academic. When a
    /// write lands and the read-back afterwards finds bytes nobody approved, that action is recorded
    /// <see cref="ChangePlanActionStatus.Failed"/> and every later one
    /// <see cref="ChangePlanActionStatus.Skipped"/> — so no action in the plan says
    /// <see cref="ChangePlanActionStatus.Applied"/> while a live server holds corrupted content and
    /// <see cref="ChangePlanActionRecord.PreImageContent"/> is the only way back. A predicate reading only
    /// <see cref="ChangePlanActionStatus.Applied"/> purges exactly that plan first.
    /// </para>
    /// <para>
    /// <strong>Non-terminal plans are never touched, at any age.</strong> A plan still
    /// <see cref="ChangePlanStatus.Previewed"/> and not yet expired is about to be applied, and one in
    /// <see cref="ChangePlanStatus.Applying"/> is mid-flight; discarding either one's post-image would break
    /// the apply that is happening right now.
    /// </para>
    /// <para>
    /// <strong>What is given up, stated plainly.</strong> Once an applied plan's images are purged, that plan
    /// can no longer be reverted from its recorded pre-image — there is nothing left to restore from. That is
    /// the point of <paramref name="imageRetention"/> being a knob: raise it for a longer revert horizon,
    /// lower it for less plaintext at rest.
    /// </para>
    /// </remarks>
    /// <param name="now">
    /// The sweep's reading of the clock, supplied by the caller from its injected <see cref="TimeProvider"/>
    /// rather than read here, so a store implementation contains no clock of its own.
    /// </param>
    /// <param name="imageRetention">
    /// How long a plan that actually changed something keeps its images after taking effect. Must not be
    /// negative.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the sweep did, for logging.</returns>
    Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
        DateTimeOffset now,
        TimeSpan imageRetention,
        CancellationToken ct = default);
}

/// <summary>What one <see cref="IChangePlanStore.PurgeImagesAsync"/> sweep did.</summary>
/// <param name="ExpiredPlansMarkedStale">
/// How many plans were still <see cref="ChangePlanStatus.Previewed"/> past their
/// <see cref="ChangePlanRecord.ExpiresAt"/> and were promoted to <see cref="ChangePlanStatus.Stale"/>.
/// </param>
/// <param name="PlansPurged">How many plans had image content discarded from at least one of their actions.</param>
/// <param name="ActionsPurged">How many individual action rows had image content discarded.</param>
public sealed record ChangePlanImagePurgeResult(
    int ExpiredPlansMarkedStale,
    int PlansPurged,
    int ActionsPurged)
{
    /// <summary>A sweep that found nothing to do.</summary>
    public static readonly ChangePlanImagePurgeResult Nothing = new(0, 0, 0);

    /// <summary>Whether this sweep changed anything at all.</summary>
    public bool Any => ExpiredPlansMarkedStale > 0 || PlansPurged > 0 || ActionsPurged > 0;
}

/// <summary>
/// Thrown by <see cref="IChangePlanStore.UpdateAsync"/> when the stored plan row has moved on since the
/// caller read it — the optimistic-concurrency failure that stops a plan being applied twice.
/// </summary>
/// <remarks>
/// A distinct domain exception rather than the storage provider's own concurrency type, so
/// <c>Servyx.Domain</c> (and therefore the configuration engine that consumes this interface) stays free of
/// any dependency on Entity Framework. An implementation translates; callers catch this.
/// </remarks>
public sealed class ChangePlanConcurrencyException : Exception
{
    /// <summary>Creates a <see cref="ChangePlanConcurrencyException"/> with a default message.</summary>
    public ChangePlanConcurrencyException()
        : base("The change plan was modified by someone else since it was read.")
    {
    }

    /// <summary>Creates a <see cref="ChangePlanConcurrencyException"/> with the given message.</summary>
    public ChangePlanConcurrencyException(string message) : base(message) { }

    /// <summary>Creates a <see cref="ChangePlanConcurrencyException"/> with the given message and inner exception.</summary>
    public ChangePlanConcurrencyException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="ChangePlanConcurrencyException"/> naming the plan that lost the race.</summary>
    /// <param name="message">The message.</param>
    /// <param name="planId">The plan whose update was rejected.</param>
    /// <param name="innerException">The storage provider's own concurrency failure, if any.</param>
    public ChangePlanConcurrencyException(string message, string planId, Exception? innerException = null)
        : base(message, innerException)
    {
        PlanId = planId;
    }

    /// <summary>The id of the plan whose update was rejected, if known.</summary>
    public string? PlanId { get; }
}
