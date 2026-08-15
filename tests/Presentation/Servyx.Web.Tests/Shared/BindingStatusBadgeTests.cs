using Bunit;
using Servyx.Application.Servers;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

/// <summary>
/// A fifth badge in the <see cref="StateBadge"/>/<see cref="HealthBadge"/>/<see cref="DriftBadge"/>/
/// <see cref="TrustBadge"/> family, mirroring their shape exactly rather than sharing a base — that
/// consolidation was deliberately deferred (see <see cref="TrustBadgeTests"/>'s identical remark).
/// </summary>
public class BindingStatusBadgeTests : BunitContext
{
    [Fact]
    public void Bound_renders_no_badge_at_all()
    {
        // ServerBindingStatus.Bound is the overwhelmingly common case: the badge must be visually silent
        // for it, not a "Bound" pill with different styling than today.
        var cut = Render<BindingStatusBadge>(p => p.Add(x => x.Status, ServerBindingStatus.Bound));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Ambiguous_renders_a_badge_naming_the_tied_candidates_in_its_tooltip()
    {
        var cut = Render<BindingStatusBadge>(p => p
            .Add(x => x.Status, ServerBindingStatus.Ambiguous)
            .Add(x => x.CandidateGameIds, ["palworld", "palworld-modded"]));

        var span = cut.Find("span[data-testid='binding-status-badge']");
        span.ClassList.Should().Contain("svx-badge");
        span.ClassList.Should().Contain("binding-status-ambiguous");
        span.TextContent.Trim().Should().Be("Ambiguous binding");

        var tooltip = span.GetAttribute("title");
        tooltip.Should().Contain("palworld");
        tooltip.Should().Contain("palworld-modded");
        tooltip.Should().Contain("did not guess");
    }

    [Fact]
    public void NeedsRebind_renders_a_badge_naming_the_previous_definition_and_points_to_the_rebind_action()
    {
        var cut = Render<BindingStatusBadge>(p => p
            .Add(x => x.Status, ServerBindingStatus.NeedsRebind)
            .Add(x => x.CandidateGameIds, ["palworld"]));

        var span = cut.Find("span[data-testid='binding-status-badge']");
        span.ClassList.Should().Contain("binding-status-needsrebind");
        span.TextContent.Trim().Should().Be("Needs rebind");

        var tooltip = span.GetAttribute("title");
        tooltip.Should().Contain("palworld");
        // The server detail page now offers an explicit Rebind action for this state (see
        // ServerBindingStatusRenderingTests) — the tooltip must route there rather than claim none exists.
        tooltip.Should().Contain("explicitly rebind it");
        tooltip.Should().NotContain("no action in Servyx to resolve this");
    }
}
