using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;

namespace Servyx.Application.Tests.Servers;

/// <summary>
/// Tests for <see cref="ServerBindingResolver"/>: the discovery fan-out across several loaded definitions'
/// criteria, and the most-specific-wins-then-explicit-ambiguity conflict rule.
/// </summary>
public class ServerBindingResolverTests
{
    private static readonly GameDefinitionRef PalworldRef = new("palworld", "sha256:palworld-v1", "filesystem", "definitions/palworld-docker.yaml");
    private static readonly GameDefinitionRef FactorioRef = new("factorio", "sha256:factorio-v1", "filesystem", "definitions/factorio-docker.yaml");

    private static readonly AdoptionCriteria PalworldCriteria = new(
        "palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld");

    private static readonly AdoptionCriteria FactorioCriteria = new(
        "factorio", "Factorio Dedicated Server", "factoriotools/factorio", "/factorio");

    [Fact]
    public async Task TwoContainersOfDifferentGames_EachBindToTheirOwnDefinition()
    {
        var palworldContainer = BuildServer("c-palworld", "thijsvanloef/palworld-server-docker", "/palworld");
        var factorioContainer = BuildServer("c-factorio", "factoriotools/factorio", "/factorio");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([palworldContainer]));
        discovery.DiscoverAsync("factoriotools/factorio", "/factorio", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([factorioContainer]));

        var results = await ServerBindingResolver.ResolveAsync(
            discovery,
            [new(PalworldCriteria, PalworldRef), new(FactorioCriteria, FactorioRef)],
            NullLogger.Instance);

        results.Should().HaveCount(2);

        var palworldMatch = results.Single(r => r.Server.ServerId == "c-palworld");
        palworldMatch.State.Should().Be(ServerMatchState.Bound);
        palworldMatch.Definition.Should().Be(PalworldRef);

        var factorioMatch = results.Single(r => r.Server.ServerId == "c-factorio");
        factorioMatch.State.Should().Be(ServerMatchState.Bound);
        factorioMatch.Definition.Should().Be(FactorioRef);
    }

    [Fact]
    public async Task ContainerMatchingTwoDefinitionsWithIdenticalDetectRules_IsAmbiguous_NamingBothCandidates()
    {
        var container = BuildServer("c-tied", "thijsvanloef/palworld-server-docker", "/palworld");
        var secondPalworldRef = new GameDefinitionRef("palworld-modded", "sha256:palworld-modded-v1", "filesystem", "definitions/palworld-modded.yaml");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([container]));

        var results = await ServerBindingResolver.ResolveAsync(
            discovery,
            [new(PalworldCriteria, PalworldRef), new(PalworldCriteria, secondPalworldRef)],
            NullLogger.Instance);

        var match = results.Should().ContainSingle().Subject;
        match.State.Should().Be(ServerMatchState.Ambiguous);
        match.Definition.Should().BeNull();
        match.Candidates.Should().BeEquivalentTo([PalworldRef, secondPalworldRef]);
    }

    [Fact]
    public async Task ContainerMatchingNoCriteria_IsNotReturned()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([]));

        var results = await ServerBindingResolver.ResolveAsync(
            discovery, [new(PalworldCriteria, PalworldRef)], NullLogger.Instance);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task MostSpecificMountWins_WhenAContainerSatisfiesTwoDifferentMountsForTheSameImage()
    {
        // Same image repository, two definitions requiring different mounts. The container happens to have
        // both mounts present, so it is discovered under both detect rules; the longer (more specific)
        // required mount wins rather than leaving this tied.
        var shortMountRef = new GameDefinitionRef("shared-base", "sha256:base-v1", "filesystem", "definitions/base.yaml");
        var longMountRef = new GameDefinitionRef("shared-extended", "sha256:extended-v1", "filesystem", "definitions/extended.yaml");
        var shortMountCriteria = new AdoptionCriteria("shared-base", "Shared Base", "shared/image", "/data");
        var longMountCriteria = new AdoptionCriteria("shared-extended", "Shared Extended", "shared/image", "/data/extended");

        var container = BuildServer("c-shared", "shared/image", "/data", "/data/extended");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("shared/image", "/data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([container]));
        discovery.DiscoverAsync("shared/image", "/data/extended", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([container]));

        var results = await ServerBindingResolver.ResolveAsync(
            discovery,
            [new(shortMountCriteria, shortMountRef), new(longMountCriteria, longMountRef)],
            NullLogger.Instance);

        var match = results.Should().ContainSingle().Subject;
        match.State.Should().Be(ServerMatchState.Bound);
        match.Definition.Should().Be(longMountRef);
    }

    [Fact]
    public async Task DiscoveryFailureForOneDetectRule_DoesNotBlindResolutionToOtherDefinitions()
    {
        var factorioContainer = BuildServer("c-factorio", "factoriotools/factorio", "/factorio");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));
        discovery.DiscoverAsync("factoriotools/factorio", "/factorio", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([factorioContainer]));

        var results = await ServerBindingResolver.ResolveAsync(
            discovery,
            [new(PalworldCriteria, PalworldRef), new(FactorioCriteria, FactorioRef)],
            NullLogger.Instance);

        var match = results.Should().ContainSingle().Subject;
        match.Server.ServerId.Should().Be("c-factorio");
        match.Definition.Should().Be(FactorioRef);
    }

    private static DiscoveredServer BuildServer(string serverId, string image, params string[] mountDestinations) => new(
        ServerId: serverId,
        Name: serverId,
        Image: image,
        ImageDigest: null,
        State: "running",
        HealthStatus: "healthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: mountDestinations.Select(d => new DiscoveredMount($"/host{d}", d, true)).ToList(),
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>());
}
