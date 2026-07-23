using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Layout;

namespace Servyx.Web.Tests.Layout;

public class NavMenuTests : BunitContext
{
    private static readonly (string Label, string Href)[] ExpectedEntries =
    [
        ("Dashboard", ""),
        ("Servers", "servers"),
        ("Games", "games"),
        ("Backups", "backups"),
        ("Mods", "mods"),
        ("Plugins", "plugins"),
        ("Settings", "settings"),
        ("Users", "users"),
        ("Audit", "audit"),
    ];

    [Fact]
    public void RendersAllNineNavEntries_WithCorrectHrefs()
    {
        var cut = Render<NavMenu>();

        var links = cut.FindAll("a.svx-nav-link");
        links.Should().HaveCount(9);

        foreach (var (_, href) in ExpectedEntries)
        {
            cut.FindAll($"a[href='{href}']").Should().ContainSingle(
                because: $"the sidebar must link to \"{href}\"");
        }
    }

    [Fact]
    public void ActiveRoute_SetsAriaCurrentPage()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("servers");

        var cut = Render<NavMenu>();

        var activeLink = cut.Find("a[href='servers']");
        activeLink.GetAttribute("aria-current").Should().Be("page");

        var inactiveLink = cut.Find("a[href='games']");
        inactiveLink.HasAttribute("aria-current").Should().BeFalse();
    }
}
