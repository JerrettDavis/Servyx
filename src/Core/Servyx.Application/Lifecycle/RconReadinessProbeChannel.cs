using Servyx.Domain.Lifecycle;
using Servyx.Domain.Rcon;

namespace Servyx.Application.Lifecycle;

/// <summary>
/// Adapts an <see cref="IRconChannelResolver"/> and a definition-declared control command into an
/// <see cref="IReadinessProbeChannel"/>, so a definition's <c>control-probe</c> readiness probe (e.g.
/// Palworld's <c>info</c> command) can drive a <see cref="ControlProbeReadiness"/> detector.
/// </summary>
/// <remarks>
/// <strong>This adapter never inspects or gates on write mode — and that omission is deliberate, not an
/// oversight.</strong> It does exactly one thing: resolve a session, then call
/// <see cref="Servyx.Domain.Rcon.IRconSession.InvokeAsync"/> with the declared command id. Whether that
/// call is permitted under the server's current <c>WriteMode</c> is entirely
/// <c>WriteGuardedRconSession</c>'s job (an infrastructure concern), which classifies each command by its
/// definition-declared <c>readOnly</c> flag. A readiness probe command (e.g. <c>info</c>) is declared
/// <c>readOnly: true</c>, so it is permitted even when the server's write mode is <c>ReadOnly</c> — this
/// type simply never adds a second gate on top of that one, which is what lets readiness checking keep
/// working on a read-only server.
/// </remarks>
public sealed class RconReadinessProbeChannel : IReadinessProbeChannel
{
    private readonly IRconChannelResolver _resolver;
    private readonly string _commandId;
    private readonly string? _serverName;

    /// <summary>Creates a probe channel that invokes <paramref name="commandId"/> against the resolved session.</summary>
    /// <param name="resolver">Resolves the RCON session for a server.</param>
    /// <param name="commandId">The definition-declared command id to invoke, e.g. <c>info</c>.</param>
    /// <param name="serverName">The server's container name, if known, passed through to <paramref name="resolver"/>.</param>
    public RconReadinessProbeChannel(IRconChannelResolver resolver, string commandId, string? serverName = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        _resolver = resolver;
        _commandId = commandId;
        _serverName = serverName;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A connection failure (unreachable channel, no channel configured, protocol error) is reported as
    /// <see cref="ProbeAttempt.ConnectionFailed"/> so <see cref="ControlProbeReadiness"/> treats it as
    /// "not ready yet" and keeps polling, matching every other reachability failure in this codebase.
    /// Cancellation is never swallowed here — it propagates so the caller's own timeout/cancellation
    /// handling (in <see cref="ControlProbeReadiness"/>) governs it.
    /// </remarks>
    public async Task<ProbeAttempt> ProbeAsync(string serverId, CancellationToken ct)
    {
        IRconSession? session;
        try
        {
            session = await _resolver.GetSessionAsync(serverId, _serverName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeAttempt.ConnectionFailed($"could not resolve an rcon session: {ex.Message}");
        }

        if (session is null)
        {
            return new ProbeAttempt.ConnectionFailed("no rcon control channel is configured for this server");
        }

        try
        {
            var response = await session.InvokeAsync(_commandId, null, ct).ConfigureAwait(false);
            return new ProbeAttempt.Responded(response.Text);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProbeAttempt.ConnectionFailed(ex.Message);
        }
    }
}
