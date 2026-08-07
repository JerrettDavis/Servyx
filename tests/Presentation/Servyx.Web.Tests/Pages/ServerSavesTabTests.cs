using Bunit;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;

namespace Servyx.Web.Tests.Pages;

public class ServerSavesTabTests : BunitContext
{
    private static readonly SaveInfo Save = new(
        WorldId: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        LevelFileName: "Level.sav",
        LevelFileSizeBytes: 5L * 1024 * 1024,
        LevelMetaFileName: "LevelMeta.sav",
        LevelMetaFileSizeBytes: 4096,
        PlayerFiles:
        [
            new PlayerSaveFile("76561198000000001.sav", 2048),
            new PlayerSaveFile("76561198000000002.sav", 4096),
        ]);

    [Fact]
    public void Listed_WithASave_RendersWorldAndPlayerFiles()
    {
        var cut = Render<ServerSavesTab>(p => p.Add(
            x => x.Result, new SavesResult(Save, SavesAvailability.Listed, null)));

        cut.Markup.Should().Contain(Save.WorldId);
        cut.Markup.Should().Contain("Level.sav");
        cut.Markup.Should().Contain("LevelMeta.sav");
        cut.FindAll("div[aria-label='Player save files'] .svx-row-link").Should().HaveCount(2);
    }

    [Fact]
    public void Listed_WithNoSave_ShowsHonestEmptyState_NotTheDegradedOne()
    {
        var cut = Render<ServerSavesTab>(p => p.Add(
            x => x.Result, new SavesResult(null, SavesAvailability.Listed, null)));

        cut.Find("[data-testid='saves-empty']").TextContent.Should().Contain("No save data found");
        cut.FindAll("[data-testid='saves-list-failed']").Should().BeEmpty();
        cut.FindAll("[data-testid='saves-not-configured']").Should().BeEmpty();
    }

    [Fact]
    public void Failed_RendersTheDegradedStateWithFailureDetail_NeverCollapsedIntoEmpty()
    {
        var cut = Render<ServerSavesTab>(p => p.Add(
            x => x.Result, new SavesResult(null, SavesAvailability.Failed, "daemon unreachable")));

        var alert = cut.Find("[data-testid='saves-list-failed']");
        alert.GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid='saves-list-failure-detail']").TextContent.Should().Be("daemon unreachable");
        cut.FindAll("[data-testid='saves-empty']").Should().BeEmpty();
    }

    [Fact]
    public void NotConfigured_RendersItsOwnDistinctState()
    {
        var cut = Render<ServerSavesTab>(p => p.Add(
            x => x.Result, new SavesResult(null, SavesAvailability.NotConfigured, null)));

        cut.Find("[data-testid='saves-not-configured']").Should().NotBeNull();
        cut.FindAll("[data-testid='saves-list-failed']").Should().BeEmpty();
        cut.FindAll("[data-testid='saves-empty']").Should().BeEmpty();
    }

    [Fact]
    public void NoPlayersYet_ShowsItsOwnEmptyStateInsideTheCard()
    {
        var save = Save with { PlayerFiles = [] };
        var cut = Render<ServerSavesTab>(p => p.Add(
            x => x.Result, new SavesResult(save, SavesAvailability.Listed, null)));

        cut.Markup.Should().Contain("No players have joined this world yet");
    }
}
