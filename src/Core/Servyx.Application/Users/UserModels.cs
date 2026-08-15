using Servyx.Domain.Common;

namespace Servyx.Application.Users;

/// <summary>Which of the well-known outcomes <see cref="IUserService.CreateAsync"/> landed on.</summary>
public enum CreateUserOutcome
{
    /// <summary>A new <see cref="Servyx.Domain.Entities.User"/> row was created.</summary>
    Created,

    /// <summary>The requested username is blank or longer than the column allows.</summary>
    InvalidUsername,

    /// <summary>An account already exists under the requested username; no second row was created.</summary>
    UsernameTaken,

    /// <summary>The supplied password is shorter than <see cref="CreateUserResult.MinimumPasswordLength"/>.</summary>
    WeakPassword,
}

/// <summary>
/// The outcome of one <see cref="IUserService.CreateAsync"/> call. Every member of
/// <see cref="CreateUserOutcome"/> is an expected, non-exceptional outcome, matching the "results, not
/// exceptions, for expected failures" convention <c>RegistrationResult</c> already follows for host
/// registration.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this call landed on.</param>
/// <param name="UserId">The new row's id, when <paramref name="Outcome"/> is <see cref="CreateUserOutcome.Created"/>; otherwise null.</param>
/// <param name="Detail">A human-readable explanation for the non-success outcomes; otherwise null.</param>
public sealed record CreateUserResult(CreateUserOutcome Outcome, UserId? UserId, string? Detail)
{
    /// <summary>
    /// The minimum length accepted for a new account's password. Matches
    /// <c>OperatorCredentialStore.MinimumPasswordLength</c> — the same "length is the only guessing-cost
    /// control there is" reasoning applies to a per-account password with no lockout or rate limiter wired up
    /// yet.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>The maximum length accepted for a username, matching the column's mapped length.</summary>
    public const int MaximumUsernameLength = 200;

    /// <summary>A new account was created; <paramref name="id"/> is its id.</summary>
    public static CreateUserResult Created(UserId id) => new(CreateUserOutcome.Created, id, null);

    /// <summary>The requested username is not usable.</summary>
    public static CreateUserResult InvalidUsername(string detail) =>
        new(CreateUserOutcome.InvalidUsername, null, detail);

    /// <summary>An account already exists under this username.</summary>
    public static CreateUserResult UsernameTaken(string username) =>
        new(CreateUserOutcome.UsernameTaken, null,
            $"An account already exists under the username '{username}'.");

    /// <summary>The supplied password is shorter than <see cref="MinimumPasswordLength"/>.</summary>
    public static CreateUserResult WeakPassword() =>
        new(CreateUserOutcome.WeakPassword, null,
            $"The password must be at least {MinimumPasswordLength} characters.");
}

/// <summary>Which of the well-known outcomes <see cref="IUserService.ChangePasswordAsync"/> landed on.</summary>
public enum ChangePasswordOutcome
{
    /// <summary>The stored verifier was replaced.</summary>
    Changed,

    /// <summary>
    /// The supplied current password did not verify. Also returned — deliberately indistinguishable from a
    /// wrong password — when the account does not exist or is deactivated, so this member can never be used
    /// to probe either fact from outside. Mirrors <c>OperatorCredentialStore.ChangePasswordAsync</c>'s own
    /// "wrong password and no account are one outcome" discipline.
    /// </summary>
    CurrentPasswordIncorrect,

    /// <summary>The proposed new password was refused before anything was written — too short, or blank.</summary>
    WeakPassword,
}

/// <summary>
/// The outcome of one <see cref="IUserService.ChangePasswordAsync"/> call. Every member of
/// <see cref="ChangePasswordOutcome"/> is an expected, non-exceptional outcome, matching
/// <see cref="CreateUserResult"/>'s own "results, not exceptions" convention.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this call landed on.</param>
/// <param name="Detail">A human-readable explanation for the non-success outcomes; otherwise null.</param>
public sealed record ChangePasswordResult(ChangePasswordOutcome Outcome, string? Detail)
{
    /// <summary>The verifier was replaced.</summary>
    public static readonly ChangePasswordResult Changed = new(ChangePasswordOutcome.Changed, null);

    /// <summary>
    /// The current password did not verify (or the account does not exist, or is deactivated — see
    /// <see cref="ChangePasswordOutcome.CurrentPasswordIncorrect"/>'s own remarks).
    /// </summary>
    public static readonly ChangePasswordResult CurrentPasswordIncorrect =
        new(ChangePasswordOutcome.CurrentPasswordIncorrect, null);

    /// <summary>The proposed new password is shorter than <see cref="CreateUserResult.MinimumPasswordLength"/>.</summary>
    public static ChangePasswordResult WeakPassword() =>
        new(ChangePasswordOutcome.WeakPassword,
            $"The password must be at least {CreateUserResult.MinimumPasswordLength} characters.");
}

/// <summary>Which of the well-known outcomes <see cref="IUserService.ResetPasswordAsync"/> landed on.</summary>
public enum ResetPasswordOutcome
{
    /// <summary>The stored verifier was replaced without the caller having to produce the current password.</summary>
    Reset,

    /// <summary>No account exists under the requested username; nothing was written.</summary>
    UserNotFound,

    /// <summary>The proposed new password was refused before anything was written — too short, or blank.</summary>
    WeakPassword,
}

/// <summary>
/// The outcome of one <see cref="IUserService.ResetPasswordAsync"/> call. Every member of
/// <see cref="ResetPasswordOutcome"/> is an expected, non-exceptional outcome, matching
/// <see cref="CreateUserResult"/>'s own "results, not exceptions" convention.
/// </summary>
/// <param name="Outcome">Which of the well-known outcomes this call landed on.</param>
/// <param name="Detail">A human-readable explanation for the non-success outcomes; otherwise null.</param>
public sealed record ResetPasswordResult(ResetPasswordOutcome Outcome, string? Detail)
{
    /// <summary>The verifier was replaced.</summary>
    public static readonly ResetPasswordResult Reset = new(ResetPasswordOutcome.Reset, null);

    /// <summary>No account exists under the requested username.</summary>
    public static ResetPasswordResult UserNotFound(string username) =>
        new(ResetPasswordOutcome.UserNotFound, $"No account exists under the username '{username}'.");

    /// <summary>The proposed new password is shorter than <see cref="CreateUserResult.MinimumPasswordLength"/>.</summary>
    public static ResetPasswordResult WeakPassword() =>
        new(ResetPasswordOutcome.WeakPassword,
            $"The password must be at least {CreateUserResult.MinimumPasswordLength} characters.");
}
