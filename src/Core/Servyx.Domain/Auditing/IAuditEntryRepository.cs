using Servyx.Domain.Entities;

namespace Servyx.Domain.Auditing;

/// <summary>
/// Durable storage for <see cref="AuditEntry"/> rows — the append-only accountability trail behind
/// <c>Servyx.Application.Auditing.IAuditLogger</c>.
/// </summary>
/// <remarks>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> Exactly the same reasoning as
/// <see cref="Servyx.Domain.Users.IUserRepository"/>: every infrastructure project references
/// <c>Servyx.Domain</c> and nothing else, so an abstraction infrastructure must implement has to be declared
/// here. <c>Servyx.Infrastructure.Persistence</c> supplies the real, EF-backed implementation
/// (<c>EfAuditEntryRepository</c>, over the <c>AuditEntries</c> table).
/// <para>
/// <strong>Append-only.</strong> There is deliberately no update or delete member — see
/// <see cref="AuditEntry"/>'s own remarks on why a trail that could be edited after the fact would not be one.
/// </para>
/// </remarks>
public interface IAuditEntryRepository
{
    /// <summary>Persists a newly-recorded <see cref="AuditEntry"/> row.</summary>
    Task AddAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// The <paramref name="limit"/> most recent entries, newest first. Kept alongside <see cref="SearchAsync"/>
    /// as the simplest possible read (no filter, no paging) — a caller that only ever wants "the last N" does
    /// not need to reason about page numbers or an unconstrained <see cref="AuditEntryFilter"/>.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// One page of entries matching <paramref name="filter"/>, newest first — the read path behind the
    /// <c>/audit</c> reader UI. <paramref name="pageNumber"/> is 1-based; <paramref name="pageSize"/> must be
    /// at least 1. This trail is append-only and grows without bound, so this is deliberately a server-side
    /// filtered, paged query rather than a "fetch everything, filter in the UI" surface — see
    /// <c>EfAuditEntryRepository.SearchAsync</c>'s own remarks for exactly how much of that filtering an EF
    /// Core SQLite provider can push down to SQL versus what it still has to do after materializing.
    /// </summary>
    Task<AuditEntryPage> SearchAsync(
        AuditEntryFilter filter, int pageNumber, int pageSize, CancellationToken ct = default);
}

/// <summary>
/// Filter criteria for <see cref="IAuditEntryRepository.SearchAsync"/>. Every criterion left
/// <see langword="null"/> (or, for <see cref="Actor"/>/<see cref="ActionPrefix"/>, blank) is unconstrained.
/// </summary>
/// <remarks>
/// Deliberately narrow — exact actor match and action-prefix match (meaningful because every recorded
/// <see cref="AuditEntry.Action"/> follows the dotted "noun.verb" convention <see cref="AuditActions"/>
/// documents, e.g. matching <c>"user."</c> against <c>"user.created"</c>/<c>"user.role_changed"</c>/...) plus
/// an inclusive UTC timestamp range cover what the <c>/audit</c> reader UI needs, without growing into a
/// general-purpose query object nothing else in this codebase needs yet.
/// </remarks>
/// <param name="Actor">Matches only entries whose <see cref="AuditEntry.Actor"/> equals this value exactly, or unconstrained when <see langword="null"/>/blank.</param>
/// <param name="ActionPrefix">Matches only entries whose <see cref="AuditEntry.Action"/> starts with this value, or unconstrained when <see langword="null"/>/blank.</param>
/// <param name="FromUtc">Matches only entries whose <see cref="AuditEntry.TimestampUtc"/> is at or after this instant, or unconstrained when <see langword="null"/>.</param>
/// <param name="ToUtc">Matches only entries whose <see cref="AuditEntry.TimestampUtc"/> is at or before this instant, or unconstrained when <see langword="null"/>.</param>
public sealed record AuditEntryFilter(
    string? Actor = null,
    string? ActionPrefix = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null)
{
    /// <summary>No constraint at all — every entry matches.</summary>
    public static readonly AuditEntryFilter None = new();
}

/// <summary>
/// One page of <see cref="AuditEntry"/> rows matching an <see cref="AuditEntryFilter"/>, newest first, plus
/// <see cref="TotalCount"/> — the total number of rows matching the filter across every page, not just
/// <see cref="Entries"/>.Count — so a reader UI can render "page X of Y" or disable "next" without a second
/// round trip.
/// </summary>
public sealed record AuditEntryPage(IReadOnlyList<AuditEntry> Entries, int TotalCount);
