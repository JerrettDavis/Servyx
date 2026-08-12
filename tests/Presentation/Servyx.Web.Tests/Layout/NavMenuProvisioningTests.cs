using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Layout;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Layout;

/// <summary>
/// The sidebar's Deploy entry is the visible half of the provisioning gate. Per the README invariant —
/// "every mutating control is VISIBLE BUT LOCKED until you explicitly enable writes" — Deploy must always
/// appear in the sidebar; a closed gate may only lock it, never remove it.
/// </summary>
public class NavMenuProvisioningTests : BunitContext
{
    /// <summary>
    /// Regression guard for the old, defective behaviour this test used to encode: a closed gate removed the
    /// Deploy entry from the sidebar entirely, which hid the single most important write surface exactly when
    /// a new user needed to learn it exists. Deploy must now render — as a locked, non-navigating control,
    /// not a live link.
    /// </summary>
    [Fact]
    public void GateClosed_RendersDeployEntryLockedNotHidden()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));

        var cut = Render<NavMenu>();

        // The ten read-only routes are unaffected: still live links, same count as before.
        cut.FindAll("a.svx-nav-link").Should().HaveCount(10);
        cut.FindAll("a[href='deploy']").Should().BeEmpty();

        // Deploy is present, just not as a live link.
        var locked = cut.Find("[data-testid=nav-link-locked-deploy]");
        locked.TagName.Should().Be("BUTTON");
        locked.HasAttribute("disabled").Should().BeTrue();
        locked.TextContent.Should().Contain("Deploy");
        locked.GetAttribute("title").Should().Contain("Servyx:Provisioning:Enabled");
        locked.QuerySelectorAll("svg").Should().HaveCountGreaterThanOrEqualTo(2); // the entry icon and the lock icon
    }

    [Fact]
    public void GateNotRegisteredAtAll_BehavesExactlyLikeAClosedGate()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("a.svx-nav-link").Should().HaveCount(10);
        cut.FindAll("a[href='deploy']").Should().BeEmpty();

        var locked = cut.Find("[data-testid=nav-link-locked-deploy]");
        locked.HasAttribute("disabled").Should().BeTrue();
        locked.GetAttribute("title").Should().Contain("Servyx:Provisioning:Enabled");
    }

    [Fact]
    public void GateOpen_AddsTheDeployEntry()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: true));

        var cut = Render<NavMenu>();

        cut.FindAll("a.svx-nav-link").Should().HaveCount(11);
        cut.FindAll("a[href='deploy']").Should().ContainSingle();

        // Live, not locked: no disabled locked-entry button anywhere in the sidebar.
        cut.FindAll("[data-testid=nav-link-locked-deploy]").Should().BeEmpty();
    }

    /// <summary>
    /// The locked entry is a disabled native <c>&lt;button&gt;</c> with no <c>@onclick</c> at all — not an
    /// <c>&lt;a&gt;</c>, and not a live element with a handler that merely checks the gate. bUnit cannot even
    /// simulate a click on it, which is the strongest available proof that no code path here reaches
    /// <c>NavigationManager</c>.
    /// </summary>
    [Fact]
    public void LockedDeployEntry_HasNoClickHandler_SoItCannotNavigate()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));
        var nav = Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;

        var cut = Render<NavMenu>();
        var locked = cut.Find("[data-testid=nav-link-locked-deploy]");

        locked.TagName.Should().Be("BUTTON");
        locked.HasAttribute("href").Should().BeFalse();

        var act = () => locked.Click();
        act.Should().Throw<MissingEventHandlerException>();

        // Nothing moved, because nothing could have.
        nav.Uri.Should().Be(before);
    }

    [Fact]
    public void DeployRoute_HasATitle_ForTheTopBar()
        => NavCatalog.TitleFor("deploy").Should().Be("Deploy");
}
