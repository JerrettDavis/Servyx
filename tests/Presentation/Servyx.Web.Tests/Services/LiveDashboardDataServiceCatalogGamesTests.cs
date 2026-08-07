using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Transport;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Definitions.Support;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Pins <c>LiveDashboardDataService.BuildGamesFromCatalog</c> — the data-driven, and now only,
/// <c>GetGamesAsync</c> path — against a real <see cref="GameDefinitionCatalog"/> built the same way
/// <c>Program.cs</c>'s bootstrap block builds one: a <see cref="FileSystemGameDefinitionProvider"/>, refreshed
/// once, whose text is the real, unmodified <c>definitions/palworld-docker.yaml</c>. This test was added
/// ahead of, and is what made safe, the retirement of this codebase's original hardcoded loader (formerly
/// <c>PalworldDefinitionLoader</c>/<c>PalworldDefinitionInfo</c>): every literal asserted below was first
/// proven identical to that legacy path's own pinned output before the legacy path was deleted, and
/// <c>LiveDashboardDataServiceCharacterizationTests</c> now asserts the same literals through the same
/// catalog-backed construction this test uses.
/// </summary>
/// <remarks>
/// <c>LoadRealCatalogAsync</c> used to root the provider directly at the repository's own <c>definitions/</c>
/// directory. Since M6's second real game definition (<c>definitions/minecraft-itzg.yaml</c>) now lives
/// there too, doing so would make this "exactly one card" pin observe two — a test-isolation problem, not a
/// behavior change this class exists to pin. The fix copies the real Palworld file's own unmodified text
/// into a throwaway <see cref="TempDefinitionsDirectory"/> instead — the exact byte content this suite has
/// always pinned, just no longer sharing a directory listing with a second, unrelated definition. No
/// assertion below changed.
/// </remarks>
public class LiveDashboardDataServiceCatalogGamesTests
{
    private static readonly TargetDescriptor Target =
        new("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>());

    private static async Task<GameDefinitionCatalog> LoadRealCatalogAsync()
    {
        var repoRoot = RepoRootLocator.Find();
        var yaml = await File.ReadAllTextAsync(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));

        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("palworld-docker.yaml", yaml);

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();

        return catalog;
    }

    [Fact]
    public async Task BuildGamesFromCatalog_ReturnsExactlyOneCard_WithEveryFieldMatchingTheLegacyPathsPinnedLiterals()
    {
        var catalog = await LoadRealCatalogAsync();
        catalog.Faults.Should().BeEmpty("definitions/palworld-docker.yaml must load cleanly for this pin to be meaningful");
        catalog.DefinitionsById.Should().ContainSingle("this pin assumes today's bundled definitions/ directory holds exactly one definition");

        var sut = new LiveDashboardDataService(
            Substitute.For<IServerQueryService>(),
            NullLogger<LiveDashboardDataService>.Instance,
            Target,
            backupDashboard: null,
            catalog: catalog);

        var games = await sut.GetGamesAsync();

        games.Should().HaveCount(1);
        var card = games[0];

        card.Id.Should().Be("palworld");
        card.Name.Should().Be("Palworld Dedicated Server");
        card.Version.Should().Be("1.0.0");
        card.Tags.Should().Equal("survival", "steam", "unreal");
        // Same CHARACTERIZATION-parity literal the legacy path pins: BuildGamesFromCatalog hardcodes
        // TrustTier.Builtin rather than reading a real trust evaluation (none exists yet — see
        // FileSystemGameDefinitionProvider's remarks on trust evaluation being out of scope this phase).
        card.Trust.Should().Be(TrustTier.Builtin);
        // Same CHARACTERIZATION-parity literal the legacy path pins: BuildGamesFromCatalog hardcodes
        // ModsSupported: false rather than reading the parsed definition's own mods.supported value.
        card.ModsSupported.Should().BeFalse();

        card.DeploymentProfiles.Should().HaveCount(1);
        var profile = card.DeploymentProfiles[0];
        // Unlike the legacy path (which hardcodes the literal "docker-thijsvanloef"), BuildGamesFromCatalog
        // reads the deployment profile's own parsed Id — for today's bundled definition the two are the same
        // string, which is exactly what this assertion proves.
        profile.Id.Should().Be("docker-thijsvanloef");
        profile.Kind.Should().Be("docker");
        profile.Description.Should().Be(
            "thijsvanloef/palworld-server-docker:latest. Adopts an existing container whose image "
            + "repository matches 'thijsvanloef/palworld-server-docker'.");
    }
}
