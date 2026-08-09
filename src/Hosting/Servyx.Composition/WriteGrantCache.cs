using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence;

namespace Servyx.Composition;

/// <summary>
/// The in-memory view of every per-server write grant the operator has recorded in the database, keyed on
/// the container's durable identity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a cache exists at all.</strong> <c>IWriteModeResolver.Resolve</c> is synchronous and is now
/// consulted on <em>every</em> guarded command rather than once per connect (see
/// <c>WriteGuardedExecutionTarget</c>), so it cannot afford — and must not perform — a database round-trip
/// per <c>docker exec</c>. This type turns that into a <see cref="Dictionary{TKey,TValue}"/> lookup: the
/// database is read once at startup (<see cref="Prime"/>) and then only after a grant change has dropped the
/// snapshot.
/// </para>
/// <para>
/// <strong>That reload is lazy, and lands on a command.</strong> <see cref="Invalidate"/> only drops the
/// snapshot; the replacement is loaded inside <see cref="Snapshot"/> on the next call that needs one. So the
/// first guarded command issued after a grant flip pays one synchronous SQLite read, holding
/// <c>_loadGate</c> while it does — concurrent guarded commands queue behind it for the duration. That is a
/// deliberate trade and not a claim of "never on the command path": an eager reload on the write path would
/// have to be sequenced against <see cref="Invalidate"/>'s version counter to avoid re-opening the publish
/// race below, and the read is a local, indexed, few-row query on a path only mutating commands reach.
/// </para>
/// <para>
/// <strong>Why a singleton over <see cref="IDbContextFactory{TContext}"/>.</strong> <c>ServyxDbContext</c> is
/// registered Scoped and this type is a process-lifetime singleton consumed by other singletons (the
/// resolver, the transports built over it), so it cannot hold a context. It takes the already-singleton
/// factory and opens a short-lived context per load, exactly as <c>EfServerDefinitionBindingStore</c> and
/// <c>EfServerRepository</c> do. The load is deliberately <em>synchronous</em>: the only caller that matters
/// is a synchronous resolver, and sync-over-async on a Blazor Server circuit is a deadlock generator. A
/// local SQLite read is the one place a synchronous EF query is the right answer.
/// </para>
/// <para>
/// <strong>The master switch short-circuits before the database.</strong> With
/// <c>Servyx:Provisioning:Enabled</c> closed there is no grant anywhere in this process by construction, so
/// <see cref="ModeFor"/> returns <see cref="ServerWriteMode.ReadOnly"/> without opening a context at all.
/// That is not an optimisation — it is the property that keeps a read-only host's behaviour independent of
/// whether its database is even reachable.
/// </para>
/// <para>
/// <strong>The key is <c>Server.ContainerId</c> and nothing else.</strong> Not the container name (an
/// operator can rename a container outside Servyx at any time, and names are not unique across hosts), and
/// not the (<c>HostId</c>, <c>ContainerId</c>) pair — <c>Server.HostId</c> is null by design for every
/// adopted server today, so a pair match would compare null to null and contribute nothing while appearing
/// to check more. See <c>docs/plans/ui-management-surface.md</c> §2 "Grant key semantics".
/// </para>
/// <para>
/// <strong>Every failure direction is read-only.</strong> A missing row, a blank container id, a closed
/// master switch, an unreachable database — all resolve to <see cref="ServerWriteMode.ReadOnly"/>. A load
/// failure is logged and <em>not</em> cached, so the next call retries rather than pinning the process to an
/// empty grant set until restart; the cost of that retry is bounded by the fact that only mutating commands
/// ever reach this type.
/// </para>
/// </remarks>
public sealed class WriteGrantCache
{
    /// <summary>
    /// A cache that holds nothing and never opens a context — what a host composes when the master switch is
    /// closed, and the safe fallback for any consumer that could not resolve a real one.
    /// </summary>
    public static readonly WriteGrantCache Closed = new(ProvisioningGate.Closed, contexts: null, logger: null);

    private static readonly IReadOnlyDictionary<string, ServerWriteMode> EmptyGrants =
        new Dictionary<string, ServerWriteMode>(StringComparer.OrdinalIgnoreCase);

    private readonly ProvisioningGate _gate;
    private readonly IDbContextFactory<ServyxDbContext>? _contexts;
    private readonly ILogger? _logger;
    private readonly Lock _loadGate = new();

    private volatile IReadOnlyDictionary<string, ServerWriteMode>? _grants;
    private volatile bool _everLoaded;

    /// <summary>
    /// Bumped by every <see cref="Invalidate"/>. A load stamps this before it starts and re-reads it before
    /// it publishes; a mismatch means a grant changed while the load was in flight, so the loaded snapshot is
    /// already known to be stale and must not become the cached one. See <see cref="Snapshot"/>.
    /// </summary>
    private long _version;

    /// <summary>Creates the cache.</summary>
    /// <param name="gate">
    /// The process-level master switch. Closed means no grant exists and the database is never touched.
    /// </param>
    /// <param name="contexts">
    /// The factory short-lived read contexts are opened from, or <see langword="null"/> when this process
    /// composed no persistence at all — which, like a closed gate, means every server is read-only.
    /// </param>
    /// <param name="logger">Where a failed load is reported. Optional.</param>
    public WriteGrantCache(
        ProvisioningGate gate,
        IDbContextFactory<ServyxDbContext>? contexts,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gate);

        _gate = gate;
        _contexts = contexts;
        _logger = logger;
    }

    /// <summary>Whether this cache could ever hold a grant — i.e. the master switch is open and a store exists.</summary>
    public bool CanGrant => _gate.Enabled && _contexts is not null;

    /// <summary>
    /// The write posture recorded for <paramref name="containerId"/>, or
    /// <see cref="ServerWriteMode.ReadOnly"/> when the master switch is closed, the id is blank, or no
    /// <c>Server</c> row carries it. <strong>A missing row is read-only</strong> — the single most important
    /// line in this type.
    /// </summary>
    /// <param name="containerId">The discovery-native container id the grant is keyed on.</param>
    public ServerWriteMode ModeFor(string? containerId)
    {
        if (!_gate.Enabled || _contexts is null || string.IsNullOrWhiteSpace(containerId))
        {
            return ServerWriteMode.ReadOnly;
        }

        return Snapshot().TryGetValue(containerId, out var mode) ? mode : ServerWriteMode.ReadOnly;
    }

    /// <summary>
    /// Every container id currently carrying a non-<see cref="ServerWriteMode.ReadOnly"/> grant. Empty when
    /// the master switch is closed. Read by the UI's write-state label, never by the write guard.
    /// </summary>
    public IReadOnlyCollection<string> GrantedContainerIds
    {
        get
        {
            if (!_gate.Enabled || _contexts is null)
            {
                return [];
            }

            return [.. Snapshot().Where(pair => pair.Value != ServerWriteMode.ReadOnly).Select(pair => pair.Key)];
        }
    }

    /// <summary>
    /// Drops the cached snapshot so the next read re-reads the database. Called by the grant-write path
    /// immediately after the row is persisted and before it returns, which is what makes a UI flip visible to
    /// the next command issued anywhere in this process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The version bump, not the null, is what makes this safe.</strong> Nulling an already-null
    /// field is a no-op, and there is one interleaving in which that no-op silently loses a revocation: a
    /// concurrent load that has already read the pre-write rows but has not yet published them. Without a
    /// version, that load republishes the pre-revoke grant set <em>after</em> this call returned success,
    /// with no further invalidation scheduled — write access stays live, the UI says ReadOnly, and only an
    /// unrelated grant change or a restart clears it. The counter closes that window: the racing load sees
    /// the stamp move and declines to publish.
    /// </para>
    /// <para>
    /// <strong>This deliberately does not take <c>_loadGate</c>.</strong> Doing so would also close the race,
    /// but it would park every operator's grant write behind whatever database read happens to be in flight.
    /// The counter is lock-free on the write side and costs the load two extra interlocked reads.
    /// </para>
    /// <para>
    /// <strong>The increment must come BEFORE the null, and the order is not stylistic.</strong>
    /// <see cref="Snapshot"/> re-checks the version after it publishes precisely so that a load which wrote
    /// its snapshot over a null this method had just installed retracts it. That argument only holds because
    /// a caller who observed the null here necessarily observed the increment first. Swapping these two
    /// lines would leave a load able to publish a stale snapshot and then see a matching version, which is
    /// the whole defect back again. Do not reorder them.
    /// </para>
    /// </remarks>
    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _grants = null;
    }

    /// <summary>
    /// A test-only seam invoked inside the load lock immediately after a load returns and immediately before
    /// the version re-check decides whether to publish it — i.e. at exactly the instant the publish race
    /// opens. It exists because that window cannot be hit reliably by a background thread, and a race test
    /// that only <em>usually</em> hits it would pass against the broken implementation. Always
    /// <see langword="null"/> in every production composition; <c>internal</c>, so no host can set it.
    /// </summary>
    internal Action? LoadInterleaveHookForTests { get; set; }

    /// <summary>
    /// The second test-only seam, invoked inside the load lock after the pre-publish version check has
    /// already passed and immediately before the assignment it guards. It parks a load in the narrow window
    /// between those two statements — the one the post-publish re-check exists for, and the only window in
    /// which a stale snapshot can be written over a null <c>Invalidate()</c> has just installed. Same
    /// reasoning as <see cref="LoadInterleaveHookForTests"/>: a couple of instructions cannot be hit
    /// reliably by timing, and a race test that hits its window only sometimes is a green check attesting to
    /// a property the code may not have. Always <see langword="null"/> in production; <c>internal</c>.
    /// </summary>
    internal Action? PublishInterleaveHookForTests { get; set; }

    /// <summary>
    /// Loads the snapshot eagerly, swallowing (and logging) any failure. Called once at startup, after the
    /// schema has been migrated, so the first guarded command does not pay for the initial read — and so a
    /// database that is unreachable at startup is reported then rather than at the operator's first click.
    /// </summary>
    public void Prime()
    {
        if (!_gate.Enabled || _contexts is null)
        {
            return;
        }

        _ = Snapshot();
    }

    private IReadOnlyDictionary<string, ServerWriteMode> Snapshot()
    {
        var current = _grants;
        if (current is not null)
        {
            return current;
        }

        lock (_loadGate)
        {
            current = _grants;
            if (current is not null)
            {
                return current;
            }

            // Stamped BEFORE the read, re-read after it. Anything Invalidate() does in between makes this
            // load's result stale by construction, however fresh it looked when the rows came back.
            var stamp = Interlocked.Read(ref _version);

            var loaded = TryLoad();

            LoadInterleaveHookForTests?.Invoke();

            if (loaded is null)
            {
                // Deliberately NOT cached: pinning the process to an empty snapshot because the database was
                // briefly unavailable would silently disable every grant until restart. Failing closed for
                // this one call and retrying on the next is the honest trade.
                return EmptyGrants;
            }

            if (Interlocked.Read(ref _version) == stamp)
            {
                PublishInterleaveHookForTests?.Invoke();

                _grants = loaded;

                // The check above and the assignment below it are two statements, not one atomic operation,
                // so an Invalidate() can land entirely BETWEEN them: it increments the version and nulls a
                // field that is still null, and this line then writes the stale snapshot over the top of
                // that null. The window is a couple of instructions rather than a whole database read, but
                // it is the identical defect and the identical consequence — a revoke the operator was told
                // had landed, silently serving write access afterwards.
                //
                // Re-checking AFTER the publish converges in every ordering, and that is a property of
                // Invalidate() incrementing BEFORE it nulls: if its null ran before this publish, its
                // increment did too, so the check below sees the mismatch and retracts. If its null ran
                // after this publish, the field is already null and there is nothing to retract. There is
                // no interleaving in which a stale snapshot survives both checks.
                if (Interlocked.Read(ref _version) != stamp)
                {
                    _grants = null;
                }
            }

            // Returned to THIS caller either way — it asked before the change landed, and it is about to be
            // re-asked on the next command anyway. What must not happen is this snapshot becoming the cached
            // one: _grants ends up null, so the very next read re-loads and sees the change. A revocation
            // that is durable but invisible until an unrelated write is not a revocation.
            return loaded;
        }
    }

    private IReadOnlyDictionary<string, ServerWriteMode>? TryLoad()
    {
        try
        {
            using var context = _contexts!.CreateDbContext();

            var rows = context.Servers
                .AsNoTracking()
                .Select(server => new { server.ContainerId, server.WriteMode })
                .ToList();

            var grants = new Dictionary<string, ServerWriteMode>(rows.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (!string.IsNullOrWhiteSpace(row.ContainerId))
                {
                    grants[row.ContainerId] = row.WriteMode;
                }
            }

            _everLoaded = true;
            return grants;
        }
        catch (Exception ex)
        {
            // The FIRST failure is a Warning, not an Error, because the ordinary shape of it is benign and
            // expected: the web host deliberately logs its startup safety warnings BEFORE running migrations
            // (so a migration failure can never swallow them), and on a fresh install the Servers table does
            // not exist yet at that moment. A failure AFTER a read has ever succeeded is a different animal —
            // a store that was working and is not — and is reported at Error.
            _logger?.Log(
                _everLoaded ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Could not read per-server write grants from the database; every server is treated as "
                + "{ReadOnly} until the next successful read. No grant is being widened by this failure. On a "
                + "fresh install this is expected once during startup, before the schema is migrated.",
                nameof(ServerWriteMode.ReadOnly));
            return null;
        }
    }
}
