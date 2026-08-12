using Servyx.Domain.Secrets;

namespace Servyx.Domain.Hosts;

/// <summary>
/// Where a host's SSH credential ended up after <see cref="IHostCredentialImporter.ImportPrivateKeyAsync"/>,
/// so a caller can persist <see cref="Servyx.Domain.Entities.Host.CredentialUrn"/> without re-deriving the
/// naming convention itself.
/// </summary>
/// <param name="PrivateKeyUrn">Where the private key bytes were written.</param>
/// <param name="PassphraseUrn">Where the passphrase was written, or <see langword="null"/> if none was supplied.</param>
public sealed record HostCredentialImportResult(SecretUrn PrivateKeyUrn, SecretUrn? PassphraseUrn);

/// <summary>
/// The write-only path for putting a remote host's SSH private key into secret storage as part of registering
/// that host.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this abstraction exists at all.</strong> The concrete store
/// (<c>Servyx.Web.Hosts.SshHostCredentialStore</c>) lives in the Presentation project, because the
/// <see cref="ISecretStore"/> it is built on is only registered by the Web host's own composition
/// (<c>AddServyxOperatorAuthentication</c> → <c>AddServyxSecrets</c>). The host-registration use case that
/// needs it lives in <c>Servyx.Application</c>, which must never reference Presentation — the dependency graph
/// runs Web → Application → Domain and never back. Declaring the seam here, in the one project every layer
/// already references, is what lets the use case depend on the capability while the outer composition root
/// (<c>Servyx.Web/Program.cs</c>) supplies the implementation. This is the same reasoning
/// <see cref="IHostRepository"/> documents for itself.
/// </para>
/// <para>
/// <strong>Write-only on purpose.</strong> There is deliberately no read-back or delete method here. Resolving
/// a credential for actual use is the transport's job at connection time, and a surface that cannot read
/// cannot become a way to exfiltrate a key that is already stored. The absence of a delete is equally
/// deliberate: see <c>IHostRegistrationService.DeregisterAsync</c> for why deregistration does not scrub the
/// secret store.
/// </para>
/// </remarks>
public interface IHostCredentialImporter
{
    /// <summary>
    /// Writes <paramref name="privateKeyBytes"/> — and, if supplied, <paramref name="passphrase"/> — into
    /// secret storage for the host identified by <paramref name="hostKey"/>, attributed to
    /// <paramref name="actor"/> because a secret write is an audit event.
    /// </summary>
    /// <param name="hostKey">The host's registered name; becomes the URN's scope-id segment.</param>
    /// <param name="privateKeyBytes">The private key's exact bytes, stored byte-for-byte.</param>
    /// <param name="passphrase">The key's passphrase, if it has one. Null or empty means "no passphrase" and writes nothing for it.</param>
    /// <param name="actor">The authenticated operator's identity, recorded against the write.</param>
    /// <param name="ct">Cancels the import.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="hostKey"/> is not a valid secret URN segment, <paramref name="privateKeyBytes"/> is
    /// empty, or <paramref name="actor"/> is null, empty, or whitespace.
    /// </exception>
    Task<HostCredentialImportResult> ImportPrivateKeyAsync(
        string hostKey,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct = default);
}
