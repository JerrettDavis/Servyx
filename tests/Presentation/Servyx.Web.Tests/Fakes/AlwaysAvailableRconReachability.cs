using Servyx.Domain.Rcon;
using Servyx.Infrastructure.Rcon;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// An <see cref="IRconReachability"/> that always reports itself available and hands back whatever
/// <paramref name="factory"/> builds, without opening a socket or an exec channel.
/// </summary>
/// <remarks>
/// For tests that want a chain-acquired <see cref="IRconSession"/> without exercising real reachability
/// (write-guard preservation, memoization, retry-after-failure) — see
/// <c>RconReachabilityChainWiringTests</c> for the tests that exercise the real strategies instead.
/// </remarks>
/// <param name="factory">Builds (or throws while building) the session <see cref="AcquireAsync"/> returns.</param>
public sealed class AlwaysAvailableRconReachability(Func<RconEndpoint, IRconSession> factory) : IRconReachability
{
    /// <inheritdoc />
    public string StrategyId => "test-direct";

    /// <inheritdoc />
    public string? LastUnavailableReason => null;

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(RconEndpoint endpoint, CancellationToken ct = default) => Task.FromResult(true);

    /// <inheritdoc />
    public Task<IRconSession> AcquireAsync(RconEndpoint endpoint, CancellationToken ct = default) =>
        Task.FromResult(factory(endpoint));
}
