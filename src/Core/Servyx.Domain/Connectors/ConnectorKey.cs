using System.Security.Cryptography;
using System.Text;

namespace Servyx.Domain.Connectors;

/// <summary>
/// The key <see cref="IConnectorPool"/> uses to identify a pooled connection. Two <see cref="ConnectorKey"/>
/// values that compare equal share a pooled connection; values that differ get separate ones.
/// </summary>
/// <param name="Kind">The connector kind, e.g. <c>"ssh"</c>.</param>
/// <param name="EndpointKey">A stable string identifying the endpoint, e.g. <c>"10.0.0.4:22"</c>.</param>
/// <param name="CredentialKey">
/// A hash of the resolved credential URN(s) — see <see cref="ComputeCredentialKey"/> — never the secret
/// value itself. Rotating a credential naturally produces a new <see cref="ConnectorKey"/> and therefore a
/// new pool entry, rather than silently reusing a session authenticated under the credential that just got
/// rotated out.
/// </param>
/// <param name="TrustKey">
/// A string identifying the trust posture in effect (e.g. a serialized <see cref="TrustPolicy"/> case name
/// plus, for <see cref="TrustPolicy.PinnedFingerprints"/>, a hash of the pinned set), so that a change in
/// trust posture is also reflected as a distinct pool entry.
/// </param>
public sealed record ConnectorKey(
    string Kind,
    string EndpointKey,
    string CredentialKey,
    string TrustKey)
{
    /// <summary>
    /// Computes a stable, non-reversible key from one or more credential URNs, suitable for
    /// <see cref="CredentialKey"/>. Deliberately a one-way hash: the pool key must be computable without
    /// holding a live <see cref="Secrets.SecretLease"/> open just to key a dictionary, and it must never let
    /// a secret's resolved value (or, ideally, even its URN) leak into a log line via the key itself.
    /// </summary>
    /// <param name="credentialUrns">The credential URNs (in a stable, caller-chosen order) this connector resolves from.</param>
    public static string ComputeCredentialKey(IEnumerable<string> credentialUrns)
    {
        ArgumentNullException.ThrowIfNull(credentialUrns);

        var joined = string.Join('\n', credentialUrns);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash);
    }
}
