using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Entities;
using Servyx.Domain.Secrets;
using Servyx.Web.Authentication;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Authentication;

/// <summary>
/// Tests for <see cref="UserBootstrapMigration"/> — the one-time startup step that keeps an operator
/// upgrading from the single shared operator password from being locked out once sign-in switches to
/// per-account verification. See that type's own remarks for why the hash is reused rather than a first-login
/// reset flow being required.
/// </summary>
public class UserBootstrapMigrationTests
{
    private const string LegacyPassword = "correct-horse-battery-staple";

    private static ServiceProvider Compose(FakeUserRepository? users, OperatorCredentialStore? credentials)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (users is not null)
        {
            services.AddSingleton<Servyx.Domain.Users.IUserRepository>(users);
        }

        if (credentials is not null)
        {
            services.AddSingleton(credentials);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task An_install_with_a_legacy_password_and_no_users_gets_a_bootstrap_admin_account()
    {
        var users = new FakeUserRepository();
        var credentials = new OperatorCredentialStore(new RecordingSecretStore());
        await credentials.TrySetInitialPasswordAsync(LegacyPassword);

        using var provider = Compose(users, credentials);
        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);

        users.Rows.Should().ContainSingle();
        var migrated = users.Rows[0];

        migrated.Username.Should().Be(UserBootstrapMigration.BootstrapUsername);
        migrated.Role.Should().Be(UserRole.Admin);
        migrated.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task The_migrated_account_authenticates_with_the_exact_same_password_the_operator_already_had()
    {
        // The whole point: reusing the verifier byte-for-byte (PasswordHash is shared between the legacy
        // OperatorCredentialStore path and per-account User passwords) means the operator's existing password
        // keeps working unchanged — no new credential to choose or transmit at upgrade time.
        var users = new FakeUserRepository();
        var credentials = new OperatorCredentialStore(new RecordingSecretStore());
        await credentials.TrySetInitialPasswordAsync(LegacyPassword);

        using var provider = Compose(users, credentials);
        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);

        var migrated = users.Rows.Single();
        PasswordHash.Verify(migrated.PasswordHash, LegacyPassword).Should().BeTrue();
        PasswordHash.Verify(migrated.PasswordHash, "some-other-password").Should().BeFalse();
    }

    [Fact]
    public async Task A_genuinely_fresh_install_with_no_legacy_password_creates_no_account()
    {
        var users = new FakeUserRepository();
        var credentials = new OperatorCredentialStore(new RecordingSecretStore());

        using var provider = Compose(users, credentials);
        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);

        users.Rows.Should().BeEmpty(
            "a fresh install has nothing to migrate — /login's own first-run form creates the first account");
    }

    [Fact]
    public async Task An_install_that_already_has_a_user_is_never_touched()
    {
        var users = new FakeUserRepository
        {
            Rows =
            {
                new User
                {
                    Id = Servyx.Domain.Common.UserId.New(),
                    Username = "someone-else",
                    PasswordHash = PasswordHash.Create("irrelevant-password-1"),
                    Role = UserRole.Viewer,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            },
        };
        var credentials = new OperatorCredentialStore(new RecordingSecretStore());
        await credentials.TrySetInitialPasswordAsync(LegacyPassword);

        using var provider = Compose(users, credentials);
        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);

        users.Rows.Should().ContainSingle(
            "an install with an existing account must never gain a second, unrequested one");
        users.Rows[0].Username.Should().Be("someone-else");
    }

    [Fact]
    public async Task With_no_IUserRepository_composed_the_migration_is_a_no_op_rather_than_throwing()
    {
        var credentials = new OperatorCredentialStore(new RecordingSecretStore());
        await credentials.TrySetInitialPasswordAsync(LegacyPassword);

        using var provider = Compose(users: null, credentials);

        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);
        // No assertion beyond "did not throw" — there is nowhere to observe an effect without a repository.
    }

    [Fact]
    public async Task With_no_OperatorCredentialStore_composed_the_migration_is_a_no_op_rather_than_throwing()
    {
        var users = new FakeUserRepository();
        using var provider = Compose(users, credentials: null);

        await UserBootstrapMigration.MigrateLegacyOperatorPasswordAsync(provider);

        users.Rows.Should().BeEmpty();
    }
}
