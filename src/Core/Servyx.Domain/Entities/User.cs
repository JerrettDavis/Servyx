using Servyx.Domain.Common;

namespace Servyx.Domain.Entities;

/// <summary>
/// The level of access a <see cref="User"/> holds. Foundation only: no policy in this codebase gates on it
/// yet (that is a later, separate increment) — this type exists now so the schema and the entity do not need
/// to change shape when that wiring lands.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Explicit, gapped numeric values, not the default 0/1/2.</strong> The values are persisted as
/// names, not ordinals (see <c>UserConfiguration</c>), so nothing about storage depends on the numbering —
/// but the gaps still matter: they let a role be inserted between two existing ones later (e.g. something
/// between <see cref="Operator"/> and <see cref="Admin"/>) without renumbering every value that already
/// shipped, which would silently change the outcome of any numeric comparison (<c>role &gt;= Operator</c>)
/// written against the old numbering.
/// </para>
/// <para>
/// <strong>Callers must never branch on the string name of a role.</strong> Comparing
/// <c>user.Role == UserRole.Admin</c> is fine; comparing <c>user.Role.ToString() == "Admin"</c> or hardcoding
/// role names in configuration/UI is exactly the magic-string coupling this enum exists to avoid — it makes
/// adding a role a text-search exercise instead of a compiler-checked one.
/// </para>
/// </remarks>
public enum UserRole
{
    /// <summary>Read-only access. The least privileged role.</summary>
    Viewer = 0,

    /// <summary>Day-to-day server operation, short of account/role administration.</summary>
    Operator = 10,

    /// <summary>Full access, including managing other users' accounts and roles.</summary>
    Admin = 20,
}

/// <summary>
/// A Servyx user account. Persistence-ignorant: this type carries no storage-specific behavior, and
/// infrastructure layers are responsible for mapping it to and from whatever store is in use.
/// </summary>
/// <remarks>
/// <strong>Foundation for the identity/RBAC system, not yet wired to anything.</strong> This increment adds
/// the entity, its durable storage, and an application-layer service over it — nothing here is consulted by
/// authentication or authorization yet. The app continues to authenticate through the single shared operator
/// password (see <c>Servyx.Web.Authentication.OperatorCredentialStore</c>) until a later increment switches
/// the auth pipeline over to these rows.
/// </remarks>
public sealed class User
{
    /// <summary>The user's stable identifier.</summary>
    public required UserId Id { get; set; }

    /// <summary>
    /// The user's sign-in name. Unique across all accounts — enforced by a unique index at the database, not
    /// only by callers pre-checking (see <c>UserConfiguration</c>).
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// The user's password, as an opaque verifier — never the plaintext. See
    /// <see cref="Servyx.Domain.Secrets.PasswordHash"/>, the same PBKDF2-HMAC-SHA256 algorithm
    /// <c>OperatorCredentialStore</c> uses for the single shared operator password.
    /// </summary>
    public required string PasswordHash { get; set; }

    /// <summary>The user's current role. See <see cref="UserRole"/>'s own remarks on how it may grow.</summary>
    public required UserRole Role { get; set; }

    /// <summary>
    /// Whether this account may currently sign in. A deactivated account's row is kept, not deleted — the
    /// same "forget means stop tracking, not erase" discipline <c>Host</c>/<c>Server</c> follow for their own
    /// removal paths — so a reactivation restores the exact same account rather than requiring a new one.
    /// </summary>
    public required bool IsActive { get; set; }

    /// <summary>When this account was created.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}
