using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Layout;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Layout;

/// <summary>
/// The sidebar's Deploy entry is the visible half of the provisioning gate. When the gate is closed — the
/// default, and the only configuration a read-only host has — the sidebar must be indistinguishable from
/// the one it rendered before provisioning existed.
/// </summary>
public class NavMenuProvisioningTests : BunitContext
{
    [Fact]
    public void GateClosed_RendersNoDeployEntry()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));

        var cut = Render<NavMenu>();

        cut.FindAll("a.svx-nav-link").Should().HaveCount(9);
        cut.FindAll("a[href='deploy']").Should().BeEmpty();
    }

    [Fact]
    public void GateNotRegisteredAtAll_BehavesExactlyLikeAClosedGate()
    {
        var cut = Render<NavMenu>();

        cut.FindAll("a.svx-nav-link").Should().HaveCount(9);
        cut.FindAll("a[href='deploy']").Should().BeEmpty();
    }

    [Fact]
    public void GateOpen_AddsTheDeployEntry()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: true));

        var cut = Render<NavMenu>();

        cut.FindAll("a.svx-nav-link").Should().HaveCount(10);
        cut.FindAll("a[href='deploy']").Should().ContainSingle();
    }

    [Fact]
    public void DeployRoute_HasATitle_ForTheTopBar()
        => NavCatalog.TitleFor("deploy").Should().Be("Deploy");
}
