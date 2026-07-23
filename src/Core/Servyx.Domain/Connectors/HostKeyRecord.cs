namespace Servyx.Domain.Connectors;

/// <summary>
/// A pinned record of a remote host's public key, as trusted by this system.
/// </summary>
/// <param name="Host">The hostname or IP address the key was presented for.</param>
/// <param name="Port">The port the key was presented on.</param>
/// <param name="Algorithm">The key algorithm, e.g. <c>"ssh-ed25519"</c> or <c>"rsa-sha2-256"</c>.</param>
/// <param name="Sha256Fingerprint">
/// The key's fingerprint in OpenSSH's display form: <c>SHA256:</c> followed by the unpadded standard-alphabet
/// base64 encoding of the SHA-256 hash of <paramref name="PublicKeyBlob"/>. See
/// <see cref="HostKeyFingerprint.ComputeSha256"/>.
/// </param>
/// <param name="PublicKeyBlob">The raw public key blob, in the wire format the transport presented it in.</param>
/// <param name="PinnedAt">When this key was pinned.</param>
/// <param name="PinnedByActor">Who (or what) pinned this key — pinning is an audit event.</param>
public sealed record HostKeyRecord(
    string Host,
    int Port,
    string Algorithm,
    string Sha256Fingerprint,
    byte[] PublicKeyBlob,
    DateTimeOffset PinnedAt,
    string PinnedByActor);
