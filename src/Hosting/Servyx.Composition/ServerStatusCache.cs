using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Servyx.Infrastructure.Persistence;

namespace Servyx.Composition;

/// <summary>
/// The in-memory, thread-safe view of every adopted server's last-known status and resource sample, keyed on
/// the container's durable identity. This is what <c>LiveDashboardDataService</c> reads from instead of
/// issuing a live discovery/metrics call on every page load — see <c>ServerStatusRefreshService</c>, the
/// sole writer, for how it stays current.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read/write split, mirroring <see cref="WriteGrantCache"/>.</strong> A Blazor Server page's
/// <c>OnInitializedAsync</c> must never block on a live SSH/Docker/RCON round-trip; this cache turns that
/// into a lock-free dictionary lookup. Unlike <see cref="WriteGrantCache"/>, staleness here is not a
/// correctness hazard (a write grant flip must be visible immediately; a stale player count or CPU sample is
/// merely stale, and the UI says so — see <c>DashboardSummary.IsStale</c>/<c>ServerListResult.IsStale</c>),
/// so there is no invalidate-and-reload-on-next-read dance: the cache only ever changes when the background
/// worker calls <see cref="ReplaceAll"/>, on its own schedule.
/// </para>
/// <para>
/// <strong>Priming.</strong> <see cref="Prime"/> loads whatever was last durably written
/// (<c>ServerStatusSnapshot</c> rows) so the very first page load after a process restart shows the last
/// real read rather than an empty cache — the same reason <see cref="WriteGrantCache.Prime"/> exists. A
/// priming failure is logged and swallowed: the cache simply starts empty and is populated by the first
/// background refresh tick instead.
/// </para>
/// </remarks>
public sealed class ServerStatusCache
{
    private readonly IDbContextFactory<ServyxDbContext> _contexts;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ServerStatusEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the cache.</summary>
    /// <param name="contexts">The factory short-lived read/write contexts are opened from for <see cref="Prime"/> and by the refresh worker.</param>
    /// <param name="logger">Where a failed priming load is reported. Optional.</param>
    public ServerStatusCache(IDbContextFactory<ServyxDbContext> contexts, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        _contexts = contexts;
        _logger = logger;
    }

    /// <summary>The cached entry for <paramref name="containerId"/>, or <see langword="null"/> if none has ever been recorded.</summary>
    public ServerStatusEntry? Get(string containerId) =>
        !string.IsNullOrWhiteSpace(containerId) && _entries.TryGetValue(containerId, out var entry) ? entry : null;

    /// <summary>Records (or overwrites) the entry for a single container id.</summary>
    public void Set(string containerId, ServerStatusEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(entry);

        _entries[containerId] = entry;
    }

    /// <summary>Every cached entry, in no particular order.</summary>
    public IReadOnlyList<ServerStatusEntry> GetAll() => _entries.Values.ToList();

    /// <summary>
    /// Atomically replaces the cache's contents with exactly what one refresh tick produced: every id in
    /// <paramref name="fresh"/> is set, and every id that was cached before this call but is absent from
    /// <paramref name="fresh"/> is removed — so a forgotten or no-longer-discovered server does not linger
    /// in the dashboard forever. Called once per tick by <see cref="ServerStatusRefreshService"/>.
    /// </summary>
    public void ReplaceAll(IReadOnlyDictionary<string, ServerStatusEntry> fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        foreach (var staleKey in _entries.Keys.Where(key => !fresh.ContainsKey(key)).ToList())
        {
            _entries.TryRemove(staleKey, out _);
        }

        foreach (var (id, entry) in fresh)
        {
            _entries[id] = entry;
        }
    }

    /// <summary>
    /// Loads every durably-recorded snapshot into the cache. Called once at startup, after the schema has
    /// been migrated (see <c>ServyxCoreComposition.MigrateDatabaseAsync</c>), so a page load immediately
    /// after a restart reads the last real data instead of an empty cache while the first background tick is
    /// still in flight.
    /// </summary>
    public void Prime()
    {
        try
        {
            using var context = _contexts.CreateDbContext();

            var rows = context.ServerStatusSnapshots.AsNoTracking().ToList();
            foreach (var row in rows)
            {
                _entries[row.ContainerId] = ServerStatusMapping.ToEntry(row);
            }
        }
        catch (Exception ex)
        {
            // Same posture as WriteGrantCache.TryLoad: a database that is briefly unreadable at startup must
            // not stop the host, and the cache degrades to empty rather than to stale-forever — the first
            // successful background refresh tick populates it regardless of whether priming succeeded.
            _logger?.LogWarning(
                ex,
                "Could not prime the server status cache from the database; it starts empty and will be "
                + "populated by the next background refresh.");
        }
    }
}
