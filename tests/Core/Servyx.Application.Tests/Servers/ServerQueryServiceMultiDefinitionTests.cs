using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application.Tests.Servers;

/// <summary>
/// Tests for <see cref="ServerQueryService"/>'s multi-definition constructor: per-server binding
/// resolution, ambiguity surfacing, unmatched containers, and the persisted-binding pin (including the
/// stale-content-hash "needs re-binding" case) — see <c>DiscoverMultiAsync</c>'s own remarks.
/// </summary>
public class ServerQueryServiceMultiDefinitionTests
{
    private static readonly GameDefinitionRef PalworldRef = new("palworld", "sha256:palworld-v1", "filesystem", "definitions/palworld-docker.yaml");
    private static readonly GameDefinitionRef FactorioRef = new("factorio", "sha256:factorio-v1", "filesystem", "definitions/factorio-docker.yaml");

    private static readonly AdoptionCriteria PalworldCriteria = new(
        "palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld");

    private static readonly AdoptionCriteria FactorioCriteria = new(
        "factorio", "Factorio Dedicated Server", "factoriotools/factorio", "/factorio");

    [Fact]
    public async Task TwoDefinitionsTwoContainers_EachBindsToItsOwnDefinition_WithItsOwnSettingsAndGameName()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-palworld", "thijsvanloef/palworld-server-docker", "/palworld")]));
        discovery.DiscoverAsync("factoriotools/factorio", "/factorio", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-factorio", "factoriotools/factorio", "/factorio")]));

        var lookup = new FakeDefinitionLookup();
        lookup.Add(PalworldRef.ContentHash, new BoundDefinitionData("palworld", "Palworld Dedicated Server", [], NoHealthSignal()));
        lookup.Add(FactorioRef.ContentHash, new BoundDefinitionData("factorio", "Factorio Dedicated Server", [], NoHealthSignal()));

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef), new(FactorioCriteria, FactorioRef)], lookup);

        var servers = await sut.GetAdoptedServersAsync();

        servers.Should().HaveCount(2);
        servers.Single(s => s.Id == "c-palworld").Game.Should().Be("Palworld Dedicated Server");
        servers.Single(s => s.Id == "c-factorio").Game.Should().Be("Factorio Dedicated Server");
        servers.Should().OnlyContain(s => s.BindingStatus == ServerBindingStatus.Bound);
    }

    [Fact]
    public async Task AmbiguousMatch_IsSurfaced_NamingBothCandidates_RatherThanPickingOne()
    {
        var secondRef = new GameDefinitionRef("palworld-modded", "sha256:palworld-modded-v1", "filesystem", "definitions/palworld-modded.yaml");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-tied", "thijsvanloef/palworld-server-docker", "/palworld")]));

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef), new(PalworldCriteria, secondRef)], new FakeDefinitionLookup());

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.BindingStatus.Should().Be(ServerBindingStatus.Ambiguous);
        summary.AmbiguousCandidateGameIds.Should().BeEquivalentTo(["palworld", "palworld-modded"]);
    }

    [Fact]
    public async Task ContainerMatchingNoDefinition_IsNotAdopted()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([]));

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef)], new FakeDefinitionLookup());

        (await sut.GetAdoptedServersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ZeroDefinitions_ProducesAnHonestEmptyList_WithoutCallingDiscovery()
    {
        var discovery = Substitute.For<IServerDiscovery>();

        var sut = CreateService(discovery, [], new FakeDefinitionLookup());

        (await sut.GetAdoptedServersAsync()).Should().BeEmpty();
        await discovery.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistedBinding_IsReused_AcrossASimulatedRestart_RegardlessOfWhatFreshMatchingWouldSay()
    {
        // Two definitions now both match this container's detect rule (a hypothetical edit landed since the
        // binding was first recorded) — but the server was already pinned to Palworld by content hash, so
        // that pin wins over today's now-ambiguous fresh match. This is the "must not silently change an
        // already-running server's behaviour" guarantee.
        var secondRef = new GameDefinitionRef("palworld-modded", "sha256:palworld-modded-v1", "filesystem", "definitions/palworld-modded.yaml");

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-palworld", "thijsvanloef/palworld-server-docker", "/palworld")]));

        var lookup = new FakeDefinitionLookup();
        lookup.Add(PalworldRef.ContentHash, new BoundDefinitionData("palworld", "Palworld Dedicated Server", [], NoHealthSignal()));

        var store = new FakeBindingStore();
        await store.SaveAsync(new ServerDefinitionBinding("c-palworld", ServerDefinitionBindingState.Bound, PalworldRef, [], DateTimeOffset.UnixEpoch));

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef), new(PalworldCriteria, secondRef)], lookup, store);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.BindingStatus.Should().Be(ServerBindingStatus.Bound);
        summary.Game.Should().Be("Palworld Dedicated Server");
    }

    [Fact]
    public async Task UnresolvedFirstMatch_IsPersisted_SoTheNextCallReusesItWithoutReResolving()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-palworld", "thijsvanloef/palworld-server-docker", "/palworld")]));

        var lookup = new FakeDefinitionLookup();
        lookup.Add(PalworldRef.ContentHash, new BoundDefinitionData("palworld", "Palworld Dedicated Server", [], NoHealthSignal()));
        var store = new FakeBindingStore();

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef)], lookup, store);

        await sut.GetAdoptedServersAsync();

        var persisted = await store.TryGetAsync("c-palworld");
        persisted.Should().NotBeNull();
        persisted!.State.Should().Be(ServerDefinitionBindingState.Bound);
        persisted.Definition.Should().Be(PalworldRef);
    }

    [Fact]
    public async Task PinnedContentHashNoLongerInCatalog_IsMarkedNeedsRebind_NotSilentlySubstituted()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildServer("c-palworld", "thijsvanloef/palworld-server-docker", "/palworld")]));

        // The lookup deliberately has nothing for PalworldRef.ContentHash — the definition was edited or
        // removed since this server was bound, so its pinned content hash no longer resolves.
        var lookup = new FakeDefinitionLookup();

        var store = new FakeBindingStore();
        await store.SaveAsync(new ServerDefinitionBinding("c-palworld", ServerDefinitionBindingState.Bound, PalworldRef, [], DateTimeOffset.UnixEpoch));

        var sut = CreateService(discovery, [new(PalworldCriteria, PalworldRef)], lookup, store);

        var summary = (await sut.GetAdoptedServersAsync()).Single();

        summary.BindingStatus.Should().Be(ServerBindingStatus.NeedsRebind);
        // "Unknown (needs re-binding)" — ServerQueryService.NeedsRebindGameName is internal, so this pins
        // the literal rather than referencing it, matching this test project's existing convention (see
        // ServerQueryServiceCharacterizationTests.ExpectedGenericUnhealthyExplanation).
        summary.Game.Should().Be("Unknown (needs re-binding)");

        // Never silently substituted: the persisted row itself must still name the original pin, not have
        // been silently rewritten to whatever currently resolves for that id.
        var stillPersisted = await store.TryGetAsync("c-palworld");
        stillPersisted!.Definition.Should().Be(PalworldRef);
    }

    private static LifecycleDefinition NoHealthSignal() => new(Ready: [], Stop: new StopPlan([]), CrashDetection: [], HealthSignal: null);

    private static ServerQueryService CreateService(
        IServerDiscovery discovery,
        IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet,
        IBoundDefinitionLookup lookup,
        IServerDefinitionBindingStore? bindingStore = null) => new(
        discovery,
        Substitute.For<IMetricsSource>(),
        Substitute.For<ILogStream>(),
        Substitute.For<ITransport>(),
        criteriaSet,
        lookup,
        NullLogger<ServerQueryService>.Instance,
        bindingStore);

    private static DiscoveredServer BuildServer(string serverId, string image, string mountDestination) => new(
        ServerId: serverId,
        Name: serverId,
        Image: image,
        ImageDigest: null,
        State: "running",
        HealthStatus: "healthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: [new DiscoveredMount($"/host{mountDestination}", mountDestination, true)],
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>());

    private sealed class FakeDefinitionLookup : IBoundDefinitionLookup
    {
        private readonly Dictionary<string, BoundDefinitionData> _byHash = new(StringComparer.Ordinal);

        public void Add(string contentHash, BoundDefinitionData data) => _byHash[contentHash] = data;

        public BoundDefinitionData? TryGetByContentHash(string contentHash) =>
            _byHash.TryGetValue(contentHash, out var data) ? data : null;
    }

    private sealed class FakeBindingStore : IServerDefinitionBindingStore
    {
        private readonly Dictionary<string, ServerDefinitionBinding> _byServerId = new(StringComparer.Ordinal);

        public Task<ServerDefinitionBinding?> TryGetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(_byServerId.TryGetValue(serverId, out var binding) ? binding : null);

        public Task SaveAsync(ServerDefinitionBinding binding, CancellationToken ct = default)
        {
            _byServerId[binding.ServerId] = binding;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string serverId, CancellationToken ct = default)
        {
            _byServerId.Remove(serverId);
            return Task.CompletedTask;
        }
    }
}
