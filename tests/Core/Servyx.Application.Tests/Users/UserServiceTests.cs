using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Users;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Secrets;
using Servyx.Domain.Users;

namespace Servyx.Application.Tests.Users;

/// <summary>
/// Tests for <see cref="UserService"/> — create/read/list, role changes, activate/deactivate, and password
/// verification. Follows <c>HostRegistrationServiceTests</c>'s convention of a hand-written fake repository
/// that carries state across calls, rather than a sequence of stubbed returns.
/// </summary>
public class UserServiceTests
{
    private const string Actor = "operator";

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeUserRepository : IUserRepository
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

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (UserService Service, FakeUserRepository Repository) Build()
    {
        var repository = new FakeUserRepository();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));
        var service = new UserService(repository, NullLogger<UserService>.Instance, time);

        return (service, repository);
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_user_persists_a_row_with_a_hashed_password_never_the_plaintext()
    {
        var (service, repository) = Build();

        var result = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Operator, Actor);

        result.Outcome.Should().Be(CreateUserOutcome.Created);
        result.UserId.Should().NotBeNull();

        repository.Rows.Should().ContainSingle();
        var row = repository.Rows[0];

        row.Id.Should().Be(result.UserId!.Value);
        row.Username.Should().Be("alice");
        row.Role.Should().Be(UserRole.Operator);
        row.IsActive.Should().BeTrue("a newly created account can sign in immediately");
        row.CreatedAt.Should().Be(new DateTimeOffset(2026, 8, 13, 9, 0, 0, TimeSpan.Zero));

        row.PasswordHash.Should().NotContain("correct-horse-battery-staple");
        PasswordHash.Verify(row.PasswordHash, "correct-horse-battery-staple").Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_user_trims_the_username()
    {
        var (service, repository) = Build();

        await service.CreateAsync("  alice  ", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        repository.Rows.Single().Username.Should().Be("alice");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Creating_a_user_with_a_blank_username_is_refused(string username)
    {
        var (service, repository) = Build();

        var result = await service.CreateAsync(username, "correct-horse-battery-staple", UserRole.Viewer, Actor);

        result.Outcome.Should().Be(CreateUserOutcome.InvalidUsername);
        repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_user_with_a_username_over_the_maximum_length_is_refused()
    {
        var (service, repository) = Build();
        var tooLong = new string('a', CreateUserResult.MaximumUsernameLength + 1);

        var result = await service.CreateAsync(tooLong, "correct-horse-battery-staple", UserRole.Viewer, Actor);

        result.Outcome.Should().Be(CreateUserOutcome.InvalidUsername);
        repository.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task Creating_a_user_with_a_weak_password_is_refused(string password)
    {
        var (service, repository) = Build();

        var result = await service.CreateAsync("alice", password, UserRole.Viewer, Actor);

        result.Outcome.Should().Be(CreateUserOutcome.WeakPassword);
        repository.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_second_user_under_an_existing_username_is_refused()
    {
        var (service, repository) = Build();

        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);
        var second = await service.CreateAsync("alice", "another-strong-password", UserRole.Admin, Actor);

        second.Outcome.Should().Be(CreateUserOutcome.UsernameTaken);
        repository.Rows.Should().ContainSingle("no second row may be created under a taken username");
    }

    // ── Lookup / list ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryGetAsync_ReturnsNull_ForAnUnknownId()
    {
        var (service, _) = Build();

        (await service.TryGetAsync(UserId.New())).Should().BeNull();
    }

    [Fact]
    public async Task TryGetByUsernameAsync_ReturnsTheMatchingRow()
    {
        var (service, _) = Build();
        var created = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        var found = await service.TryGetByUsernameAsync("alice");

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.UserId);
    }

    [Fact]
    public async Task ListAsync_ReturnsEveryCreatedUser()
    {
        var (service, _) = Build();

        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);
        await service.CreateAsync("bob", "another-strong-password", UserRole.Admin, Actor);

        var all = await service.ListAsync();

        all.Select(u => u.Username).Should().BeEquivalentTo(["alice", "bob"]);
    }

    // ── Role / activation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRoleAsync_UpdatesTheStoredRole()
    {
        var (service, repository) = Build();
        var created = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        var changed = await service.ChangeRoleAsync(created.UserId!.Value, UserRole.Admin, Actor);

        changed.Should().BeTrue();
        repository.Rows.Single().Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task ChangeRoleAsync_ForAnUnknownId_ReturnsFalse()
    {
        var (service, _) = Build();

        (await service.ChangeRoleAsync(UserId.New(), UserRole.Admin, Actor)).Should().BeFalse();
    }

    [Fact]
    public async Task SetActiveAsync_CanDeactivateAndReactivate_WithoutRemovingTheRow()
    {
        var (service, repository) = Build();
        var created = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        (await service.SetActiveAsync(created.UserId!.Value, false, Actor)).Should().BeTrue();
        repository.Rows.Should().ContainSingle("deactivation must not delete the row");
        repository.Rows.Single().IsActive.Should().BeFalse();

        (await service.SetActiveAsync(created.UserId!.Value, true, Actor)).Should().BeTrue();
        repository.Rows.Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SetActiveAsync_ForAnUnknownId_ReturnsFalse()
    {
        var (service, _) = Build();

        (await service.SetActiveAsync(UserId.New(), false, Actor)).Should().BeFalse();
    }

    // ── VerifyPasswordAsync ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyPasswordAsync_AcceptsTheCorrectPassword_AndRejectsAnyOther()
    {
        var (service, _) = Build();
        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        (await service.VerifyPasswordAsync("alice", "correct-horse-battery-staple")).Should().BeTrue();
        (await service.VerifyPasswordAsync("alice", "wrong-password-entirely")).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPasswordAsync_ForAnUnknownUsername_ReturnsFalseRatherThanThrowing()
    {
        var (service, _) = Build();

        (await service.VerifyPasswordAsync("nobody", "anything at all")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task VerifyPasswordAsync_ForAnEmptyCandidate_ReturnsFalse(string? candidate)
    {
        var (service, _) = Build();
        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        (await service.VerifyPasswordAsync("alice", candidate)).Should().BeFalse();
    }

    [Fact]
    public async Task VerifyPasswordAsync_ForADeactivatedAccount_AuthenticatesNobody()
    {
        var (service, _) = Build();
        var created = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);
        await service.SetActiveAsync(created.UserId!.Value, false, Actor);

        (await service.VerifyPasswordAsync("alice", "correct-horse-battery-staple")).Should().BeFalse(
            "a deactivated account must authenticate nobody, even with the right password");
    }

    // ── ChangePasswordAsync (self-service) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePasswordAsync_WithTheCorrectCurrentPassword_ReplacesTheStoredVerifier()
    {
        var (service, repository) = Build();
        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        var result = await service.ChangePasswordAsync(
            "alice", "correct-horse-battery-staple", "a-brand-new-password-1");

        result.Outcome.Should().Be(ChangePasswordOutcome.Changed);
        (await service.VerifyPasswordAsync("alice", "a-brand-new-password-1")).Should().BeTrue();
        (await service.VerifyPasswordAsync("alice", "correct-horse-battery-staple")).Should().BeFalse(
            "the old password must stop working the moment the new one is stored");

        repository.Rows.Single().PasswordHash.Should().NotContain("a-brand-new-password-1");
    }

    [Fact]
    public async Task ChangePasswordAsync_WithTheWrongCurrentPassword_ChangesNothing()
    {
        var (service, _) = Build();
        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        var result = await service.ChangePasswordAsync("alice", "not-the-password", "a-brand-new-password-1");

        result.Outcome.Should().Be(ChangePasswordOutcome.CurrentPasswordIncorrect);
        (await service.VerifyPasswordAsync("alice", "correct-horse-battery-staple")).Should().BeTrue(
            "a refused rotation must not touch the stored credential");
    }

    [Fact]
    public async Task ChangePasswordAsync_ForAnUnknownUsername_ReportsTheSameOutcomeAsAWrongPassword()
    {
        // Deliberate: a distinct outcome here would let this member be used to probe which usernames exist.
        var (service, _) = Build();

        var result = await service.ChangePasswordAsync("nobody", "anything-at-all", "a-brand-new-password-1");

        result.Outcome.Should().Be(ChangePasswordOutcome.CurrentPasswordIncorrect);
    }

    [Fact]
    public async Task ChangePasswordAsync_ForADeactivatedAccount_ReportsTheSameOutcomeAsAWrongPassword()
    {
        var (service, _) = Build();
        var created = await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);
        await service.SetActiveAsync(created.UserId!.Value, false, Actor);

        var result = await service.ChangePasswordAsync(
            "alice", "correct-horse-battery-staple", "a-brand-new-password-1");

        result.Outcome.Should().Be(ChangePasswordOutcome.CurrentPasswordIncorrect,
            "a deactivated account must not be usable to rotate its own password back in, even with the " +
            "right current one");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public async Task ChangePasswordAsync_WithAWeakNewPassword_IsRefused(string newPassword)
    {
        var (service, _) = Build();
        await service.CreateAsync("alice", "correct-horse-battery-staple", UserRole.Viewer, Actor);

        var result = await service.ChangePasswordAsync("alice", "correct-horse-battery-staple", newPassword);

        result.Outcome.Should().Be(ChangePasswordOutcome.WeakPassword);
        (await service.VerifyPasswordAsync("alice", "correct-horse-battery-staple")).Should().BeTrue(
            "a refused rotation must not touch the stored credential");
    }
}
