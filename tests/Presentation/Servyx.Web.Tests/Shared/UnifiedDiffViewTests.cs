using Bunit;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

public class UnifiedDiffViewTests : BunitContext
{
    private const string SmallDiff =
        "--- a/palworld/.env\n" +
        "+++ b/palworld/.env\n" +
        "@@ -1,3 +1,3 @@\n" +
        " UNCHANGED=1\n" +
        "-PORT=8211\n" +
        "+PORT=9000\n" +
        " ANOTHER=2\n";

    [Fact]
    public void Renders_added_removed_and_context_lines_with_distinct_classes()
    {
        var cut = Render<UnifiedDiffView>(p => p.Add(x => x.Diff, SmallDiff));

        var root = cut.Find("[data-testid='plan-diff']");
        root.Should().NotBeNull();

        cut.Find(".diff-add").TextContent.Should().Contain("PORT=9000");
        cut.Find(".diff-remove").TextContent.Should().Contain("PORT=8211");
        cut.FindAll(".diff-context").Should().NotBeEmpty();
        cut.FindAll(".diff-header").Should().HaveCount(2);
        cut.FindAll(".diff-hunk").Should().ContainSingle();
    }

    [Fact]
    public void An_empty_diff_says_so_rather_than_rendering_an_empty_box()
    {
        var cut = Render<UnifiedDiffView>(p => p.Add(x => x.Diff, string.Empty));

        cut.Find("[data-testid='plan-diff-empty']").Should().NotBeNull();
        cut.FindAll("[data-testid='plan-diff']").Should().BeEmpty();
    }

    [Fact]
    public void A_title_is_rendered_when_supplied()
    {
        var cut = Render<UnifiedDiffView>(p => p
            .Add(x => x.Diff, SmallDiff)
            .Add(x => x.Title, "palworld/.env"));

        cut.Find(".diff-title").TextContent.Should().Be("palworld/.env");
    }

    [Fact]
    public void A_long_diff_collapses_past_the_threshold_and_expands_on_click()
    {
        var lines = new List<string> { "--- a/x", "+++ b/x", "@@ -1,50 +1,50 @@" };
        for (var i = 0; i < 50; i++)
        {
            lines.Add($" context line {i}");
        }

        var diff = string.Join('\n', lines);
        var cut = Render<UnifiedDiffView>(p => p.Add(x => x.Diff, diff));

        cut.FindAll(".diff-line").Count.Should().Be(UnifiedDiffView.CollapseThreshold);

        var expand = cut.Find("[data-testid='plan-diff-expand']");
        expand.TextContent.Should().Contain("more line");
        expand.Click();

        cut.FindAll(".diff-line").Count.Should().Be(lines.Count);
        cut.FindAll("[data-testid='plan-diff-expand']").Should().BeEmpty();
    }

    [Fact]
    public void A_short_diff_never_shows_the_expand_control()
    {
        var cut = Render<UnifiedDiffView>(p => p.Add(x => x.Diff, SmallDiff));

        cut.FindAll("[data-testid='plan-diff-expand']").Should().BeEmpty();
    }
}
