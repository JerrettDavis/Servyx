namespace Servyx.Domain.Transport;

/// <summary>
/// Routes a server id to the specific <see cref="IExecutionTarget"/> it should be read from or acted on
/// through: the local Docker daemon's session for a server discovered there, or a specific
/// registered/configured ssh+docker host's own session for a server discovered on one. This is the seam
/// later per-server surfaces — console log streaming, metrics, RCON, backups — resolve against, instead of
/// each assuming the single process-wide <see cref="ITransport"/>/<see cref="IExecutionTarget"/> the
/// composition root happens to have wired, which today is either "the local Docker daemon" or "the one
/// statically-declared ssh+docker host" for every server at once, never a per-server choice between the two.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by the same raw host identity <c>DiscoveredServer.HostKey</c> already carries</strong> (see
/// <c>ServerSummary.HostKey</c>) — <see langword="null"/> for a server discovered on the local Docker
/// daemon, or a registered/configured host's own name otherwise. A caller is expected to already have this
/// value in hand — it flows through <c>ServerSummary</c>/<c>ServerDetail</c> straight from discovery — so
/// this contract never looks a server up by id itself. Doing so would require a dependency on the
/// application-layer server-lookup surface (<c>IServerQueryService</c>), and that surface is exactly the
/// kind of consumer this resolver exists to serve, so depending on it here would create a reference cycle.
/// </para>
/// <para>
/// <strong><c>hostKey</c> decides the branch; <c>serverId</c> only matters for the local one.</strong> A
/// registered/configured host's <see cref="IExecutionTarget"/> (see
/// <c>Servyx.Infrastructure.Ssh.Docker.IHostConnectionSource</c>) is already a host-wide session capable of
/// addressing any container on that host by id in each command it runs — <c>serverId</c> plays no part in
/// selecting it. The local Docker daemon's <see cref="IExecutionTarget"/>, in contrast, is scoped to a
/// single container at connect time, so <c>serverId</c> is exactly what selects which one.
/// </para>
/// </remarks>
public interface IServerExecutionTargetResolver
{
    /// <summary>
    /// Resolves the <see cref="IExecutionTarget"/> <paramref name="serverId"/> should be read from or acted
    /// on through.
    /// </summary>
    /// <param name="serverId">
    /// The server's id — the same value <c>ServerSummary.Id</c> carries. Used only for the local (null
    /// <paramref name="hostKey"/>) branch, to scope the returned session to the right container.
    /// </param>
    /// <param name="hostKey">
    /// The server's <c>ServerSummary.HostKey</c> — <see langword="null"/> for a server discovered on the
    /// local Docker daemon, or a registered/configured host's own name otherwise.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="hostKey"/> names a host that is not currently connectable (never registered, or
    /// registered then removed/disabled), or <paramref name="hostKey"/> is <see langword="null"/> but this
    /// process has no local execution surface to fall back to. Never falls back silently to a different host
    /// than the one named — misrouting an unrecognised host key to some other host's session would run
    /// whatever the caller does next (log reads, metrics, RCON, backups) against the wrong machine.
    /// </exception>
    Task<IExecutionTarget> ResolveAsync(string serverId, string? hostKey, CancellationToken ct = default);
}
