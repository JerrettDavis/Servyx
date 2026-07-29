using Servyx.Domain.Secrets;

namespace Servyx.Web.Authentication;

/// <summary>
/// The one place the single operator's password verifier is written to and read from. It owns no storage of
/// its own: everything goes through the existing <see cref="ISecretStore"/> abstraction at one fixed
/// <see cref="SecretUrn"/>, so the operator password is protected by exactly the same encrypted-at-rest,
/// sandboxed-path machinery as every other Servyx secret.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only a verifier is ever persisted.</strong> The bytes handed to
/// <see cref="ISecretStore.SetAsync"/> are <see cref="OperatorPasswordHash.Create"/>'s output — algorithm,
/// iteration count, salt, derived key — and the plaintext password appears nowhere in them. Verification
/// re-derives and compares in constant time; there is no code path in this type that compares strings.
/// </para>
/// <para>
/// <strong>First-run bootstrap is one-time, not a back door.</strong>
/// <see cref="TrySetInitialPasswordAsync"/> refuses — returning <see langword="false"/> without touching the
/// store — the moment a verifier already exists, and the check and the write happen under a single lock so
/// two simultaneous first-run requests cannot both win. Once a password is set, the only way to change it is
/// <see cref="ChangePasswordAsync"/>, which verifies the current password first.
/// </para>
/// </remarks>
public sealed class OperatorCredentialStore
{
    /// <summary>
    /// Where the verifier lives: <c>secret://global/servyx/auth/operator-password</c>. Follows the
    /// established <c>secret://{scope}/{scopeId}/{category}/{name}</c> convention and the existing use of the
    /// <c>global</c> scope for process-wide credentials (compare
    /// <c>secret://global/digitalocean/api/token</c> and <c>secret://global/azure/api/client-secret</c>).
    /// </summary>
    public static readonly SecretUrn PasswordUrn =
        SecretUrn.Create("global", "servyx", "auth", "operator-password");

    /// <summary>
    /// The minimum length accepted when a password is set. A single-operator box has no lockout and no rate
    /// limiter (see the composition root's remarks), so length is the only guessing-cost control there is.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>The actor recorded against every write, since a secret write is an audit event.</summary>
    private const string Actor = "servyx.web/operator";

    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Creates a store over <paramref name="secrets"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="secrets"/> is null.</exception>
    public OperatorCredentialStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    /// <summary>Whether an operator password has ever been set on this install.</summary>
    public Task<bool> IsPasswordSetAsync(CancellationToken ct = default)
        => _secrets.ExistsAsync(PasswordUrn, ct);

    /// <summary>
    /// Sets the operator password for the first and only time. Returns <see langword="false"/> — writing
    /// nothing — if a password has already been set, which is what stops the first-run flow from ever being
    /// a permanent way in.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="password"/> is shorter than <see cref="MinimumPasswordLength"/>.
    /// </exception>
    public async Task<bool> TrySetInitialPasswordAsync(string password, CancellationToken ct = default)
    {
        ValidateNewPassword(password);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _secrets.ExistsAsync(PasswordUrn, ct).ConfigureAwait(false))
            {
                return false;
            }

            await WriteAsync(password, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Replaces the operator password, but only for a caller that can already produce the current one.
    /// Returns <see langword="false"/> — writing nothing — if <paramref name="currentPassword"/> does not
    /// verify, or if no password has been set yet (in which case
    /// <see cref="TrySetInitialPasswordAsync"/> is the only route in).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="newPassword"/> is shorter than <see cref="MinimumPasswordLength"/>.
    /// </exception>
    public async Task<bool> ChangePasswordAsync(
        string currentPassword, string newPassword, CancellationToken ct = default)
    {
        ValidateNewPassword(newPassword);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await VerifyPasswordAsync(currentPassword, ct).ConfigureAwait(false))
            {
                return false;
            }

            await WriteAsync(newPassword, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the operator password. Returns <see langword="false"/> when no
    /// password has been set: an install that has not been bootstrapped authenticates nobody.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(string? candidate, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        using var lease = await _secrets.GetAsync(PasswordUrn, ct).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        return OperatorPasswordHash.Verify(OperatorPasswordHash.FromStoredBytes(lease.Value), candidate);
    }

    private async Task WriteAsync(string password, CancellationToken ct)
    {
        var encoded = OperatorPasswordHash.Create(password);
        await _secrets
            .SetAsync(PasswordUrn, OperatorPasswordHash.ToStoredBytes(encoded), Actor, ct)
            .ConfigureAwait(false);
    }

    private static void ValidateNewPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"The operator password must be at least {MinimumPasswordLength} characters.",
                nameof(password));
        }
    }
}
