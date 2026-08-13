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
    /// The <paramref name="limit"/> most recent entries, newest first. The read path the future <c>/audit</c>
    /// reader UI will use — not consumed by anything in this increment, but present now so the store's
    /// contract is exercised end-to-end by this increment's own tests rather than left as an untested
    /// write-only surface.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct = default);
}
