using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Users;

namespace Servyx.Infrastructure.Persistence.Tests;

/// <summary>
/// A minimal <see cref="IDbContextFactory{TContext}"/> over a <see cref="SqliteDatabaseFixture"/>'s
/// already-migrated connection, so <see cref="EfUserRepository"/> — which takes a factory rather than a
/// context directly, see its own remarks — can be exercised against the same real, relational, throwaway
/// database every other persistence test uses. Mirrors <c>EfServerRepositoryTests</c>' own
/// <c>FixtureDbContextFactory</c>.
/// </summary>
file sealed class FixtureDbContextFactory(SqliteDatabaseFixture fixture) : IDbContextFactory<ServyxDbContext>
{
    public ServyxDbContext CreateDbContext() => fixture.CreateContext();
}

/// <summary>
/// Tests for <see cref="EfUserRepository"/>, the durable store behind Servyx's own user account bookkeeping:
/// a row must be listable, findable by id and by username, and must survive a simulated restart (a disposed
/// context replaced by a brand-new one, per <see cref="SqliteDatabaseFixture"/>'s own remarks) exactly like
/// every other row in this database. Also pins the unique index on <c>Username</c> against the real,
/// relational database — not only <c>UserService</c>'s own pre-check.
/// </summary>
public class EfUserRepositoryTests
{
    private static User NewUser(UserId? id = null, string username = "alice", UserRole role = UserRole.Viewer) => new()
    {
        Id = id ?? UserId.New(),
        Username = username,
        PasswordHash = "PBKDF2-SHA256$600000$c2FsdA==$a2V5$",
        Role = role,
        IsActive = true,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task AddAsync_then_ListAsync_finds_the_row_through_a_new_context()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser();

        await repository.AddAsync(user);

        var all = await repository.ListAsync();
        all.Should().ContainSingle(u => u.Id == user.Id && u.Username == "alice");
    }

    [Fact]
    public async Task TryGetAsync_finds_a_tracked_row_by_id()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser();
        await repository.AddAsync(user);

        var loaded = await repository.TryGetAsync(user.Id);

        loaded.Should().NotBeNull();
        loaded!.Username.Should().Be("alice");
        loaded.Role.Should().Be(UserRole.Viewer);
        loaded.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task TryGetAsync_an_unknown_id_returns_null()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));

        (await repository.TryGetAsync(UserId.New())).Should().BeNull();
    }

    [Fact]
    public async Task TryGetByUsernameAsync_finds_a_tracked_row_by_username()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser(username: "bob");
        await repository.AddAsync(user);

        var loaded = await repository.TryGetByUsernameAsync("bob");

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(user.Id);
    }

    [Fact]
    public async Task TryGetByUsernameAsync_an_unknown_username_returns_null()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));

        (await repository.TryGetByUsernameAsync("nobody")).Should().BeNull();
    }

    [Fact]
    public async Task Username_IsUnique_AtTheDatabase()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        await repository.AddAsync(NewUser(username: "shared-name"));

        var act = async () => await repository.AddAsync(NewUser(username: "shared-name"));

        // Enforced at the database, not only in UserService's own pre-check — see UserConfiguration's unique
        // index on Username, matching HostConfiguration's discipline for Host.Name.
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SetRoleAsync_persists_the_new_role()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser(role: UserRole.Viewer);
        await repository.AddAsync(user);

        var updated = await repository.SetRoleAsync(user.Id, UserRole.Admin);

        updated.Should().NotBeNull();
        updated!.Role.Should().Be(UserRole.Admin);

        // Read through a NEW context, so this proves a real write/read cycle rather than a still-live
        // identity map handing back the object that was just mutated.
        (await repository.TryGetAsync(user.Id))!.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task SetRoleAsync_an_unknown_id_reports_null_and_writes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser(role: UserRole.Viewer);
        await repository.AddAsync(user);

        var updated = await repository.SetRoleAsync(UserId.New(), UserRole.Admin);

        updated.Should().BeNull();
        (await repository.TryGetAsync(user.Id))!.Role.Should().Be(UserRole.Viewer);
    }

    [Fact]
    public async Task SetActiveAsync_can_deactivate_and_reactivate_without_removing_the_row()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser();
        await repository.AddAsync(user);

        var deactivated = await repository.SetActiveAsync(user.Id, false);
        deactivated.Should().NotBeNull();
        deactivated!.IsActive.Should().BeFalse();
        (await repository.ListAsync()).Should().ContainSingle("deactivation must not delete the row");

        var reactivated = await repository.SetActiveAsync(user.Id, true);
        reactivated!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetActiveAsync_an_unknown_id_reports_null_and_writes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));

        (await repository.SetActiveAsync(UserId.New(), false)).Should().BeNull();
    }

    [Fact]
    public async Task SetPasswordHashAsync_persists_the_new_verifier()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));
        var user = NewUser();
        await repository.AddAsync(user);

        var updated = await repository.SetPasswordHashAsync(user.Id, "PBKDF2-SHA256$600000$bmV3$dmVyaWZpZXI=");

        updated.Should().NotBeNull();
        (await repository.TryGetAsync(user.Id))!.PasswordHash.Should().Be("PBKDF2-SHA256$600000$bmV3$dmVyaWZpZXI=");
    }

    [Fact]
    public async Task SetPasswordHashAsync_an_unknown_id_reports_null_and_writes_nothing()
    {
        using var fixture = new SqliteDatabaseFixture();
        var repository = new EfUserRepository(new FixtureDbContextFactory(fixture));

        (await repository.SetPasswordHashAsync(UserId.New(), "PBKDF2-SHA256$600000$bmV3$dmVyaWZpZXI=")).Should().BeNull();
    }
}
