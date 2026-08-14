using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;

namespace Servyx.Composition;

/// <summary>
/// The <see cref="IServerRepository"/> every host actually resolves: the durable one, wrapped so that
/// <em>any</em> write to a <c>Server</c> row drops <see cref="WriteGrantCache"/>'s snapshot before the call
/// returns.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a decorator rather than a call at the one place that needed it.</strong> Invalidation used to
/// live in exactly one caller — <c>WriteGrantService.SetWriteModeAsync</c> — which made it a convention, and
/// a convention that had already been broken: <c>ServerAdoptionService.ForgetAsync</c> calls
/// <see cref="IServerRepository.RemoveAsync"/> directly and told the cache nothing. An operator who forgot a
/// server holding <see cref="ServerWriteMode.Enabled"/> deleted the row, watched it disappear from the UI,
/// and left the cache still mapping that container id to <see cref="ServerWriteMode.Enabled"/> — for every
/// already-open session and every new one, until an unrelated grant change or a restart. Re-adopting the same
/// container then weaponised it: adoption always writes <see cref="ServerWriteMode.ReadOnly"/>, so a
/// freshly-adopted, never-granted server came back writable.
/// </para>
/// <para>
/// <strong>The fix is structural, in the same spirit as <c>WriteGuardedTransport</c>.</strong> Nothing in
/// this process can reach the <c>Server</c> table except through <see cref="IServerRepository"/>, and the
/// only registration of that interface is this wrapper, so there is no route by which a second caller — one
/// nobody has written yet — can mutate a row and forget to invalidate. Deliberately NOT solved by routing
/// removal through <c>IWriteGrantService</c>: that would fix the one caller that exists today and leave the
/// next one free to make the same mistake, and it would push a <c>Servyx.Composition</c> type into
/// <c>Servyx.Application</c>, which does not (and must not) reference it.
/// </para>
/// <para>
/// <strong>Every mutating member invalidates, including <see cref="AddAsync"/>.</strong> Adoption alone is
/// benign in isolation — a new row is <see cref="ServerWriteMode.ReadOnly"/>, and a cache with no entry for
/// that container id also answers read-only, so the two agree. It invalidates anyway, because "which
/// mutations happen to be safe to skip" is precisely the reasoning that produced the removal gap. Reads pass
/// straight through and touch nothing.
/// </para>
/// <para>
/// <strong>Invalidation happens after the store call returns, never before.</strong> Same ordering
/// <c>WriteGrantService</c> documents: invalidating first would leave a window in which a reload re-read the
/// pre-write row and cached it. <see cref="WriteGrantCache.Invalidate"/> is itself race-safe against a load
/// already in flight — see its own remarks.
/// </para>
/// </remarks>
public sealed class GrantInvalidatingServerRepository : IServerRepository
{
    private readonly IServerRepository _inner;
    private readonly WriteGrantCache _grants;

    /// <summary>Creates the wrapper.</summary>
    /// <param name="inner">The durable repository every call delegates to.</param>
    /// <param name="grants">The in-memory grant view dropped after every mutation.</param>
    public GrantInvalidatingServerRepository(IServerRepository inner, WriteGrantCache grants)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(grants);

        _inner = inner;
        _grants = grants;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);

    /// <inheritdoc />
    public Task<Server?> TryGetAsync(ServerId id, CancellationToken ct = default) => _inner.TryGetAsync(id, ct);

    /// <inheritdoc />
    public async Task AddAsync(Server server, CancellationToken ct = default)
    {
        await _inner.AddAsync(server, ct).ConfigureAwait(false);
        _grants.Invalidate();
    }

    /// <inheritdoc />
    public async Task<Server?> SetWriteModeAsync(
        ServerId id,
        ServerWriteMode mode,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        var updated = await _inner.SetWriteModeAsync(id, mode, changedBy, changedAt, ct).ConfigureAwait(false);
        _grants.Invalidate();
        return updated;
    }

    /// <inheritdoc />
    public async Task<Server?> SetMirrorDerivedSurfacesAsync(
        ServerId id,
        bool mirrorDerivedSurfaces,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        // Invalidates too, even though this flag is not itself a write grant and WriteGrantCache does not
        // read it. "Which mutations happen to be safe to skip" is the exact reasoning that produced the
        // removal gap this decorator exists to close — every mutating member invalidates, without exception.
        var updated = await _inner
            .SetMirrorDerivedSurfacesAsync(id, mirrorDerivedSurfaces, changedBy, changedAt, ct)
            .ConfigureAwait(false);

        _grants.Invalidate();
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(ServerId id, CancellationToken ct = default)
    {
        var removed = await _inner.RemoveAsync(id, ct).ConfigureAwait(false);
        _grants.Invalidate();
        return removed;
    }
}
