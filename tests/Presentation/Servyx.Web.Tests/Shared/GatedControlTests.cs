using Bunit;
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
    public void GatedButton_DefaultReason_ExplainsTheWriteGrant()
    {
        var cut = Render<GatedButton>(parameters => parameters.AddChildContent("Stop"));

        cut.Find("button").GetAttribute("title")
            .Should().Be("This server is in read-only mode. An operator must grant it write access before this control works.");
    }

    [Fact]
    public void Existing_gated_call_sites_are_unchanged_when_enabled_defaults_false()
    {
        // No call site anywhere in the app predating write support ever set Enabled or OnClick — both
        // default, and both components must therefore render exactly as they always have: disabled,
        // lock-iconed, with the explanatory tooltip.
        var button = Render<GatedButton>(parameters => parameters
            .Add(p => p.Label, "Start")
            .AddChildContent("Start"));

        var buttonElement = button.Find("button");
        buttonElement.HasAttribute("disabled").Should().BeTrue();
        buttonElement.QuerySelector("svg, .icon").Should().NotBeNull();

        var control = Render<GatedControl>(parameters => parameters
            .Add(p => p.Label, "Desired value")
            .AddChildContent("<input value=\"32\" />"));

        var fieldset = control.Find("fieldset");
        fieldset.HasAttribute("disabled").Should().BeTrue();
        fieldset.QuerySelector(".gated-control-lock").Should().NotBeNull();
    }
}
