using Servyx.Domain.Connectors;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Derives a <see cref="ConnectorKey"/> from an SSH <see cref="ConnectorDescriptor"/>, so that
/// <see cref="IConnectorPool"/> callers get a stable, deterministic pool key without hand-rolling one at
/// every call site.
/// </summary>
public static class SshConnectorKeyFactory
{
    /// <summary>
    /// Creates a <see cref="ConnectorKey"/> for <paramref name="descriptor"/>. Two descriptors that agree
    /// on kind, endpoint, credential URNs (in the same order — see
    /// <see cref="ConnectorKey.ComputeCredentialKey"/>), and trust posture produce equal keys; changing any
    /// of those — in particular rotating a credential URN — produces a different one.
    /// </summary>
    public static ConnectorKey CreateKey(ConnectorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var (endpoint, _) = SshEndpoint.Parse(descriptor.Endpoint);
        var credentialKey = ConnectorKey.ComputeCredentialKey(descriptor.CredentialRefs);
        var trustKey = ComputeTrustKey(descriptor.Trust);

        return new ConnectorKey(descriptor.Kind, endpoint.ToString(), credentialKey, trustKey);
    }

    private static string ComputeTrustKey(TrustPolicy policy) => policy switch
    {
        TrustPolicy.RequirePinned => "require-pinned",
        TrustPolicy.TrustOnFirstUse => "trust-on-first-use",
        TrustPolicy.PinnedFingerprints pinned => "pinned-fingerprints:" + ConnectorKey.ComputeCredentialKey(pinned.Sha256.OrderBy(s => s, StringComparer.Ordinal)),
        _ => throw new NotSupportedException($"Unrecognized trust policy type '{policy.GetType()}'."),
    };
}
