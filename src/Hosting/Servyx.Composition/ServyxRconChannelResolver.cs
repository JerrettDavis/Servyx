using Servyx.Application.Lifecycle;
using Servyx.Domain.Rcon;

namespace Servyx.Composition;

/// <summary>
/// Thin <see cref="IRconChannelResolver"/> adapter over <see cref="ServyxRconChannels"/>.
/// </summary>
/// <remarks>
/// <see cref="ServyxRconChannels"/> is a composition-root type — it wires host configuration, the secret
/// store, and the definition's reachability strategies together. <c>Servyx.Application</c> (where
/// <see cref="ServerLifecycleService"/> lives) never depends on the presentation layer, so it depends on
/// <see cref="IRconChannelResolver"/> instead; this is the one-line adapter <see cref="IRconChannelResolver"/>'s
/// own remarks call for.
/// </remarks>
public sealed class ServyxRconChannelResolver : IRconChannelResolver
{
    private readonly ServyxRconChannels _channels;

    /// <summary>Creates a resolver delegating to <paramref name="channels"/>.</summary>
    public ServyxRconChannelResolver(ServyxRconChannels channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    /// <inheritdoc />
    public Task<IRconSession?> GetSessionAsync(string? serverId, string? serverName = null, CancellationToken ct = default) =>
        _channels.GetSessionAsync(serverId, serverName, ct);
}
