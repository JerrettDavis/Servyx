using Servyx.Domain.Hosts;
using Servyx.Domain.Secrets;

namespace Servyx.Web.Hosts;

/// <summary>
/// Result of a successful <see cref="SshHostCredentialStore.ImportPrivateKeyAsync"/> call: the URNs the
/// credential now lives at, so a caller (a future host-registration flow) can persist
/// <c>ConnectorDescriptor.CredentialRefs</c> entries without re-deriving the naming convention itself.
/// </summary>
/// <param name="PrivateKeyUrn">Where the private key bytes were written.</param>
/// <param name="PassphraseUrn">
/// Where the passphrase was written, or <see langword="null"/> if none was supplied and nothing was written
/// for it.
/// </param>
public sealed record SshHostCredentialImportResult(SecretUrn PrivateKeyUrn, SecretUrn? PassphraseUrn);

/// <summary>
/// The runtime (not startup-only) write path for putting a remote host's SSH private key into
/// <see cref="ISecretStore"/>, for a UI form that submits a key directly rather than requiring an operator to
/// edit configuration and restart (compare <c>SecretImport</c>, the existing config-driven,
/// <c>Servyx:Secrets:Import</c>-based path this store is a runtime sibling to, not a replacement for). It
/// owns no storage of its own: everything goes through the same <see cref="ISecretStore"/> abstraction, at
/// the same <c>secret://connector/{hostKey}/ssh/{name}</c> URNs, that <c>SshCredentialResolver</c> already
/// knows how to read back — see <c>docs/user-guide/adopting-a-remote-host.md</c>, "Getting the key into the
/// secret store".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only ever writes, never reads back.</strong> There is no method here that returns the private key
/// or passphrase after storage — resolving a credential for actual use is
/// <c>SshCredentialResolver</c>'s job, at connection time, not this store's. That asymmetry is deliberate:
/// a write-only surface cannot itself become a way to exfiltrate a key that is already in the store.
/// </para>
/// <para>
/// <strong>Never logs key material.</strong> This type takes no <c>ILogger</c> dependency at all — the same
/// choice <c>OperatorCredentialStore</c> makes — so there is no code path inside it that could log a secret
/// byte, a passphrase character, or either one folded into an exception message. The only strings this type
/// ever puts in an exception are a host key label and a parameter name, neither of which is secret-derived.
/// </para>
/// <para>
/// <strong>Overwrites, unlike the startup import.</strong> <c>SecretImport</c> deliberately never overwrites
/// an existing secret, because it is meant to be idempotent across restarts of unattended configuration. This
/// store is the opposite kind of write path — an explicit, one-off action an authenticated operator took
/// through the UI right now — so a re-submission (rotating a key, fixing a passphrase typo) is expected to
/// replace what was there, exactly like <see cref="ISecretStore.SetAsync"/> already does by contract. No
/// first-run lock is needed for the same reason <c>OperatorCredentialStore</c> needs one and this does not:
/// there is no "only once" invariant to protect here.
/// </para>
/// <para>
/// <strong>Implements <see cref="IHostCredentialImporter"/>, explicitly.</strong> That interface is declared in
/// <c>Servyx.Domain</c> so <c>Servyx.Application</c>'s host-registration use case can depend on this capability
/// without depending on the Presentation project this type is forced to live in (it needs the
/// <see cref="ISecretStore"/> only the Web host registers — see <c>Program.cs</c>). The implementation is
/// explicit rather than implicit so this type's own richer surface (the static URN helpers, the
/// <see cref="SshHostCredentialImportResult"/> return type) stays exactly as it was for the callers and tests
/// that already use it, with the interface adding a second, narrower way in rather than reshaping the first.
/// </para>
/// </remarks>
public sealed class SshHostCredentialStore : IHostCredentialImporter
{
    /// <summary>The category segment every URN this store writes shares: <c>secret://connector/{hostKey}/ssh/...</c>.</summary>
    private const string Category = "ssh";

    private const string PrivateKeyName = "private-key";
    private const string PassphraseName = "passphrase";

    private readonly ISecretStore _secrets;

    /// <summary>Creates a store over <paramref name="secrets"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="secrets"/> is null.</exception>
    public SshHostCredentialStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
    }

    /// <summary>
    /// Where a host's SSH private key lives: <c>secret://connector/{hostKey}/ssh/private-key</c>. Exposed so
    /// a caller can populate a host's <c>CredentialUrn</c> field with exactly the value this store writes to,
    /// without restating the naming convention.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hostKey"/> is not a valid secret URN segment.</exception>
    public static SecretUrn PrivateKeyUrn(string hostKey) => SecretUrn.Create("connector", hostKey, Category, PrivateKeyName);

    /// <summary>
    /// Where a host's private-key passphrase lives, if it has one: <c>secret://connector/{hostKey}/ssh/passphrase</c>
    /// — the sibling URN <c>SshCredentialResolver</c> already looks for by convention (any credential ref
    /// whose <see cref="SecretUrn.Name"/> is <c>"passphrase"</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hostKey"/> is not a valid secret URN segment.</exception>
    public static SecretUrn PassphraseUrn(string hostKey) => SecretUrn.Create("connector", hostKey, Category, PassphraseName);

    /// <summary>
    /// Writes <paramref name="privateKeyBytes"/> — and, if supplied, <paramref name="passphrase"/> — into the
    /// secret store for the remote host identified by <paramref name="hostKey"/>. Because a secret write is
    /// an audit event, <paramref name="actor"/> identifies the authenticated operator who submitted it (never
    /// a hardcoded constant — this is a runtime, operator-attributed write, unlike <c>SecretImport</c>'s
    /// fixed <c>"servyx.web/startup-import"</c> actor).
    /// </summary>
    /// <param name="hostKey">
    /// The host's configured name/label (the <c>&lt;name&gt;</c> in <c>Servyx:Hosts:&lt;name&gt;</c>) — becomes
    /// the URN's <c>scopeId</c> segment.
    /// </param>
    /// <param name="privateKeyBytes">
    /// The private key's exact bytes. Copied byte-for-byte into the store, the same whitespace- and
    /// newline-preserving discipline <c>SecretImport</c> uses, since a private key is sensitive to both.
    /// </param>
    /// <param name="passphrase">
    /// The private key's passphrase, if it has one. Encoded as UTF-8 and stored at
    /// <see cref="PassphraseUrn"/> — the same encoding <c>SshCredentialResolver</c> decodes it back out of
    /// with <c>SecretLease.ToUtf8String()</c>. Null or empty means "no passphrase": nothing is written for it,
    /// and any previously stored passphrase for this host is left untouched (a caller that wants to clear a
    /// passphrase must do so explicitly via <see cref="ISecretStore.DeleteAsync"/>, not by omitting it here).
    /// </param>
    /// <param name="actor">The authenticated operator's identity, recorded against both writes.</param>
    /// <param name="ct">Cancels the import between the private-key write and the passphrase write.</param>
    /// <returns>The URNs written to, for a caller that needs to persist them.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="hostKey"/> is not a valid secret URN segment, <paramref name="privateKeyBytes"/> is
    /// empty, or <paramref name="actor"/> is null, empty, or whitespace.
    /// </exception>
    public async Task<SshHostCredentialImportResult> ImportPrivateKeyAsync(
        string hostKey,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        if (privateKeyBytes.Length == 0)
        {
            throw new ArgumentException(
                "The private key must not be empty. An empty value is always an error for a secret.",
                nameof(privateKeyBytes));
        }

        // SecretUrn.Create validates hostKey (non-empty, safe character set, no path-traversal shape) and
        // throws ArgumentException naming only the offending hostKey string — never key material — if it
        // isn't one of those.
        var privateKeyUrn = PrivateKeyUrn(hostKey);

        await _secrets.SetAsync(privateKeyUrn, privateKeyBytes, actor, ct).ConfigureAwait(false);

        SecretUrn? passphraseUrn = null;
        if (!string.IsNullOrEmpty(passphrase))
        {
            passphraseUrn = PassphraseUrn(hostKey);
            var passphraseBytes = System.Text.Encoding.UTF8.GetBytes(passphrase);
            await _secrets.SetAsync(passphraseUrn.Value, passphraseBytes, actor, ct).ConfigureAwait(false);
        }

        return new SshHostCredentialImportResult(privateKeyUrn, passphraseUrn);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates verbatim to <see cref="ImportPrivateKeyAsync"/> and restates its result in the Domain-declared
    /// shape. There is no second write path here, and no behaviour that differs by which surface a caller came
    /// in through.
    /// </remarks>
    async Task<HostCredentialImportResult> IHostCredentialImporter.ImportPrivateKeyAsync(
        string hostKey,
        ReadOnlyMemory<byte> privateKeyBytes,
        string? passphrase,
        string actor,
        CancellationToken ct)
    {
        var result = await ImportPrivateKeyAsync(hostKey, privateKeyBytes, passphrase, actor, ct).ConfigureAwait(false);
        return new HostCredentialImportResult(result.PrivateKeyUrn, result.PassphraseUrn);
    }
}
