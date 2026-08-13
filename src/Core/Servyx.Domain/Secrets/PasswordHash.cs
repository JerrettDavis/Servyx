using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Servyx.Domain.Secrets;

/// <summary>
/// Derives and verifies password hashes using PBKDF2-HMAC-SHA256. Pure computation over strings and bytes: it
/// neither reads nor writes storage — that is each caller's own job (see
/// <c>Servyx.Web.Authentication.OperatorCredentialStore</c> for the single shared operator password, and
/// <c>Servyx.Application.Users.IUserService</c> for per-account user passwords).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The one place this algorithm is implemented.</strong> Extracted from what was originally
/// <c>Servyx.Web.Authentication.OperatorPasswordHash</c>'s private implementation so that a second caller —
/// user account passwords — never has to duplicate security-sensitive hashing code. <c>OperatorPasswordHash</c>
/// now forwards to this type and keeps its own public surface (constants, <c>Create</c>, <c>Verify</c>,
/// <c>ToStoredBytes</c>/<c>FromStoredBytes</c>) unchanged, so nothing about its existing behavior — or the
/// verifiers it has already written to disk on any install — changed by moving the code here.
/// </para>
/// <para>
/// <strong>Nothing here ever holds, stores, or compares a plaintext password beyond the single derivation it
/// was handed one for.</strong> <see cref="Create"/> returns an encoded verifier — algorithm, iteration count,
/// salt and derived key — and the plaintext is never part of it. <see cref="Verify"/> re-derives from the
/// candidate using the <em>stored</em> parameters and compares with
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>, so the
/// comparison's duration does not leak how many leading bytes were right.
/// </para>
/// <para>
/// <strong>Encoded form:</strong> <c>PBKDF2-SHA256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 key&gt;</c>.
/// The iteration count and salt travel <em>with</em> the verifier rather than being compiled in, so raising
/// <see cref="Iterations"/> later does not invalidate verifiers already stored: an existing one keeps being
/// checked at the count it was created with, and is only re-derived at the new count when its password is next
/// set. A per-call random salt (<see cref="SaltSizeBytes"/> bytes from <see cref="RandomNumberGenerator"/>)
/// means two accounts that chose the same password do not share a verifier, and a precomputed table is
/// worthless against either.
/// </para>
/// </remarks>
public static class PasswordHash
{
    /// <summary>The algorithm label that prefixes every encoded verifier.</summary>
    public const string AlgorithmLabel = "PBKDF2-SHA256";

    /// <summary>
    /// The PBKDF2 iteration count used for newly created verifiers: 600,000, which is OWASP's current
    /// Password Storage Cheat Sheet recommendation for PBKDF2-HMAC-SHA256 (600,000 for SHA-256; 210,000 is
    /// the SHA-512 figure).
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>Salt length in bytes (128 bits), drawn from a cryptographic RNG per verifier created.</summary>
    public const int SaltSizeBytes = 16;

    /// <summary>Derived key length in bytes (256 bits), matching the SHA-256 output size.</summary>
    public const int KeySizeBytes = 32;

    private const char FieldSeparator = '$';

    /// <summary>
    /// Derives a new verifier for <paramref name="password"/> with a freshly generated random salt at
    /// <see cref="Iterations"/> iterations.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="password"/> is null, empty, or whitespace.</exception>
    public static string Create(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            password: password,
            salt: salt,
            iterations: Iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: KeySizeBytes);

        try
        {
            return string.Join(
                FieldSeparator,
                AlgorithmLabel,
                Iterations.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(salt),
                Convert.ToBase64String(key));
        }
        finally
        {
            // The derived key is not the password, but it is the thing an attacker would need; there is no
            // reason to leave a copy of it lying in managed memory once it has been encoded.
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> derives to the key inside <paramref name="encoded"/>, using the
    /// salt and iteration count recorded in <paramref name="encoded"/> itself.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> — never throws, and never "succeeds by default" — for a null, empty,
    /// truncated, or otherwise unparseable verifier. A corrupted or tampered verifier must lock everyone out,
    /// not wave everyone through.
    /// </remarks>
    public static bool Verify(string? encoded, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        if (!TryDecode(encoded, out var iterations, out var salt, out var expectedKey))
        {
            return false;
        }

        var actualKey = Rfc2898DeriveBytes.Pbkdf2(
            password: candidate,
            salt: salt,
            iterations: iterations,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: expectedKey.Length);

        try
        {
            return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualKey);
            CryptographicOperations.ZeroMemory(expectedKey);
        }
    }

    /// <summary>Encodes <paramref name="encoded"/> as the UTF-8 bytes suitable for storage.</summary>
    public static byte[] ToStoredBytes(string encoded) => Encoding.UTF8.GetBytes(encoded);

    /// <summary>Decodes UTF-8 bytes read back from storage.</summary>
    public static string FromStoredBytes(ReadOnlySpan<byte> stored) => Encoding.UTF8.GetString(stored);

    private static bool TryDecode(string encoded, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];

        var parts = encoded.Split(FieldSeparator);
        if (parts.Length != 4 || !string.Equals(parts[0], AlgorithmLabel, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && key.Length > 0;
    }
}
