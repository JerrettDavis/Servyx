using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Users;

namespace Servyx.Infrastructure.Persistence.Users;

/// <summary>
/// The durable <see cref="IUserRepository"/>, backed by the <c>Users</c> table via <see cref="ServyxDbContext"/>.
/// </summary>
/// <remarks>
/// Takes an <see cref="IDbContextFactory{TContext}"/> rather than a <see cref="ServyxDbContext"/> directly,
/// following <c>EfHostRepository</c>'s pattern exactly: this type is registered singleton (its consumer,
/// <c>Servyx.Application.Users.UserService</c>, is registered the same way — see the composition root), and
/// <see cref="ServyxDbContext"/> is registered scoped, so a singleton cannot hold it directly. The factory is
/// itself singleton-safe and creates a short-lived context per call, one unit of work each.
/// </remarks>
public sealed class EfUserRepository : IUserRepository
{
    private readonly IDbContextFactory<ServyxDbContext> _contextFactory;

    /// <summary>Creates a repository that opens a short-lived context per call via <paramref name="contextFactory"/>.</summary>
    public EfUserRepository(IDbContextFactory<ServyxDbContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Users.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> TryGetAsync(UserId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> TryGetByUsernameAsync(string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await context.Users.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Username == username, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(User user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        context.Users.Add(user);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> SetRoleAsync(UserId id, UserRole role, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Tracked (not AsNoTracking) on purpose: this writes an existing row, so EF has to observe the
        // mutation. Matches EfServerRepository.SetWriteModeAsync's discipline.
        var existing = await context.Users.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        existing.Role = role;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <inheritdoc />
    public async Task<User?> SetActiveAsync(UserId id, bool isActive, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.Users.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        existing.IsActive = isActive;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }

    /// <inheritdoc />
    public async Task<User?> SetPasswordHashAsync(UserId id, string passwordHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using var context = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await context.Users.SingleOrDefaultAsync(row => row.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        existing.PasswordHash = passwordHash;

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return existing;
    }
}
