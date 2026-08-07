using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// Docker being unreachable is a normal, expected condition for a self-hosted panel — not a bug. These
/// scenarios drive <see cref="ServerQueryService"/> with substitutes that fail exactly as a downed daemon
/// would, and prove it degrades to an honest state rather than throwing, while still letting a caller's
/// own cancellation propagate rather than being mistaken for a transport failure.
/// </summary>
[Feature("Graceful degradation", "As an operator I see an honest \"unreachable\" state when Docker is down, never a crash")]
public class GracefulDegradationTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private static readonly TargetDescriptor Target = new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static ServerQueryService CreateUnreachableQueryService()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));

        var transport = Substitute.For<ITransport>();
        transport.ProbeAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TargetHealth(false, null, "Docker engine unreachable: no such pipe")));

        return new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            transport,
            new AdoptionCriteria("palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld"),
            NullLogger<ServerQueryService>.Instance);
    }

    [Scenario("The server list degrades to empty rather than throwing when the daemon is unreachable", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task ServerList_ReturnsEmpty_WhenDaemonUnreachable_InsteadOfThrowing()
        => await Given("the Docker daemon is unreachable", () => CreateUnreachableQueryService())
            .When("the server list is requested", async Task<IReadOnlyList<ServerSummary>> (queryService) => await queryService.GetAdoptedServersAsync())
            .Then("an empty list is returned rather than an exception", servers => Task.FromResult(servers.Count == 0))
            .AssertPassed();

    [Scenario("The connection state names the endpoint that was tried and reports it unreachable", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task ConnectionState_ReportsUnreachable_AndNamesTheEndpoint()
        => await Given("the Docker daemon is unreachable", () => CreateUnreachableQueryService())
            .When("the connection state is requested", async Task<Servyx.Application.Servers.DockerConnectionState> (queryService) => await queryService.GetConnectionStateAsync(Target))
            .Then("the connection state reports unreachable", state => Task.FromResult(!state.Reachable))
            .And("it names the endpoint that was tried", state => Task.FromResult(state.Endpoint == Target.Endpoint))
            .AssertPassed();

    [Scenario("A cancelled operation propagates cancellation rather than being reported as a transport failure", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task CancelledOperation_PropagatesCancellation_RatherThanBeingReportedAsATransportFailure()
        => await Given("a query service whose discovery is cancelled mid-flight", () =>
            {
                var discovery = Substitute.For<IServerDiscovery>();
                discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new OperationCanceledException());

                return new ServerQueryService(
                    discovery,
                    Substitute.For<IMetricsSource>(),
                    Substitute.For<ILogStream>(),
                    Substitute.For<ITransport>(),
                    new AdoptionCriteria("palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld"),
                    NullLogger<ServerQueryService>.Instance);
            })
            .When("an operation is cancelled", async Task<Exception?> (queryService) =>
            {
                try
                {
                    await queryService.GetAdoptedServersAsync();
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            })
            .Then(
                "cancellation propagates as OperationCanceledException rather than being swallowed into an empty, honest-looking list",
                ex => Task.FromResult(ex is OperationCanceledException))
            .AssertPassed();
}
