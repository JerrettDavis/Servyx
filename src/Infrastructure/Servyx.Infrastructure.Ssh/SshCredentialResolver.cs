using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Resolves the credentials an <see cref="SshConnector"/> needs from its
/// <see cref="ConnectorDescriptor.CredentialRefs"/>, by convention: a <see cref="SecretUrn"/> whose
/// <see cref="SecretUrn.Name"/> is <c>"username"</c>, <c>"password"</c>, <c>"private-key"</c>, or
/// <c>"passphrase"</c> (the last two optional; at least one of <c>"password"</c>/<c>"private-key"</c> is
/// required). Resolution happens here, as late as possible — see <c>docs/connectors.md</c>, "Secrets" — and
/// the caller owns disposing the returned <see cref="ResolvedSshCredentials"/>.
/// </summary>
internal static class SshCredentialResolver
{
    /// <summary>Resolves credentials for <paramref name="descriptor"/>, falling back to <paramref name="endpointUsernameHint"/> if no <c>"username"</c> credential is present.</summary>
    /// <exception cref="InvalidOperationException">No username could be determined, or neither a password nor a private key credential is present.</exception>
    public static async Task<ResolvedSshCredentials> ResolveAsync(
        ConnectorDescriptor descriptor,
        string? endpointUsernameHint,
        ISecretStore secretStore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(secretStore);

        string? username = endpointUsernameHint;
        SecretLease? password = null;
        SecretLease? privateKey = null;
        SecretLease? passphrase = null;

        try
        {
            foreach (var urnText in descriptor.CredentialRefs)
            {
                if (!SecretUrn.TryParse(urnText, out var urn))
                {
                    continue;
                }

                switch (urn.Name)
                {
                    case "username":
                        using (var lease = await secretStore.GetAsync(urn, ct).ConfigureAwait(false))
                        {
                            if (lease is not null)
                            {
                                username = lease.ToUtf8String();
                            }
                        }

                        break;

                    case "password":
                        password = await secretStore.GetAsync(urn, ct).ConfigureAwait(false);
                        break;

                    case "private-key":
                        privateKey = await secretStore.GetAsync(urn, ct).ConfigureAwait(false);
                        break;

                    case "passphrase":
                        passphrase = await secretStore.GetAsync(urn, ct).ConfigureAwait(false);
                        break;
                }
            }

            if (string.IsNullOrEmpty(username))
            {
                throw new InvalidOperationException(
                    "SSH connector has no username: specify one via the endpoint (e.g. 'user@host') or a 'username' credential.");
            }

            if (password is null && privateKey is null)
            {
                throw new InvalidOperationException(
                    "SSH connector has neither a 'password' nor a 'private-key' credential; at least one authentication method is required.");
            }

            var result = new ResolvedSshCredentials(username, password, privateKey, passphrase);
            password = null;
            privateKey = null;
            passphrase = null;
            return result;
        }
        finally
        {
            password?.Dispose();
            privateKey?.Dispose();
            passphrase?.Dispose();
        }
    }
}

/// <summary>
/// Credentials resolved for a single SSH connection attempt. Owns the <see cref="SecretLease"/> instances
/// it holds and must be disposed as soon as the connection has been established (or the attempt has
/// failed) — see the remarks on <see cref="SecretLease"/> for why leases should be held as briefly as
/// possible.
/// </summary>
internal sealed class ResolvedSshCredentials : IDisposable
{
    /// <summary>Creates a <see cref="ResolvedSshCredentials"/> taking ownership of the given leases.</summary>
    public ResolvedSshCredentials(string username, SecretLease? password, SecretLease? privateKey, SecretLease? passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        Username = username;
        Password = password;
        PrivateKey = privateKey;
        Passphrase = passphrase;
    }

    /// <summary>The username to authenticate as.</summary>
    public string Username { get; }

    /// <summary>The password, if a <c>"password"</c> credential was resolved.</summary>
    public SecretLease? Password { get; }

    /// <summary>The private key bytes, if a <c>"private-key"</c> credential was resolved.</summary>
    public SecretLease? PrivateKey { get; }

    /// <summary>The private key's passphrase, if a <c>"passphrase"</c> credential was resolved.</summary>
    public SecretLease? Passphrase { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Password?.Dispose();
        PrivateKey?.Dispose();
        Passphrase?.Dispose();
    }
}
