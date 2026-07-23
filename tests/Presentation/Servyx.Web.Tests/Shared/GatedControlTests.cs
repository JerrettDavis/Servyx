using Bunit;
using FluentAssertions;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

public class GatedControlTests : BunitContext
{
    [Fact]
    public void GatedButton_RendersDisabled_WithTooltipAndAriaLabel()
    {
        var cut = Render<GatedButton>(parameters => parameters
            .Add(p => p.Label, "Start")
            .AddChildContent("Start"));

        var button = cut.Find("button");

        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("title").Should().Contain("read-only mode");
        button.GetAttribute("aria-label").Should().Contain("read-only mode");
    }

    [Fact]
    public void GatedControl_RendersDisabledFieldset_WithTooltipAndAriaLabel()
    {
        var cut = Render<GatedControl>(parameters => parameters
            .Add(p => p.Label, "Desired value")
            .AddChildContent("<input value=\"32\" />"));

        var fieldset = cut.Find("fieldset");

        fieldset.HasAttribute("disabled").Should().BeTrue();
        fieldset.GetAttribute("title").Should().Contain("read-only mode");
        fieldset.GetAttribute("aria-label").Should().Contain("read-only mode");
        fieldset.QuerySelector("input").Should().NotBeNull();
    }

    [Fact]
    public void GatedButton_DefaultReason_ExplainsMilestone4()
    {
        var cut = Render<GatedButton>(parameters => parameters.AddChildContent("Stop"));

        cut.Find("button").GetAttribute("title")
            .Should().Be("Servyx is in read-only mode. Writes are enabled per-server in Milestone 4.");
    }
}
