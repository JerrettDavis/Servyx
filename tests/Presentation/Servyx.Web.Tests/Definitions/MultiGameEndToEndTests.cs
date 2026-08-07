using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Web.Tests.Definitions.Support;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Definitions;

/// <summary>
/// End-to-end proof of milestone M6's own acceptance criterion (<c>docs/roadmap.md</c>): with BOTH real,
/// shipped definitions — <c>definitions/palworld-docker.yaml</c> and <c>definitions/minecraft-itzg.yaml</c>
/// — loaded from a synthesized temp directory (never the repository's real <c>definitions/</c> folder, so
/// this suite never depends on ordering against it), a fake-discovered <c>itzg/minecraft-server</c>
/// container and a fake-discovered <c>thijsvanloef/palworld-server-docker</c> container each bind to their
/// own definition, each renders its own catalogue card, each gets its own settings list (the single most
/// important assertion here — it proves settings are genuinely per-server, not ambient), each gets its own
/// health signal (or correctly none), and neither is mislabelled or <c>Ambiguous</c>.
/// </summary>
public class MultiGameEndToEndTests
{
    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static string RealDefinitionText(string fileName)
    {
        var repoRoot = RepoRootLocator.Find();
        return File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", fileName));
    }

    private static async Task<GameDefinitionCatalog> BuildCatalogAsync(TempDefinitionsDirectory dir)
    {
        dir.WriteFlat("palworld.yaml", RealDefinitionText("palworld-docker.yaml"));
        dir.WriteFlat("minecraft.yaml", RealDefinitionText("minecraft-itzg.yaml"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();
        return catalog;
    }

    private static DiscoveredServer BuildMinecraftServer() => new(
        ServerId: "c-minecraft",
        Name: "c-minecraft",
        Image: "itzg/minecraft-server",
        ImageDigest: null,
        State: "running",
        HealthStatus: "unhealthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: [new DiscoveredMount("/host/data", "/data", true)],
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>
        {
            ["EULA"] = "true",
            ["MOTD"] = "A Minecraft Server",
            ["TYPE"] = "VANILLA",
            ["VERSION"] = "1.20.4",
            ["MEMORY"] = "2G",
            ["DIFFICULTY"] = "easy",
            ["MAX_PLAYERS"] = "20",
            ["PVP"] = "true",
            ["ENABLE_RCON"] = "true",
            ["RCON_PASSWORD"] = "topsecret",
            ["RCON_PORT"] = "25575",
            ["SERVER_PORT"] = "25565",
            ["LEVEL"] = "world",
        });

    private static DiscoveredServer BuildPalworldServer() => new(
        ServerId: "c-palworld",
        Name: "c-palworld",
        Image: "thijsvanloef/palworld-server-docker",
        ImageDigest: null,
        State: "running",
        HealthStatus: "unhealthy",
        CreatedAt: DateTimeOffset.UnixEpoch,
        StartedAt: DateTimeOffset.UnixEpoch,
        Ports: [],
        Mounts: [new DiscoveredMount("/host/palworld", "/palworld", true)],
        NetworkName: null,
        ContainerIp: null,
        MemoryLimitBytes: null,
        CpuLimit: null,
        RestartPolicy: null,
        ComposeLabels: new Dictionary<string, string>(),
        EnvironmentVariables: new Dictionary<string, string>
        {
            ["SERVER_NAME"] = "My Pal Server",
            ["SERVER_DESCRIPTION"] = "A world",
            ["PORT"] = "8211",
            ["RCON_PORT"] = "25575",
            ["PLAYERS"] = "16",
            ["DIFFICULTY"] = "Normal",
            ["DAY_TIME_SPEEDRATE"] = "1.000000",
            ["ENABLE_PLAYER_TO_PLAYER_DAMAGE"] = "False",
            ["ADMIN_PASSWORD"] = "adminsecret",
            ["SERVER_PASSWORD"] = "joinsecret",
        });

    [Fact]
    public async Task BothDefinitions_LoadWithNoFaults_AndRenderTheirOwnCatalogueCard()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir);

        catalog.Faults.Should().BeEmpty("both real definitions must parse with zero validation Errors");
        catalog.DefinitionsById.Should().HaveCount(2);

        var dashboard = new LiveDashboardDataService(
            Substitute.For<IServerQueryService>(),
            NullLogger<LiveDashboardDataService>.Instance,
            Target,
            backupDashboard: null,
            catalog: catalog);

        var games = await dashboard.GetGamesAsync();

        games.Select(g => g.Id).Should().BeEquivalentTo(["palworld", "minecraft-itzg"]);

        var palworldCard = games.Single(g => g.Id == "palworld");
        palworldCard.Name.Should().Be("Palworld Dedicated Server");
        palworldCard.Tags.Should().Equal("survival", "steam", "unreal");

        var minecraftCard = games.Single(g => g.Id == "minecraft-itzg");
        minecraftCard.Name.Should().Be("Minecraft Server (itzg)");
        minecraftCard.Tags.Should().Equal("survival", "java", "sandbox");

        // NOT a bug this task introduces: LiveDashboardDataService.BuildGamesFromCatalog hardcodes
        // ModsSupported: false for every card as a "CHARACTERIZATION-parity literal" (see that method's own
        // remarks), even though minecraft-itzg.yaml's own mods.supported is genuinely true. Pinned here,
        // not fixed — LiveDashboardDataServiceCharacterizationTests must not change, and this is exactly the
        // kind of pre-existing gap this task's report calls out rather than silently working around.
        minecraftCard.ModsSupported.Should().BeFalse(
            "GameCardSummary.ModsSupported is a hardcoded characterization-parity literal today, not yet sourced from the definition's own mods.supported");
    }

    [Fact]
    public async Task TwoContainers_EachBindsToItsOwnDefinition_NeitherAmbiguousNorMislabelled()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir);

        var criteriaSet = AdoptionCriteriaFactory.DeriveAll(
            catalog.DefinitionsById.Values
                .Select(loaded => (loaded.Ref, Definition: loaded.Document as GameDefinition))
                .Where(pair => pair.Definition is not null)
                .Select(pair => (pair.Ref, Definition: pair.Definition!)));
        criteriaSet.Should().HaveCount(2);

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("itzg/minecraft-server", "/data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildMinecraftServer()]));
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildPalworldServer()]));

        var lookup = new CatalogBoundDefinitionLookup(catalog);
        var sut = new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            Substitute.For<ITransport>(),
            criteriaSet,
            lookup,
            NullLogger<ServerQueryService>.Instance);

        var servers = await sut.GetAdoptedServersAsync();

        servers.Should().HaveCount(2);
        servers.Should().OnlyContain(s => s.BindingStatus == ServerBindingStatus.Bound);

        var minecraft = servers.Single(s => s.Id == "c-minecraft");
        minecraft.Game.Should().Be("Minecraft Server (itzg)");

        var palworld = servers.Single(s => s.Id == "c-palworld");
        palworld.Game.Should().Be("Palworld Dedicated Server");

        // Each definition declares its own lifecycle.healthSignal (or, for Minecraft, correctly none) — the
        // per-server health signal proof. Both containers are reported unhealthy above so both explanations
        // are exercised.
        palworld.HealthDetail.Should().Contain(
            "401 Unauthorized", "Palworld's own healthSignal.explanation must be used for its unhealthy detail");
        minecraft.HealthDetail.Should().Be(
            // ServerQueryService.GenericUnhealthyExplanation is internal; pinning the literal here matches
            // the convention ServerQueryServiceMultiDefinitionTests already uses for another internal
            // constant (NeedsRebindGameName) — see that class's own comment.
            "The container's own health check is reporting unhealthy. This definition has not documented " +
            "whether that signal can be trusted, so Servyx is showing it as-is.",
            "Minecraft's definition declares no lifecycle.healthSignal block at all, so the generic, "
                + "game-neutral explanation must be used instead of Palworld's");
    }

    [Fact]
    public async Task TwoContainers_EachGetsItsOwnSettingsList_NeverTheOtherGames()
    {
        using var dir = new TempDefinitionsDirectory();
        var catalog = await BuildCatalogAsync(dir);

        var criteriaSet = AdoptionCriteriaFactory.DeriveAll(
            catalog.DefinitionsById.Values
                .Select(loaded => (loaded.Ref, Definition: loaded.Document as GameDefinition))
                .Where(pair => pair.Definition is not null)
                .Select(pair => (pair.Ref, Definition: pair.Definition!)));

        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync("itzg/minecraft-server", "/data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildMinecraftServer()]));
        discovery.DiscoverAsync("thijsvanloef/palworld-server-docker", "/palworld", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DiscoveredServer>>([BuildPalworldServer()]));

        var lookup = new CatalogBoundDefinitionLookup(catalog);
        var sut = new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            Substitute.For<ITransport>(),
            criteriaSet,
            lookup,
            NullLogger<ServerQueryService>.Instance);

        var minecraftDetail = await sut.GetServerDetailAsync("c-minecraft");
        var palworldDetail = await sut.GetServerDetailAsync("c-palworld");

        minecraftDetail.Should().NotBeNull();
        palworldDetail.Should().NotBeNull();

        var minecraftKeys = minecraftDetail!.Settings.Select(s => s.Key).ToList();
        var palworldKeys = palworldDetail!.Settings.Select(s => s.Key).ToList();

        // THE core proof: settings are genuinely per-server, not ambient. The Minecraft server shows its own
        // env-derived settings and none of Palworld's; the Palworld server shows its own and none of
        // Minecraft's.
        minecraftKeys.Should().Contain(["EULA", "MOTD", "TYPE", "VERSION", "MEMORY", "DIFFICULTY", "MAX_PLAYERS", "rcon-password"]);
        minecraftKeys.Should().NotContain(["SERVER_NAME", "admin-password", "DAY_TIME_SPEEDRATE", "PLAYERS"]);

        palworldKeys.Should().Contain(["SERVER_NAME", "PORT", "PLAYERS", "DIFFICULTY", "admin-password"]);
        palworldKeys.Should().NotContain(["EULA", "MOTD", "TYPE", "VERSION", "MEMORY", "rcon-password"]);

        // The env-sourced values themselves are read from each server's OWN discovered environment, not
        // cross-contaminated between the two.
        minecraftDetail.Settings.Single(s => s.Key == "MOTD").Authoritative.Should().Be("A Minecraft Server");
        palworldDetail.Settings.Single(s => s.Key == "SERVER_NAME").Authoritative.Should().Be("My Pal Server");

        // Secrets are masked, but each server's secret rows are still its own, distinct set.
        minecraftDetail.Settings.Single(s => s.Key == "rcon-password").IsSecret.Should().BeTrue();
        palworldDetail.Settings.Single(s => s.Key == "admin-password").IsSecret.Should().BeTrue();
    }
}
