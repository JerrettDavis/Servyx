using Bunit;
using FluentAssertions;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

public class ServerBackupsTabTests : BunitContext
{
    private static IReadOnlyList<BackupEntry> SampleBackups()
        => new MockDashboardDataService().GetServerBackupsAsync("palygondwanaland").GetAwaiter().GetResult();

    [Fact]
    public void RendersForeignBackups_WithNoDestructiveControlsAtAll()
    {
        var backups = SampleBackups();

        var cut = Render<ServerBackupsTab>(p => p.Add(x => x.Backups, backups));

        var badges = cut.FindAll(".foreign-badge");
        badges.Should().HaveCount(backups.Count);
        foreach (var badge in badges)
        {
            badge.TextContent.Trim().Should().Be("Foreign");
            badge.GetAttribute("title").Should().Contain("Servyx will never prune, move, or rename");
        }

        // Not even disabled destructive controls should exist for foreign backups: no buttons,
        // no inputs, no anchors — just the read-only list and an explanatory tooltip.
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("a").Should().BeEmpty();

        var lowerMarkup = cut.Markup.ToLowerInvariant();
        lowerMarkup.Should().NotContain("delete");
        lowerMarkup.Should().NotContain("restore");
    }

    [Fact]
    public void NoBackups_ShowsHonestEmptyState()
    {
        var cut = Render<ServerBackupsTab>(p => p.Add(x => x.Backups, Array.Empty<BackupEntry>()));

        cut.Markup.Should().Contain("No backups found");
        cut.FindAll("button").Should().BeEmpty();
    }
}
