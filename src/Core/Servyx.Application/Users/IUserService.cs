using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Application.Users;

/// <summary>
/// Application-layer surface over Servyx user accounts: create, look up, list, change a role,
/// activate/deactivate, and verify a password. The counterpart, at the account level, to
/// <c>IHostRegistrationService</c> at the host level.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Foundation only — not yet wired to authentication.</strong> This increment adds the entity, its
/// durable storage, and this service; nothing in the app's login path calls it yet. The app continues to
/// authenticate through the single shared operator password
/// (<c>Servyx.Web.Authentication.OperatorCredentialStore</c>) until a later increment switches the auth
/// pipeline over. See <see cref="Servyx.Domain.Entities.User"/>'s own remarks.
/// </para>
/// <para>
/// <strong>Password hashing.</strong> Every method here that creates or rotates a password hashes it through
/// <see cref="Servyx.Domain.Secrets.PasswordHash"/> — the same PBKDF2-HMAC-SHA256 algorithm
/// <c>OperatorCredentialStore</c> uses for the single shared operator password, extracted so the two paths
/// can never drift into two different implementations of the same security-sensitive code. A plaintext
/// password is never persisted, logged, or returned by any member here.
/// </para>
/// </remarks>
public interface IUserService
{
    /// <summary>
    /// Creates a new user account with <paramref name="username"/>, <paramref name="password"/> hashed for
    /// storage, and <paramref name="role"/>. Fails, writing nothing, when the username is blank/too long or
    /// already taken, or the password does not meet <see cref="CreateUserResult"/>'s minimum length — see
    /// <see cref="CreateUserOutcome"/> for every expected outcome.
    /// </summary>
    /// <param name="username">The account's sign-in name. Must be unique.</param>
    /// <param name="password">The account's initial plaintext password. Hashed before anything is written; never persisted or returned.</param>
    /// <param name="role">The role to create the account with.</param>
    /// <param name="actor">The authenticated caller's identity, for the audit trail. Not yet persisted on the row (no such column exists), but required so every call site is ready for one.</param>
    /// <param name="ct">Cancels the creation.</param>
    Task<CreateUserResult> CreateAsync(
        string username, string password, UserRole role, string actor, CancellationToken ct = default);

    /// <summary>The tracked account for <paramref name="id"/>, or <see langword="null"/> if none exists.</summary>
    Task<User?> TryGetAsync(UserId id, CancellationToken ct = default);

    /// <summary>The tracked account for <paramref name="username"/>, or <see langword="null"/> if none exists.</summary>
    Task<User?> TryGetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Every account Servyx currently has, for display.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Changes <paramref name="id"/>'s role to <paramref name="role"/>. Returns <see langword="false"/> when
    /// no account exists for <paramref name="id"/>.
    /// </summary>
    Task<bool> ChangeRoleAsync(UserId id, UserRole role, string actor, CancellationToken ct = default);

    /// <summary>
    /// Sets whether <paramref name="id"/> may currently sign in. Returns <see langword="false"/> when no
    /// account exists for <paramref name="id"/>. The row itself is never removed — see
    /// <see cref="Servyx.Domain.Entities.User.IsActive"/>'s own remarks.
    /// </summary>
    Task<bool> SetActiveAsync(UserId id, bool isActive, string actor, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="password"/> is <paramref name="username"/>'s current password. Returns
    /// <see langword="false"/> — never throws for an ordinary lookup miss — when no account exists for
    /// <paramref name="username"/>, and also when the account exists but
    /// <see cref="Servyx.Domain.Entities.User.IsActive"/> is <see langword="false"/>: a deactivated account
    /// authenticates nobody, the same fail-closed posture <c>OperatorCredentialStore</c> takes for an
    /// unbootstrapped install.
    /// </summary>
    Task<bool> VerifyPasswordAsync(string username, string? password, CancellationToken ct = default);

    /// <summary>
    /// Changes <paramref name="username"/>'s own password, but only for a caller that can already produce the
    /// current one — the self-service counterpart to <see cref="CreateAsync"/>'s admin-issued initial
    /// password. Returns <see cref="ChangePasswordOutcome.CurrentPasswordIncorrect"/> — writing nothing —
    /// when <paramref name="currentPassword"/> does not verify, and also when no active account exists under
    /// <paramref name="username"/>; see <see cref="ChangePasswordOutcome.CurrentPasswordIncorrect"/>'s own
    /// remarks on why those are deliberately the same outcome.
    /// </summary>
    /// <param name="username">The account whose password is being changed.</param>
    /// <param name="currentPassword">The password in force now. Verified before anything is written.</param>
    /// <param name="newPassword">The replacement. Hashed before anything is written; never persisted or returned.</param>
    /// <param name="ct">Cancels the change.</param>
    Task<ChangePasswordResult> ChangePasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct = default);
}
