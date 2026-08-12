using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;

namespace Servyx.Infrastructure.Persistence.Hosts;

/// <summary>
/// The durable <see cref="IHostRepository"/>, backed by the <c>Hosts</c> table via <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <c>EfServerRepository</c>'s pattern exactly: this type is registered singleton (its consumer is
/// also process-lifetime), and <see cref="ServyxDbContext"/> is registered scoped, so a singleton cannot hold
/// it directly. The factory is itself singleton-safe and creates a short-lived context per call, one unit of
/// work each.
/// </remarks>
public sealed class EfHostRepository : IHostRepository
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a repository that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfHostRepository(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Host>> ListAsync(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Hosts.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Host?> TryGetAsync(HostId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Hosts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Host?> TryGetByNameAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Hosts.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Host host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.Hosts.Add(host);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(HostId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.Hosts.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        context.Hosts.Remove(existing);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
