using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Shared;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Shared;

/// <summary>
/// The shared <c>&lt;PageTitle&gt;</c> wrapper every routed page now uses instead of hardcoding
/// <c>"... - Servyx"</c>, so a white-labelled deployment's page titles follow
/// <see cref="BrandingOptions.ProductName"/> without every page having to know that.
/// </summary>
/// <remarks>
/// <see cref="PageTitle"/> renders via Blazor's section-content mechanism rather than into the component's
/// own render tree — it is a placeholder for a <c>SectionOutlet</c> like <c>HeadOutlet</c> — so
/// <c>cut.Markup</c> is always empty for a component that only contains one. The child content is found and
/// rendered separately here, the way bUnit's own guidance for testing <c>SectionContent</c> components
/// describes.
/// </remarks>
public class SvxPageTitleTests : BunitContext
{
    private string RenderedTitleText(Bunit.IRenderedComponent<SvxPageTitle> cut)
    {
        var pageTitle = cut.FindComponent<PageTitle>();
        var content = Render(pageTitle.Instance.ChildContent!);
        return content.Markup.Trim();
    }

    [Fact]
    public void BrandingOptions_never_registered_renders_the_unconfigured_default_name()
    {
        var cut = Render<SvxPageTitle>(p => p.Add(x => x.Section, "Games"));

        RenderedTitleText(cut).Should().Be("Games - Servyx");
    }

    [Fact]
    public void Default_BrandingOptions_registered_renders_Servyx()
    {
        Services.AddSingleton(BrandingOptions.Default);

        var cut = Render<SvxPageTitle>(p => p.Add(x => x.Section, "Backups"));

        RenderedTitleText(cut).Should().Be("Backups - Servyx");
    }

    [Fact]
    public void A_configured_ProductName_replaces_Servyx_in_the_title()
    {
        Services.AddSingleton(new BrandingOptions { ProductName = "Acme Server Manager" });

        var cut = Render<SvxPageTitle>(p => p.Add(x => x.Section, "Deploy"));

        RenderedTitleText(cut).Should().Be("Deploy - Acme Server Manager");
    }
}
