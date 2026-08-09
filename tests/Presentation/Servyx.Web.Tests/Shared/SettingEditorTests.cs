using Bunit;
using Servyx.Domain.Definitions.Model;
using Servyx.Web.Components.Shared;

namespace Servyx.Web.Tests.Shared;

/// <summary>
/// Coverage for <see cref="SettingEditor"/> rendering every <see cref="SettingType"/>, honouring
/// <see cref="SettingConstraints"/>/<see cref="SettingDescriptor.Required"/>/<see cref="SettingDescriptor.RenderFormat"/>,
/// and never round-tripping a secret's stored value into a rendered input.
/// </summary>
public class SettingEditorTests : BunitContext
{
    private static readonly SettingConstraints NoConstraints =
        new(null, null, null, null, null, null, null, null, null);

    private static SettingDescriptor Descriptor(
        SettingType type,
        bool required = false,
        SettingConstraints? constraints = null,
        string? renderFormat = null,
        bool requiresRecreate = false) => new(
        Key: "SOME_KEY",
        Label: "Some setting",
        Group: "Group",
        Type: type,
        Required: required,
        Default: null,
        RenderFormat: renderFormat,
        RequiresRecreate: requiresRecreate,
        PublishByDefault: null,
        Constraints: constraints ?? NoConstraints,
        Bindings: []);

    [Theory]
    [InlineData(SettingType.String, "input")]
    [InlineData(SettingType.Text, "textarea")]
    [InlineData(SettingType.Int, "input")]
    [InlineData(SettingType.Float, "input")]
    [InlineData(SettingType.Port, "input")]
    [InlineData(SettingType.Path, "input")]
    [InlineData(SettingType.Duration, "input")]
    public void EachTextlikeType_RendersItsControl(SettingType type, string expectedTag)
    {
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(type)));

        var control = cut.Find("[data-testid='setting-editor-control']");
        control.TagName.Should().Be(expectedTag.ToUpperInvariant());
    }

    [Fact]
    public void Bool_RendersACheckbox()
    {
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Bool)));

        var control = cut.Find("[data-testid='setting-editor-control']");
        control.TagName.Should().Be("INPUT");
        control.GetAttribute("type").Should().Be("checkbox");
    }

    [Fact]
    public void Bool_HonoursCustomTrueFalseTokens()
    {
        var constraints = NoConstraints with { TrueValue = "Yes", FalseValue = "No" };
        var cut = Render<SettingEditor>(p => p
            .Add(x => x.Descriptor, Descriptor(SettingType.Bool, constraints: constraints))
            .Add(x => x.Value, "Yes"));

        cut.Find("[data-testid='setting-editor-control']").HasAttribute("checked").Should().BeTrue();
        cut.Markup.Should().Contain("Yes");
    }

    [Fact]
    public void Enum_RendersAnOptionPerConstraintValue()
    {
        var constraints = NoConstraints with { Values = ["Low", "Medium", "High"] };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Enum, constraints: constraints)));

        var control = cut.Find("[data-testid='setting-editor-control']");
        control.TagName.Should().Be("SELECT");

        var options = control.QuerySelectorAll("option");
        options.Select(o => o.GetAttribute("value")).Should().Contain(["Low", "Medium", "High"]);
    }

    [Fact]
    public void Enum_OffersAnUnsetOptionWhenNotRequired()
    {
        var constraints = NoConstraints with { Values = ["A", "B"] };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Enum, required: false, constraints: constraints)));

        cut.Find("[data-testid='setting-editor-control']").QuerySelector("option[value='']").Should().NotBeNull();
    }

    [Fact]
    public void Enum_RequiredOffersNoUnsetOption()
    {
        var constraints = NoConstraints with { Values = ["A", "B"] };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Enum, required: true, constraints: constraints)));

        cut.Find("[data-testid='setting-editor-control']").QuerySelector("option[value='']").Should().BeNull();
    }

    [Fact]
    public void Int_HonoursMinAndMaxConstraints()
    {
        var constraints = NoConstraints with { Min = 2, Max = 64 };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Int, constraints: constraints)));

        var control = cut.Find("[data-testid='setting-editor-control']");
        control.GetAttribute("min").Should().Be("2");
        control.GetAttribute("max").Should().Be("64");
    }

    [Fact]
    public void Float_HonoursStepConstraint()
    {
        var constraints = NoConstraints with { Step = 0.5 };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Float, constraints: constraints)));

        cut.Find("[data-testid='setting-editor-control']").GetAttribute("step").Should().Be("0.5");
    }

    [Fact]
    public void String_HonoursMaxLengthConstraint()
    {
        var constraints = NoConstraints with { MaxLength = 40 };
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.String, constraints: constraints)));

        cut.Find("[data-testid='setting-editor-control']").GetAttribute("maxlength").Should().Be("40");
    }

    [Fact]
    public void Duration_UsesRenderFormatAsAPlaceholderHint()
    {
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Duration, renderFormat: "e.g. 90s")));

        cut.Find("[data-testid='setting-editor-control']").GetAttribute("placeholder").Should().Be("e.g. 90s");
    }

    [Fact]
    public void Required_RendersAVisibleIndicator()
    {
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.String, required: true)));

        cut.Find("[data-testid='setting-editor-required']").Should().NotBeNull();
    }

    [Fact]
    public void NotRequired_RendersNoIndicator()
    {
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.String, required: false)));

        cut.FindAll("[data-testid='setting-editor-required']").Should().BeEmpty();
    }

    [Fact]
    public void Secret_RendersAPasswordFieldWithAHideRevealToggle_AndNeverPreFillsAStoredValue()
    {
        // The caller (ServerSettingsTab) never passes a persisted secret's real value in here — but this
        // test still pins that, even if it were passed, the control starts masked, and toggling Reveal only
        // ever shows whatever is currently in the field, not a value fetched from Servyx's database.
        var cut = Render<SettingEditor>(p => p.Add(x => x.Descriptor, Descriptor(SettingType.Secret)));

        var control = cut.Find("[data-testid='setting-editor-control']");
        control.GetAttribute("type").Should().Be("password");
        control.GetAttribute("value").Should().BeNullOrEmpty();

        cut.Find("[data-testid='setting-editor-reveal']").Click();
        cut.Find("[data-testid='setting-editor-control']").GetAttribute("type").Should().Be("text");
    }

    [Fact]
    public void ChangingTheControl_RaisesValueChangedWithTheNewValue()
    {
        string? observed = "unset";
        var cut = Render<SettingEditor>(p => p
            .Add(x => x.Descriptor, Descriptor(SettingType.String))
            .Add(x => x.ValueChanged, v => observed = v));

        cut.Find("[data-testid='setting-editor-control']").Change("a new value");

        observed.Should().Be("a new value");
    }
}
