namespace Servyx.Domain.Connectors;

/// <summary>
/// The outcome of verifying a remote host's presented public key against this system's trust state.
/// </summary>
/// <remarks>
/// There is deliberately no member on this enum meaning "accept regardless" — no <c>AcceptAny</c>, no
/// <c>Bypass</c>, nothing an "insecure" flag could map to. See the remarks on <see cref="TrustPolicy"/> for
/// why that omission is the entire point.
/// </remarks>
public enum HostKeyVerdict
{
    /// <summary>The presented key matches a fingerprint this system already trusts.</summary>
    Trusted,

    /// <summary>
    /// No trust decision has been recorded for this host yet. The caller MUST refuse the connection unless
    /// and until a human explicitly pins the presented fingerprint via <see cref="IHostKeyStore.PinAsync"/>.
    /// </summary>
    Unknown,

    /// <summary>
    /// The host presented a fingerprint different from the one previously pinned. The caller MUST refuse
    /// the connection; there is no auto-heal path. A human must explicitly re-pin, an action that should be
    /// recorded with both the old and new fingerprints.
    /// </summary>
    Changed,

    /// <summary>
    /// This host (or its previously pinned key) has been explicitly revoked via
    /// <see cref="IHostKeyStore.RevokeAsync"/>. The caller MUST refuse the connection.
    /// </summary>
    Revoked,
}
