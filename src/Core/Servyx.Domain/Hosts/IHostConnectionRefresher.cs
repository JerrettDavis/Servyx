namespace Servyx.Domain.Hosts;

/// <summary>
/// The seam a host-registration surface calls after adding or removing a <see cref="Entities.Host"/> row, so a
/// freshly-registered host becomes discoverable — and a deregistered one stops being discovered — without a
/// process restart.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives in <c>Servyx.Domain</c>.</strong> The implementation is
/// <c>Servyx.Infrastructure.Ssh.Docker.HostConnectionRegistry</c>, which caches the combined
/// configured-plus-database host set; every infrastructure project references <c>Servyx.Domain</c> and nothing
/// else, so an abstraction infrastructure must implement has to be declared here — the same reasoning
/// <see cref="IHostRepository"/> documents for itself.
/// </para>
/// <para>
/// <strong>Deliberately narrower than the registry it fronts.</strong> This interface exposes only
/// invalidation. A use case that has just written a host row has no business reading, connecting to, or
/// otherwise driving the connection set — it only needs to say "what you have cached is now stale".
/// </para>
/// </remarks>
public interface IHostConnectionRefresher
{
    /// <summary>
    /// Drops any cached view of the registered-host set, so the next consumer re-reads
    /// <see cref="IHostRepository"/>. Must be safe to call from any thread and must never throw.
    /// </summary>
    void Invalidate();
}
