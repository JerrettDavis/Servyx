using Servyx.Domain.Secrets;

namespace Servyx.Web.Authentication;

/// <summary>
/// Derives and verifies the single operator password's PBKDF2-HMAC-SHA256 hash. Pure computation over
/// strings and bytes: it neither reads nor writes storage, which is <see cref="OperatorCredentialStore"/>'s
/// job.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A thin, behavior-preserving wrapper over <see cref="Servyx.Domain.Secrets.PasswordHash"/>.</strong>
/// The PBKDF2 algorithm itself now lives there, shared with per-account user passwords
/// (<c>Servyx.Application.Users.IUserService</c>), so the two never risk drifting into two different, both
/// security-sensitive, implementations. Every member here keeps its original name, signature, and constant
/// value — this type is retained, rather than deleted in favor of direct calls to the shared type, purely so
/// <see cref="OperatorCredentialStore"/> and its existing tests need no changes at all.
/// </para>
/// <para>
/// <strong>Nothing here ever holds, stores, or compares a plaintext password beyond the single derivation
/// it was handed one for.</strong> <see cref="Create"/> returns an encoded verifier — algorithm, iteration
/// count, salt, derived key — and the plaintext is never part of it. <see cref="Verify"/> re-derives from
/// the candidate using the <em>stored</em> parameters and compares in fixed time.
/// </para>
/// <para>
/// <strong>Encoded form:</strong> <c>PBKDF2-SHA256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 key&gt;</c>.
/// See <see cref="Servyx.Domain.Secrets.PasswordHash"/>'s own remarks for the full rationale.
/// </para>
/// </remarks>
public static class OperatorPasswordHash
{
    /// <summary>The algorithm label that prefixes every encoded verifier.</summary>
    public const string AlgorithmLabel = PasswordHash.AlgorithmLabel;

    /// <summary>
    /// The PBKDF2 iteration count used for newly created verifiers. See
    /// <see cref="Servyx.Domain.Secrets.PasswordHash.Iterations"/>.
    /// </summary>
    public const int Iterations = PasswordHash.Iterations;

    /// <summary>Salt length in bytes, drawn from a cryptographic RNG per password set.</summary>
    public const int SaltSizeBytes = PasswordHash.SaltSizeBytes;

    /// <summary>Derived key length in bytes.</summary>
    public const int KeySizeBytes = PasswordHash.KeySizeBytes;

    /// <summary>
    /// Derives a new verifier for <paramref name="password"/> with a freshly generated random salt at
    /// <see cref="Iterations"/> iterations.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="password"/> is null, empty, or whitespace.</exception>
    public static string Create(string password) => PasswordHash.Create(password);

    /// <summary>
    /// Whether <paramref name="candidate"/> derives to the key inside <paramref name="encoded"/>, using the
    /// salt and iteration count recorded in <paramref name="encoded"/> itself.
    /// </summary>
    public static bool Verify(string? encoded, string? candidate) => PasswordHash.Verify(encoded, candidate);

    /// <summary>Encodes <paramref name="encoded"/> as the UTF-8 bytes persisted by the secret store.</summary>
    public static byte[] ToStoredBytes(string encoded) => PasswordHash.ToStoredBytes(encoded);

    /// <summary>Decodes the UTF-8 bytes read back from the secret store.</summary>
    public static string FromStoredBytes(ReadOnlySpan<byte> stored) => PasswordHash.FromStoredBytes(stored);
}
