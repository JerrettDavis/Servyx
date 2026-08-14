using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Servers;

namespace Servyx.Infrastructure.Persistence.Servers;

/// <summary>
/// The durable <see cref="IServerRepository"/>, backed by the <c>Servers</c> table via
/// <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <c>EfServerDefinitionBindingStore</c>'s pattern exactly: this type is registered singleton (its
/// consumer, <c>ServerAdoptionService</c>, is registered the same way — see the composition root), and
/// <see cref="ServyxDbContext"/> is registered scoped, so a singleton cannot hold it directly. The factory
/// is itself singleton-safe and creates a short-lived context per call, one unit of work each.
/// </remarks>
public sealed class EfServerRepository : IServerRepository
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a repository that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfServerRepository(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Servers.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Server?> TryGetAsync(ServerId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Servers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Server server, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.Servers.Add(server);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Server?> SetWriteModeAsync(
        ServerId id,
        ServerWriteMode mode,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Tracked (not AsNoTracking) on purpose: this is the one method here that writes an existing row, so
        // it needs EF to observe the mutation. The posture and its attribution move in one SaveChanges, so a
        // row can never end up carrying a grant with no record of who made it.
        var existing = await context.Servers.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        existing.WriteMode = mode;
        existing.WriteModeChangedBy = changedBy;
        existing.WriteModeChangedAt = changedAt;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <inheritdoc />
    public async Task<Server?> SetMirrorDerivedSurfacesAsync(
        ServerId id,
        bool mirrorDerivedSurfaces,
        string changedBy,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changedBy);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Tracked, and the flag written together with its attribution in one SaveChanges, for exactly the
        // reasons SetWriteModeAsync above documents — this is the same kind of operator-recorded posture and
        // must not be able to land without a record of who recorded it.
        var existing = await context.Servers.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        existing.MirrorDerivedSurfaces = mirrorDerivedSurfaces;
        existing.MirrorDerivedSurfacesChangedBy = changedBy;
        existing.MirrorDerivedSurfacesChangedAt = changedAt;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(ServerId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.Servers.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        context.Servers.Remove(existing);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
