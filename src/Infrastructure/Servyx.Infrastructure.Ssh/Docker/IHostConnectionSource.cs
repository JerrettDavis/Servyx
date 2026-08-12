using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// One host <see cref="CompositeServerDiscovery"/> can fan a discovery query out against: the name results
/// discovered on it are tagged with (<see cref="Servyx.Domain.Discovery.DiscoveredServer.HostKey"/>), plus an
/// already-connectable (but not necessarily yet connected — see <see cref="LazyConnectingExecutionTarget"/>)
/// session to run that query over.
/// </summary>
public sealed record HostConnection(string HostKey, IExecutionTarget ExecutionTarget);

/// <summary>
/// The live set of hosts <see cref="CompositeServerDiscovery"/> should fan a discovery query out against right
/// now. Kept as a seam — rather than <see cref="CompositeServerDiscovery"/> depending on
/// <see cref="HostConnectionRegistry"/> directly — purely so <see cref="CompositeServerDiscovery"/>'s own unit
/// tests can exercise its fan-out/partial-failure behaviour against a trivial fake, without also standing up a
/// registry, a host repository, and a transport.
/// </summary>
public interface IHostConnectionSource
{
    /// <summary>Every host currently reachable for discovery.</summary>
    Task<IReadOnlyList<HostConnection>> GetConnectionsAsync(CancellationToken ct = default);
}
