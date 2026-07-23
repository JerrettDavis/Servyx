using System.Security.Cryptography;

namespace Servyx.Domain.Connectors;

/// <summary>
/// Computes host key fingerprints in OpenSSH's display format, so a user can eyeball-compare a value shown
/// by Servyx against the output of <c>ssh-keyscan</c> or <c>ssh-keygen -lf</c>.
/// </summary>
public static class HostKeyFingerprint
{
    /// <summary>
    /// Computes the <c>SHA256:</c> fingerprint of <paramref name="publicKeyBlob"/>: the literal prefix
    /// <c>SHA256:</c> followed by the standard-alphabet base64 encoding of the SHA-256 hash of the raw key
    /// blob, with trailing <c>=</c> padding removed — exactly the format OpenSSH prints, e.g.
    /// <c>SHA256:0FSHZRVpj1KN4haa0Dnpy1LjZuMt9o+nYk2GpXbF5oo</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="publicKeyBlob"/> is null.</exception>
    public static string ComputeSha256(byte[] publicKeyBlob)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);

        var hash = SHA256.HashData(publicKeyBlob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}
