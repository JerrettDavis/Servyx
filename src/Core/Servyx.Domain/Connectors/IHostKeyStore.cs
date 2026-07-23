namespace Servyx.Domain.Connectors;

/// <summary>
/// Persists pinned host keys and revocations. Implementations live in <c>Servyx.Infrastructure</c>.
/// </summary>
public interface IHostKeyStore
{
    /// <summary>
    /// Finds the currently pinned key for <paramref name="host"/>:<paramref name="port"/>, or
    /// <see langword="null"/> if no key is pinned there — including when the host was previously pinned but
    /// has since been revoked via <see cref="RevokeAsync"/> (use <see cref="IsRevokedAsync"/> to distinguish
    /// "never pinned" from "revoked").
    /// </summary>
    Task<HostKeyRecord?> FindAsync(string host, int port, CancellationToken ct = default);

    /// <summary>
    /// Pins <paramref name="record"/> as the trusted key for its host and port, replacing any previously
    /// pinned key and clearing any prior revocation for that host and port. This is always an explicit,
    /// separately-initiated action performed by a human or an automation acting on a human's behalf — never
    /// something a verifier does on its own. Because pinning is an audit event, <paramref name="actor"/>
    /// identifies who (or what) performed it.
    /// </summary>
    Task PinAsync(HostKeyRecord record, string actor, CancellationToken ct = default);

    /// <summary>
    /// Marks <paramref name="host"/>:<paramref name="port"/> as revoked: no key for it will be considered
    /// trusted until an explicit re-pin. Revoking a host that was never pinned is permitted, to allow
    /// pre-emptively blocking a host known (from an external source) to be compromised. Because revocation
    /// is an audit event, <paramref name="actor"/> identifies who (or what) performed it.
    /// </summary>
    Task RevokeAsync(string host, int port, string actor, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="host"/>:<paramref name="port"/> is currently revoked. This is what lets
    /// <see cref="IHostKeyVerifier"/> distinguish <see cref="HostKeyVerdict.Revoked"/> from
    /// <see cref="HostKeyVerdict.Unknown"/> — a host that was pinned and then explicitly revoked must never
    /// be reported the same way as a host that was simply never seen.
    /// </summary>
    Task<bool> IsRevokedAsync(string host, int port, CancellationToken ct = default);
}
