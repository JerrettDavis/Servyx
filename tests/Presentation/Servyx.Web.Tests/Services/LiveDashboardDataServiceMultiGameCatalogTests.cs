using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Transport;
using Servyx.Web.Services;
using Servyx.Web.Tests.Definitions.Support;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Pins <c>LiveDashboardDataService.GetGamesAsync</c>/<c>GetGameDefinitionFaultsAsync</c> against a catalog
/// holding more than one definition — the structural change that made <c>/games</c> render every loaded
/// definition (plus a visible card for every one that failed to load) instead of only ever a single card.
/// <see cref="LiveDashboardDataServiceCatalogGamesTests"/> and
/// <c>LiveDashboardDataServiceCharacterizationTests</c> already pin the single-definition and null-catalog
/// cases byte-for-byte; this class is the "more than one" coverage those deliberately never exercised.
/// </summary>
/// <remarks>
/// Every synthesized definition here is the real, shipped <c>definitions/palworld-docker.yaml</c> with a
/// targeted <c>metadata.id</c> (and, for the malformed fixture, <c>apiVersion</c>) mutation — the same
/// "mutate a copy of the real text" convention <c>Servyx.Definitions.Tests.Support.DefinitionYamlFixture</c>
/// and <c>GameDefinitionParserPalworldRegressionTests</c> use, so every fixture stays schema-realistic
/// rather than risking drift from a bespoke miniature document. Every fixture is written under a
/// <see cref="TempDefinitionsDirectory"/>, never the repository's real <c>definitions/</c> folder.
/// </remarks>
public class LiveDashboardDataServiceMultiGameCatalogTests
{
    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static readonly Lazy<string> RealYamlLazy = new(() =>
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml");
        return File.ReadAllText(path);
    });

    private static string RealYaml => RealYamlLazy.Value;

    /// <summary>The real, shipped definition text with only its <c>metadata.id</c> swapped.</summary>
    private static string WithId(string id)
    {
        const string find = "id: palworld\n";
        RealYaml.Should().Contain(find, "the fixture mutation target must actually exist in the real definition text");
        return RealYaml.Replace(find, $"id: {id}\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Genuinely unparseable YAML — an unterminated flow sequence — rather than a mutated copy of the real
    /// document: this is the same fixture <c>GameDefinitionCatalogTests.RefreshAsync_MalformedSibling_...</c>
    /// uses. <see cref="FileSystemGameDefinitionProvider"/>'s own header pre-check catches this before a
    /// <c>metadata.id</c> is ever read, via <c>SafeYamlLoader.TryLoad</c>, which always populates
    /// line/column for a genuine <c>YamlException</c> — see that method's remarks — so the resulting
    /// <see cref="DefinitionFault"/> carries the real file path (this fault has a single, identifiable file)
    /// plus a non-null line and column.
    /// </summary>
    private const string MalformedYaml = "apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated";

    private static async Task<GameDefinitionCatalog> BuildCatalogAsync(TempDefinitionsDirectory dir)
    {
        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();
        return catalog;
    }

    private static LiveDashboardDataService BuildService(GameDefinitionCatalog catalog) => new(
        Substitute.For<IServerQueryService>(),
        NullLogger<LiveDashboardDataService>.Instance,
        Target,
        backupDashboard: null,
        catalog: catalog);

    [Fact]
    public async Task GetGamesAsync_TwoValidDefinitions_ReturnsTwoCards_DeterministicallyOrderedById()
    {
        using var dir = new TempDefinitionsDirectory();
        // Written zeta-first, alpha-second, on disk — proves the ordering is by id, not discovery/file order.
        dir.WriteFlat("zeta.yaml", WithId("zeta-game"));
        dir.WriteFlat("alpha.yaml", WithId("alpha-game"));

        var catalog = await BuildCatalogAsync(dir);
        catalog.Faults.Should().BeEmpty();
        catalog.DefinitionsById.Should().HaveCount(2);

        var sut = BuildService(catalog);

        var games = await sut.GetGamesAsync();

        games.Should().HaveCount(2);
        games.Select(g => g.Id).Should().Equal("alpha-game", "zeta-game");

        // A second call (simulating a page reload) must produce the same order — nothing here depends on
        // dictionary/iteration order happening to be stable by luck.
        var reload = await sut.GetGamesAsync();
        reload.Select(g => g.Id).Should().Equal("alpha-game", "zeta-game");
    }

    [Fact]
    public async Task GetGamesAsync_TwoValidDefinitions_PreservesFullFieldMapping_PerCard()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("a.yaml", WithId("card-a"));
        dir.WriteFlat("b.yaml", WithId("card-b"));

        var catalog = await BuildCatalogAsync(dir);
        var sut = BuildService(catalog);

        var games = await sut.GetGamesAsync();

        foreach (var card in games)
        {
            card.Name.Should().Be("Palworld Dedicated Server");
            card.Version.Should().Be("1.0.0");
            card.Tags.Should().Equal("survival", "steam", "unreal");
            card.Trust.Should().Be(Servyx.Domain.Definitions.TrustTier.Builtin);
            card.ModsSupported.Should().BeFalse();
            card.DeploymentProfiles.Should().HaveCount(1);
            card.DeploymentProfiles[0].Id.Should().Be("docker-thijsvanloef");
            card.DeploymentProfiles[0].Kind.Should().Be("docker");
        }
    }

    [Fact]
    public async Task GetGamesAndFaults_TwoValidPlusOneMalformed_ReturnsTwoCardsAndOneFaultWithPathAndPosition()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("good-a.yaml", WithId("good-a"));
        dir.WriteFlat("good-b.yaml", WithId("good-b"));
        var badPath = dir.WriteFlat("bad.yaml", MalformedYaml);

        var catalog = await BuildCatalogAsync(dir);
        var sut = BuildService(catalog);

        var games = await sut.GetGamesAsync();
        games.Select(g => g.Id).Should().Equal("good-a", "good-b");

        var faults = await sut.GetGameDefinitionFaultsAsync();
        faults.Should().ContainSingle();
        var fault = faults[0];

        fault.Path.Should().Be(badPath);
        fault.Message.Should().NotBeNullOrWhiteSpace();
        fault.Line.Should().NotBeNull("the malformed apiVersion is a validation error the parser can point at");
        fault.Column.Should().NotBeNull();
    }

    [Fact]
    public async Task GetGameDefinitionFaultsAsync_NoFaults_ReturnsEmptyList()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("only.yaml", WithId("only-game"));

        var catalog = await BuildCatalogAsync(dir);
        var sut = BuildService(catalog);

        var faults = await sut.GetGameDefinitionFaultsAsync();

        faults.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGamesAndFaults_NullCatalog_BothReturnEmptyLists()
    {
        var sut = new LiveDashboardDataService(
            Substitute.For<IServerQueryService>(),
            NullLogger<LiveDashboardDataService>.Instance,
            Target,
            backupDashboard: null,
            catalog: null);

        (await sut.GetGamesAsync()).Should().BeEmpty();
        (await sut.GetGameDefinitionFaultsAsync()).Should().BeEmpty();
    }
}
