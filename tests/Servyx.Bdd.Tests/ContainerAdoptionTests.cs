using Docker.DotNet;
using Docker.DotNet.Models;
using NSubstitute;
using Servyx.Infrastructure.Docker;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace Servyx.Bdd.Tests;

/// <summary>
/// Servyx adopts existing Docker containers into management rather than creating fresh ones. Adoption
/// must match on image repository (ignoring tag/digest) AND the required data mount — both loosely
/// enough to tolerate real-world tag/digest variance, and strictly enough that a look-alike image or a
/// missing mount is never silently adopted. Driven entirely through the public
/// <see cref="DockerServerDiscovery.DiscoverAsync(string, string, CancellationToken)"/> API against a
/// substituted <see cref="IDockerClient"/> — the same public surface production code uses, so these
/// scenarios cannot depend on any internal that isn't also exercised end to end.
/// </summary>
[Feature("Container adoption", "As an operator I trust that Servyx adopts exactly the containers that are genuinely mine, and nothing else")]
public class ContainerAdoptionTests(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private const string ExpectedRepo = "thijsvanloef/palworld-server-docker";
    private const string RequiredMount = "/palworld";

    private static ContainerListResponse ContainerWith(string image, string? mountDestination) => new()
    {
        ID = "container-under-test",
        Names = ["/container-under-test"],
        Image = image,
        Mounts = mountDestination is null ? [] : [new MountPoint { Destination = mountDestination, Source = "/host/path", RW = true }],
        Labels = new Dictionary<string, string>(),
    };

    private static ContainerInspectResponse InspectFor(ContainerListResponse container) => new()
    {
        ID = container.ID,
        Name = "/" + container.ID,
        State = new ContainerState { Status = "running" },
        HostConfig = new HostConfig(),
        NetworkSettings = new NetworkSettings(),
    };

    private static DockerServerDiscovery CreateDiscovery(ContainerListResponse candidate)
    {
        var containers = Substitute.For<IContainerOperations>();
        var client = Substitute.For<IDockerClient>();
        client.Containers.Returns(containers);
        containers.ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>([candidate]));
        containers.InspectContainerAsync(candidate.ID, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(InspectFor(candidate)));
        return new DockerServerDiscovery(client);
    }

    [Scenario("A container whose image differs only by tag is adopted", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task DifferentTag_SameRepository_IsAdopted()
        => await Given("a container running the expected repository under a different tag", () => CreateDiscovery(ContainerWith("thijsvanloef/palworld-server-docker:v1.2.3", RequiredMount)))
            .When("adoption runs", async Task<int> (discovery) => (await discovery.DiscoverAsync(ExpectedRepo, RequiredMount)).Count)
            .Then("it is adopted", count => Task.FromResult(count == 1))
            .AssertPassed();

    [Scenario("A digest-pinned reference to the same repository is adopted", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task DigestPinnedReference_IsAdopted()
        => await Given(
                "a container pinned by digest to the expected repository",
                () => CreateDiscovery(ContainerWith("thijsvanloef/palworld-server-docker@sha256:a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9", RequiredMount)))
            .When("adoption runs", async Task<int> (discovery) => (await discovery.DiscoverAsync(ExpectedRepo, RequiredMount)).Count)
            .Then("it is adopted", count => Task.FromResult(count == 1))
            .AssertPassed();

    [Scenario("An image whose repository merely contains the expected name as a substring is NOT adopted", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task RepositorySubstringLookAlike_IsNotAdopted()
        => await Given(
                "a container whose repository name embeds the expected name as a substring of a different image",
                () => CreateDiscovery(ContainerWith("thijsvanloef/palworld-server-docker-unofficial-fork:latest", RequiredMount)))
            .When("adoption runs", async Task<int> (discovery) => (await discovery.DiscoverAsync(ExpectedRepo, RequiredMount)).Count)
            .Then("it is NOT adopted, because repository matching is exact, not substring", count => Task.FromResult(count == 0))
            .AssertPassed();

    [Scenario("A matching image without the required mount is NOT adopted", "unit")]
    [Fact]
    [DisableOptimization]
    public async Task MatchingImage_WithoutRequiredMount_IsNotAdopted()
        => await Given("a container with the right image but no mount at the required path", () => CreateDiscovery(ContainerWith("thijsvanloef/palworld-server-docker:latest", "/some/other/path")))
            .When("adoption runs", async Task<int> (discovery) => (await discovery.DiscoverAsync(ExpectedRepo, RequiredMount)).Count)
            .Then("it is NOT adopted", count => Task.FromResult(count == 0))
            .AssertPassed();
}
