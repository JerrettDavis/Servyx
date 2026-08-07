using Servyx.Domain.Rcon;

namespace Servyx.Application.Lifecycle;

/// <summary>
/// Resolves the write-guarded RCON control session for a server, by server id and (optionally) server
/// name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists instead of <c>ServyxRconChannels</c> being injected directly.</strong>
/// <c>ServyxRconChannels</c> is a composition-root type (<c>Servyx.Web.Services</c>) — it wires together
/// host configuration, the secret store, and the definition's declared reachability strategies.
/// <c>Servyx.Application</c> only ever depends on <c>Servyx.Domain</c>, never on the presentation layer,
/// so <see cref="ServerLifecycleService"/> depends on this small port instead. Its method signature is
/// deliberately identical to <c>ServyxRconChannels.GetSessionAsync</c> — <c>(string? serverId, string?
/// serverName, CancellationToken)</c> returning <c>Task&lt;IRconSession?&gt;</c> — so wiring it up in
/// <c>Program.cs</c> is a one-line adapter (or making <c>ServyxRconChannels</c> implement this interface
/// directly), left to the composition-root worker that wires <see cref="ServerLifecycleService"/> into DI.
/// </para>
/// <para>
/// Returns <see langword="null"/> when the operator configured no RCON channel for the server, exactly as
/// <c>ServyxRconChannels.GetSessionAsync</c> does — callers must treat that as "no control channel
/// available", not as an error.
/// </para>
/// </remarks>
public interface IRconChannelResolver
{
    /// <summary>
    /// Returns the write-guarded control session for a server, or <see langword="null"/> when no RCON
    /// channel is configured for it.
    /// </summary>
    /// <param name="serverId">The server's discovery id.</param>
    /// <param name="serverName">The server's container name, if known.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IRconSession?> GetSessionAsync(string? serverId, string? serverName = null, CancellationToken ct = default);
}
