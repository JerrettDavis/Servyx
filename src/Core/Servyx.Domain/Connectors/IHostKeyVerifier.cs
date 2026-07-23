namespace Servyx.Domain.Connectors;

/// <summary>
/// Verifies a remote host's presented public key against this system's trust state, under a given
/// <see cref="TrustPolicy"/>. See the remarks on <see cref="TrustPolicy"/> for why there is no way to
/// configure this to skip verification.
/// </summary>
public interface IHostKeyVerifier
{
    /// <summary>
    /// Verifies the key <paramref name="publicKeyBlob"/> (algorithm <paramref name="algorithm"/>) presented
    /// by <paramref name="host"/>:<paramref name="port"/> under <paramref name="policy"/>.
    /// </summary>
    Task<HostKeyVerdict> VerifyAsync(
        string host,
        int port,
        string algorithm,
        byte[] publicKeyBlob,
        TrustPolicy policy,
        CancellationToken ct = default);
}
