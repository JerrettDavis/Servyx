namespace Servyx.Domain.Secrets;

/// <summary>
/// Resolves, stores, and deletes secret values addressed by <see cref="SecretUrn"/>. Descriptors and other
/// domain models hold only the URN; resolution to an actual value happens through this interface, inside
/// whichever connector implementation needs it, at the moment it needs it — never earlier, and never cached
/// as plaintext.
/// </summary>
public interface ISecretStore
{
    /// <summary>Whether a secret is currently stored at <paramref name="urn"/>.</summary>
    Task<bool> ExistsAsync(SecretUrn urn, CancellationToken ct = default);

    /// <summary>
    /// Resolves the secret at <paramref name="urn"/>, or <see langword="null"/> if none is stored there.
    /// The caller owns the returned <see cref="SecretLease"/> and must dispose it as soon as it is done
    /// with the value.
    /// </summary>
    Task<SecretLease?> GetAsync(SecretUrn urn, CancellationToken ct = default);

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="urn"/>, overwriting any existing value. Because a
    /// secret write is an audit event, <paramref name="actor"/> identifies who (or what) performed it.
    /// </summary>
    Task SetAsync(SecretUrn urn, ReadOnlyMemory<byte> value, string actor, CancellationToken ct = default);

    /// <summary>
    /// Deletes the secret at <paramref name="urn"/>, if any. Because a secret deletion is an audit event,
    /// <paramref name="actor"/> identifies who (or what) performed it.
    /// </summary>
    Task DeleteAsync(SecretUrn urn, string actor, CancellationToken ct = default);

    /// <summary>
    /// Lists every <see cref="SecretUrn"/> currently stored under the given <paramref name="scope"/> and
    /// <paramref name="scopeId"/>, across all categories and names. Does not resolve any values.
    /// </summary>
    Task<IReadOnlyList<SecretUrn>> ListAsync(string scope, string scopeId, CancellationToken ct = default);
}
