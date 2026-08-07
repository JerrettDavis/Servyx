using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// A dashboard that fabricates numbers is worse than one that admits it doesn't know yet. These
/// scenarios prove <see cref="DockerCpuPercentCalculator"/> reports "unknown" rather than a bogus 0% (or
/// worse) for a first sample, and that <see cref="ServerQueryService"/> keeps a workload's own health
/// signal distinct from Servyx's own state classification rather than conflating them.
/// </summary>
[Feature("Observability correctness", "As an operator I trust every number and status Servyx shows me is either real or honestly absent")]
public class ObservabilityCorrectnessTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    [Scenario("A first metrics sample with no prior reading reports CPU as unknown, not fabricated", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task FirstSample_WithNoPriorReading_ReportsCpuAsUnknown()
        => await Given(
                "a first metrics sample with no prior reading (the all-zero seed snapshot)",
                () => (Previous: new CpuUsageSnapshot(0, 0, 4), Current: new CpuUsageSnapshot(2_000_000_000, 10_000_000_000, 4)))
            .When("the CPU percentage is computed", async Task<double?> (readings) => await Task.FromResult(DockerCpuPercentCalculator.Compute(readings.Current, readings.Previous)))
            .Then("it is reported as unknown (null) rather than a fabricated number", percent => Task.FromResult(percent is null))
            .AssertPassed();

    [Scenario("Two consecutive readings compute CPU percentage from their delta", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task TwoConsecutiveReadings_ComputeCpuPercentageFromTheirDelta()
        => await Given(
                "two consecutive readings a known interval apart",
                () => (Previous: new CpuUsageSnapshot(5_000_000_000, 100_000_000_000, 1), Current: new CpuUsageSnapshot(7_000_000_000, 110_000_000_000, 1)))
            .When("the CPU percentage is computed", async Task<double?> (readings) => await Task.FromResult(DockerCpuPercentCalculator.Compute(readings.Current, readings.Previous)))
            .Then("it is computed from their delta (2s of CPU time over 10s wall-clock = 20%)", percent => Task.FromResult(percent == 20.0))
            .AssertPassed();

    [Scenario("A container reporting healthy=false while running keeps state and health as distinct signals", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task UnhealthyRunningContainer_KeepsStateAndHealthDistinct()
        => await Given("a container reporting healthy=false while running", () =>
            {
                var discovery = Substitute.For<IServerDiscovery>();
                discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([new DiscoveredServer(
                        ServerId: "container-1",
                        Name: "palworld-server",
                        Image: "thijsvanloef/palworld-server-docker:latest",
                        ImageDigest: null,
                        State: "running",
                        HealthStatus: "unhealthy",
                        CreatedAt: DateTimeOffset.UtcNow,
                        StartedAt: DateTimeOffset.UtcNow,
                        Ports: [],
                        Mounts: [],
                        NetworkName: null,
                        ContainerIp: null,
                        MemoryLimitBytes: null,
                        CpuLimit: null,
                        RestartPolicy: null,
                        ComposeLabels: new Dictionary<string, string>(),
                        EnvironmentVariables: new Dictionary<string, string>())]));

                return new ServerQueryService(
                    discovery,
                    Substitute.For<IMetricsSource>(),
                    Substitute.For<ILogStream>(),
                    Substitute.For<ITransport>(),
                    new AdoptionCriteria("palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld"),
                    NullLogger<ServerQueryService>.Instance);
            })
            .When("the server list is resolved", async Task<ServerSummary> (queryService) => (await queryService.GetAdoptedServersAsync()).Single())
            .Then("state is reported as Running", summary => Task.FromResult(summary.State == Servyx.Domain.Lifecycle.ServerState.Running))
            .And("health is reported as Unhealthy — a distinct signal, not conflated with state", summary => Task.FromResult(summary.Health == ServerHealthStatus.Unhealthy))
            .AssertPassed();
}
