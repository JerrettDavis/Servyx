using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Users;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IUserRepository"/> for tests that need a real <c>Servyx.Application.Users.UserService</c>
/// behind a fake store, rather than a hand-rolled <see cref="Servyx.Application.Users.IUserService"/> double.
/// Mirrors <c>Servyx.Application.Tests.Users.UserServiceTests.FakeUserRepository</c>'s shape.
/// </summary>
public sealed class FakeUserRepository : IUserRepository
{
    public List<User> Rows { get; } = [];

    public Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<User>>(Rows.ToList());

    public Task<User?> TryGetAsync(UserId id, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(row => row.Id == id));

    public Task<User?> TryGetByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(row => string.Equals(row.Username, username, StringComparison.Ordinal)));

    public Task AddAsync(User user, CancellationToken ct = default)
    {
        Rows.Add(user);
        return Task.CompletedTask;
    }

    public Task<User?> SetRoleAsync(UserId id, UserRole role, CancellationToken ct = default)
    {
        var existing = Rows.FirstOrDefault(row => row.Id == id);
        if (existing is null)
        {
            return Task.FromResult<User?>(null);
        }

        existing.Role = role;
        return Task.FromResult<User?>(existing);
    }

    public Task<User?> SetActiveAsync(UserId id, bool isActive, CancellationToken ct = default)
    {
        var existing = Rows.FirstOrDefault(row => row.Id == id);
        if (existing is null)
        {
            return Task.FromResult<User?>(null);
        }

        existing.IsActive = isActive;
        return Task.FromResult<User?>(existing);
    }

    public Task<User?> SetPasswordHashAsync(UserId id, string passwordHash, CancellationToken ct = default)
    {
        var existing = Rows.FirstOrDefault(row => row.Id == id);
        if (existing is null)
        {
            return Task.FromResult<User?>(null);
        }

        existing.PasswordHash = passwordHash;
        return Task.FromResult<User?>(existing);
    }
}
