using Servyx.Domain.Common;
using Servyx.Domain.Entities;

namespace Servyx.Domain.Users;

/// <summary>
/// Durable storage for the <see cref="User"/> rows behind Servyx's own account bookkeeping — the read/write
/// surface behind "create an account", "look one up", "list them", "change a role", and "activate/deactivate
/// one".
/// </summary>
/// <remarks>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The only implementation that can honour the word
/// "durable" is one backed by a store, and every infrastructure project references <c>Servyx.Domain</c> and
/// nothing else, by design (see the defending comments in those projects' csproj files). An abstraction
/// infrastructure must <em>implement</em> therefore has to be declared here — exactly the same reasoning
/// <see cref="Servyx.Domain.Hosts.IHostRepository"/> and <c>IServerRepository</c> already follow.
/// <c>Servyx.Infrastructure.Persistence</c> supplies the real, EF-backed implementation (<c>EfUserRepository</c>,
/// over the <c>Users</c> table).
/// <para>
/// <strong>Not yet consumed by authentication.</strong> This increment is the durable store only — no auth
/// pipeline reads from it yet. See <see cref="User"/>'s own remarks.
/// </para>
/// </remarks>
public interface IUserRepository
{
    /// <summary>Every currently-tracked <see cref="User"/> row, in no particular order.</summary>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);

    /// <summary>The tracked row for <paramref name="id"/>, or <see langword="null"/> if none exists.</summary>
    Task<User?> TryGetAsync(UserId id, CancellationToken ct = default);

    /// <summary>
    /// The tracked row for <paramref name="username"/>, or <see langword="null"/> if none exists. Lookup is
    /// case-sensitive at this layer — see <c>UserConfiguration</c> for the collation the unique index itself
    /// enforces.
    /// </summary>
    Task<User?> TryGetByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Persists a newly-created <see cref="User"/> row.</summary>
    Task AddAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Records a new role for <paramref name="id"/>. Returns the updated row, or <see langword="null"/> when
    /// no row exists for <paramref name="id"/>.
    /// </summary>
    Task<User?> SetRoleAsync(UserId id, UserRole role, CancellationToken ct = default);

    /// <summary>
    /// Records a new active/inactive posture for <paramref name="id"/> — the durable half of
    /// deactivate/reactivate. Returns the updated row, or <see langword="null"/> when no row exists for
    /// <paramref name="id"/>.
    /// </summary>
    Task<User?> SetActiveAsync(UserId id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Records a new password verifier for <paramref name="id"/>. Returns the updated row, or
    /// <see langword="null"/> when no row exists for <paramref name="id"/>. <paramref name="passwordHash"/>
    /// must already be an encoded verifier (see <see cref="Servyx.Domain.Secrets.PasswordHash.Create"/>) —
    /// this method never hashes, it only persists.
    /// </summary>
    Task<User?> SetPasswordHashAsync(UserId id, string passwordHash, CancellationToken ct = default);
}
