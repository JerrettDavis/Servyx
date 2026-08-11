using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Transport;

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
/// A lightweight summary of one persisted <see cref="ChangePlanRecord"/> and its actions, for a "recent
/// plans" listing rather than for acting on the plan.
/// </summary>
/// <remarks>
/// <strong>Deliberately excludes the blob columns.</strong> Neither this record nor
/// <see cref="ChangePlanActionSummary"/> carries <see cref="ChangePlanActionRecord.PreImageContent"/>,
/// <see cref="ChangePlanActionRecord.PostImageContent"/>, or <see cref="ChangePlanActionRecord.UnifiedDiff"/>.
/// Those hold whole configuration files, unmasked, and — when <see cref="ChangePlanActionRecord.ContainsSecrets"/>
/// is set — an operator's real passwords in plaintext. A history listing exists to answer "what happened", not
/// to render a diff, so pulling that content into memory for every row of a list view would be a needless
/// secret-exposure surface for no benefit. A caller that needs the actual content of one specific plan already
/// has <see cref="IChangePlanStore.TryGetAsync"/> for exactly that.
/// </remarks>
/// <param name="Id">The plan's identifier.</param>
/// <param name="ServerId">The server this plan targets.</param>
/// <param name="Status">The plan's current lifecycle state.</param>
/// <param name="CreatedAt">When this plan was previewed.</param>
/// <param name="CreatedBy">Who requested the preview.</param>
/// <param name="AppliedAt">When this plan was applied, if it ever was.</param>
/// <param name="AppliedBy">Who applied this plan, if it ever was.</param>
/// <param name="RevertedAt">When this plan was reverted, if it ever was.</param>
/// <param name="RevertedBy">Who reverted this plan, if it ever was.</param>
/// <param name="Actions">Its actions, summarized and already ordered by <see cref="ChangePlanActionRecord.Ordinal"/>.</param>
public sealed record ChangePlanSummary(
    ChangePlanId Id,
    ServerId ServerId,
    ChangePlanStatus Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? AppliedAt,
    string? AppliedBy,
    DateTimeOffset? RevertedAt,
    string? RevertedBy,
    IReadOnlyList<ChangePlanActionSummary> Actions);

/// <summary>
/// A lightweight summary of one persisted <see cref="ChangePlanActionRecord"/>, for a "recent plans" listing.
/// </summary>
/// <remarks>
/// See <see cref="ChangePlanSummary"/>'s own remarks for why <see cref="ChangePlanActionRecord.PreImageContent"/>,
/// <see cref="ChangePlanActionRecord.PostImageContent"/>, and <see cref="ChangePlanActionRecord.UnifiedDiff"/>
/// are deliberately absent here.
/// </remarks>
/// <param name="Id">This action row's own identifier.</param>
/// <param name="Ordinal">Execution order within the plan, zero-based.</param>
/// <param name="SurfaceId">The surface this action targets.</param>
/// <param name="ResolvedPath">The concrete path/location the bound surface resolved to at preview time.</param>
/// <param name="Kind">What kind of action this is.</param>
/// <param name="Status">This action's current lifecycle state.</param>
/// <param name="WriteReachedServer">Whether a write for this action reached the server.</param>
/// <param name="PostImageHash">Content hash of the post-image the operator approved, if any.</param>
/// <param name="ObservedPostImageHash">The post-image digest apply actually saw for this action, if any.</param>
/// <param name="PostWriteVerification">What reading this action's surface back after the write found.</param>
/// <param name="FailureReason">Why this action failed, if it did.</param>
/// <param name="AppliedAt">When this action was applied, if it ever was.</param>
/// <param name="RevertedAt">When this action finished reverting, if it ever was.</param>
public sealed record ChangePlanActionSummary(
    Guid Id,
    int Ordinal,
    string SurfaceId,
    string ResolvedPath,
    PlannedActionKind Kind,
    ChangePlanActionStatus Status,
    bool WriteReachedServer,
    string? PostImageHash,
    string? ObservedPostImageHash,
    PostWriteVerification PostWriteVerification,
    string? FailureReason,
    DateTimeOffset? AppliedAt,
    DateTimeOffset? RevertedAt);

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
/// <strong>Still deliberately narrow, and still not a repository.</strong> There is still no delete.
/// <see cref="UpdateAsync"/> and <see cref="PurgeImagesAsync"/> were added by the apply phase, which is when
/// the concurrency contract this interface previously declined to guess at was actually decided;
/// <see cref="ListRecentAsync"/> was added once a "recent plans" listing for a server page had a caller, and
/// deliberately returns <see cref="ChangePlanSummary"/> rather than <see cref="StoredChangePlan"/> — a list
/// view has no business pulling every plan's config-file blobs into memory just to render a status column.
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
    /// The most recent plans for <paramref name="serverId"/>, newest first, each with its actions summarized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <see cref="ChangePlanSummary"/>, not <see cref="StoredChangePlan"/> — see this interface's own
    /// remarks for why a listing must not carry <see cref="ChangePlanActionRecord.PreImageContent"/>,
    /// <see cref="ChangePlanActionRecord.PostImageContent"/>, or <see cref="ChangePlanActionRecord.UnifiedDiff"/>.
    /// A caller that needs the full content of one specific plan already has <see cref="TryGetAsync"/>.
    /// </para>
    /// <para>
    /// Ordered newest first by <see cref="ChangePlanRecord.CreatedAt"/> (through its sortable twin
    /// <see cref="ChangePlanRecord.CreatedAtTicks"/>), with <see cref="ChangePlanRecord.Id"/> descending as a
    /// tiebreak for plans previewed in the same instant — a history view must be deterministically ordered,
    /// not merely "usually" ordered. Both the ordering and <paramref name="limit"/> are the store's job to
    /// apply at the database, not an implementation's job to apply after loading the table.
    /// </para>
    /// </remarks>
    /// <param name="serverId">The server whose plans to list.</param>
    /// <param name="limit">The maximum number of plans to return. Must be between 1 and 100 inclusive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than 1 or greater than 100.</exception>
    Task<IReadOnlyList<ChangePlanSummary>> ListRecentAsync(
        ServerId serverId,
        int limit,
        CancellationToken ct = default);

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
    /// <para>
    /// <strong>This member must not write <see cref="ChangePlanActionRecord.PreImageContent"/> or
    /// <see cref="ChangePlanActionRecord.PostImageContent"/>.</strong> Those two columns are written once, by
    /// <see cref="SaveAsync"/>, and cleared only by <see cref="PurgeImagesAsync"/>. A caller holds its action
    /// snapshot across seconds of live server I/O, so an implementation that wrote every column would let a
    /// stale snapshot restore plaintext content the retention sweep had already discarded — see
    /// <see cref="PurgeImagesAsync"/> for why that content is what it is.
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
    /// <see cref="ChangePlanStatus.Previewed"/> and not yet expired is about to be applied, one in
    /// <see cref="ChangePlanStatus.Applying"/> is mid-flight, and one in
    /// <see cref="ChangePlanStatus.Reverting"/> is mid-restore; discarding a post-image would break the apply
    /// happening right now, and discarding a pre-image would break the revert happening right now.
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
