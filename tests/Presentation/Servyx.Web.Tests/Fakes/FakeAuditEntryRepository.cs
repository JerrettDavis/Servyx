using Servyx.Domain.Auditing;
using Servyx.Domain.Entities;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IAuditEntryRepository"/> for bUnit tests that render <c>AuditPage</c> directly,
/// rather than driving the real, persistence-backed <c>EfAuditEntryRepository</c>. Mirrors
/// <see cref="FakeUserRepository"/>'s own "state-carrying" discipline, and applies the same
/// filter/sort/page semantics <c>EfAuditEntryRepository.SearchAsync</c> documents (actor is an exact match,
/// action is a prefix match, the timestamp range is inclusive, newest first) so a test against this fake
/// exercises the same contract the real store honours.
/// </summary>
public sealed class FakeAuditEntryRepository : IAuditEntryRepository
{
    public List<AuditEntry> Rows { get; } = [];

    public Task AddAsync(AuditEntry entry, CancellationToken ct = default)
    {
        Rows.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(
            Rows.OrderByDescending(row => row.TimestampUtc).Take(limit).ToList());

    public Task<AuditEntryPage> SearchAsync(
        AuditEntryFilter filter, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        IEnumerable<AuditEntry> matches = Rows;

        if (!string.IsNullOrWhiteSpace(filter.Actor))
        {
            matches = matches.Where(row => row.Actor == filter.Actor);
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionPrefix))
        {
            matches = matches.Where(row => row.Action.StartsWith(filter.ActionPrefix, StringComparison.Ordinal));
        }

        if (filter.FromUtc is { } from)
        {
            matches = matches.Where(row => row.TimestampUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            matches = matches.Where(row => row.TimestampUtc <= to);
        }

        var ordered = matches.OrderByDescending(row => row.TimestampUtc).ToList();
        var page = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();

        return Task.FromResult(new AuditEntryPage(page, ordered.Count));
    }
}
