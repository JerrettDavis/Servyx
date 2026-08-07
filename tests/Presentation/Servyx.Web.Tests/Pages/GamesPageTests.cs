using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Definitions;
using Servyx.Web.Components.Pages.Games;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the Games page — the surface that now renders every loaded game definition as its own
/// card, plus a visibly distinct card for every definition that failed to load, instead of ever assuming
/// there is exactly one. See <see cref="Servyx.Web.Services.LiveDashboardDataService"/>'s
/// <c>BuildGamesFromCatalog</c>/<c>BuildFaultsFromCatalog</c> for where the data these tests render comes
/// from in production; this page-level suite only exercises the Razor rendering, via
/// <see cref="FixedGamesDataService"/>.
/// </summary>
public class GamesPageTests : BunitContext
{
    private static GameCardSummary Card(string id, string name = "Some Game") => new(
        Id: id,
        Name: name,
        Version: "1.0.0",
        Tags: ["tag-a"],
        Trust: TrustTier.Builtin,
        ModsSupported: false,
        DeploymentProfiles: [new DeploymentProfileSummary($"{id}-profile", "docker", $"{id} description")]);

    private void Arrange(IReadOnlyList<GameCardSummary>? games = null, IReadOnlyList<GameDefinitionFaultSummary>? faults = null) =>
        Services.AddSingleton<IDashboardDataService>(new FixedGamesDataService(games, faults));

    [Fact]
    public void No_games_and_no_faults_renders_the_original_empty_state()
    {
        Arrange();

        var cut = Render<GamesPage>();

        cut.Find("[data-testid=games-empty]").TextContent.Should().Contain("No game definitions found");
        cut.FindAll("[data-testid=game-card]").Should().BeEmpty();
        cut.FindAll("[data-testid=game-fault-card]").Should().BeEmpty();
    }

    [Fact]
    public void Two_valid_definitions_render_two_cards()
    {
        Arrange(games: [Card("game-a", "Game A"), Card("game-b", "Game B")]);

        var cut = Render<GamesPage>();

        cut.FindAll("[data-testid=game-card]").Should().HaveCount(2);
        cut.Markup.Should().Contain("Game A");
        cut.Markup.Should().Contain("Game B");
        cut.FindAll("[data-testid=games-empty]").Should().BeEmpty();
    }

    [Fact]
    public void Faults_render_as_visibly_distinct_cards_with_path_and_position_when_present()
    {
        Arrange(faults:
        [
            new GameDefinitionFaultSummary("C:\\defs\\bad.yaml", "The file could not be parsed as YAML.", 4, 8),
        ]);

        var cut = Render<GamesPage>();

        // Never silently absent — the empty state must not appear when there is a fault to show.
        cut.FindAll("[data-testid=games-empty]").Should().BeEmpty();

        var faultCard = cut.Find("[data-testid=game-fault-card]");
        faultCard.GetAttribute("role").Should().Be("alert");
        faultCard.ClassList.Should().Contain("game-card-fault");

        cut.Find("[data-testid=game-fault-path]").TextContent.Should().Contain("C:\\defs\\bad.yaml");
        cut.Find("[data-testid=game-fault-message]").TextContent.Should().Contain("The file could not be parsed as YAML.");
        cut.Find("[data-testid=game-fault-location]").TextContent.Should().Contain("Line 4").And.Contain("column 8");
    }

    [Fact]
    public void A_fault_with_no_known_position_omits_the_location_line_rather_than_showing_a_blank_one()
    {
        Arrange(faults: [new GameDefinitionFaultSummary("directory:duplicate-id", "Duplicate definition id.", null, null)]);

        var cut = Render<GamesPage>();

        cut.Find("[data-testid=game-fault-card]");
        cut.FindAll("[data-testid=game-fault-location]").Should().BeEmpty();
    }

    [Fact]
    public void Valid_games_and_faults_render_together_neither_hiding_the_other()
    {
        Arrange(
            games: [Card("game-a", "Game A")],
            faults: [new GameDefinitionFaultSummary("bad.yaml", "Broken.", 1, 1)]);

        var cut = Render<GamesPage>();

        cut.FindAll("[data-testid=game-card]").Should().ContainSingle();
        cut.FindAll("[data-testid=game-fault-card]").Should().ContainSingle();
        cut.FindAll("[data-testid=games-empty]").Should().BeEmpty();
    }
}
