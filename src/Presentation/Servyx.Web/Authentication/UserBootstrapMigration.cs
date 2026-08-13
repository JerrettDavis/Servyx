using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Domain.Users;

namespace Servyx.Web.Authentication;

/// <summary>
/// The one-time startup step that turns an install upgrading from the single shared operator password into a
/// genuinely multi-user one without locking its existing operator out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why reusing the hash, not a first-login reset flow.</strong> <see cref="OperatorPasswordHash"/> and
/// <c>Servyx.Application.Users.IUserService</c>'s per-account passwords are both thin wrappers over the exact
/// same algorithm, <see cref="Servyx.Domain.Secrets.PasswordHash"/> (see that type's own remarks) — the
/// encoded verifier format is byte-for-byte identical. That makes copying the stored verifier straight into a
/// new <c>User.PasswordHash</c> column strictly safer than a "set a new password on first login" flow would
/// be: it requires no new credential to be transmitted or chosen under pressure at upgrade time, an operator's
/// existing password keeps working unchanged, and there is no window in which the install has a User table
/// but no working credential for it.
/// </para>
/// <para>
/// <strong>Runs at most once, and only migrates — it never bootstraps a fresh install.</strong> If any
/// <c>User</c> row already exists, this is a no-op: either a previous run already migrated, or an operator (or
/// a later Increment 4 admin flow) created accounts some other way, and either way this must never overwrite
/// or duplicate them. If no legacy operator password was ever set either, this is also a no-op — that is
/// a genuinely fresh install, and <c>/login</c>'s own first-run form (now backed by
/// <c>Servyx.Application.Users.IUserService.CreateAsync</c> instead of
/// <see cref="OperatorCredentialStore.TrySetInitialPasswordAsync"/>) is what creates its first account, the
/// same one-time-bootstrap discipline this migration cannot replace.
/// </para>
/// <para>
/// <strong>Not locked against a concurrent bootstrap request.</strong> This runs once, synchronously, during
/// startup before the HTTP pipeline is listening (see <c>Program.cs</c>), so there is no concurrent request
/// for it to race — unlike <c>AuthenticationEndpoints.BootstrapAsync</c>'s first-run form, which is reachable
/// the moment the process starts serving and therefore does take its own lock.
/// </para>
/// </remarks>
public static class UserBootstrapMigration
{
    /// <summary>
    /// The username the migrated account is created under. Fixed, not operator-chosen: the legacy install had
    /// no concept of a username to preserve, and a stable, well-known name is what lets the upgrade path be
    /// documented ("sign in as <c>admin</c> with your existing password") rather than discovered.
    /// </summary>
    public const string BootstrapUsername = "admin";

    /// <summary>
    /// Migrates the legacy shared operator password into a bootstrap <see cref="UserRole.Admin"/> account, if
    /// this process has both an <see cref="IUserRepository"/> and an <see cref="OperatorCredentialStore"/>
    /// composed and the conditions in this type's own remarks are met. A no-op, not a failure, when either
    /// collaborator is absent, when <c>User</c> rows already exist, or when no legacy password was ever set.
    /// </summary>
    /// <param name="services">The built service provider to resolve collaborators from.</param>
    /// <param name="ct">Cancels the migration.</param>
    public static async Task MigrateLegacyOperatorPasswordAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var users = services.GetService<IUserRepository>();
        var credentials = services.GetService<OperatorCredentialStore>();
        if (users is null || credentials is null)
        {
            return;
        }

        var existing = await users.ListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            // Already migrated (or an account exists some other way). Never overwrite or duplicate.
            return;
        }

        var encodedHash = await credentials.TryReadEncodedHashAsync(ct).ConfigureAwait(false);
        if (encodedHash is null)
        {
            // A genuinely fresh install: no legacy password to migrate. /login's own first-run form creates
            // the first account instead.
            return;
        }

        var bootstrapUser = new User
        {
            Id = UserId.New(),
            Username = BootstrapUsername,
            PasswordHash = encodedHash,
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await users.AddAsync(bootstrapUser, ct).ConfigureAwait(false);

        services.GetService<ILoggerFactory>()
            ?.CreateLogger(OperatorAuthentication.AuditLogCategory)
            .LogWarning(
                AuthenticationAudit.LegacyOperatorPasswordMigrated,
                "Migrated the pre-multi-user shared operator password into a bootstrap Admin account named "
                + "'{Username}'. Sign in with that username and the same password used before this upgrade.",
                BootstrapUsername);
    }
}
