using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Layout;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Layout;

/// <summary>
/// The topbar badge that used to hardcode "READ-ONLY MODE — Milestone 1..." regardless of what write
/// access the process actually held. It must now reflect <see cref="WritableServers"/>: a read-only badge
/// when nothing is writable (including when the service was never registered at all), and an accurate
/// "writes enabled" badge the moment at least one server carries a grant.
/// </summary>
public class MainLayoutTests : BunitContext
{
    public MainLayoutTests()
    {
        Services.AddSingleton<IDashboardDataService, MockDashboardDataService>();

        // MainLayout mounts ThemeToggle, which reads the stored theme choice via JS on first render.
        JSInterop.Setup<string>("servyxTheme.read").SetResult("system");
    }

    private static RenderFragment Body => builder => builder.AddContent(0, "child content");

    [Fact]
    public void NothingWritable_ShowsReadOnlyBadge_NotWritableBadge()
    {
        Services.AddSingleton(WritableServers.None);

        var cut = Render<MainLayout>(parameters => parameters.Add(p => p.Body, Body));
        cut.WaitForAssertion(() => cut.FindAll(".svx-readonly-badge").Should().ContainSingle());

        cut.Find(".svx-readonly-badge").TextContent.Should().Contain("READ-ONLY MODE");
        cut.FindAll(".svx-writable-badge").Should().BeEmpty();
    }

    [Fact]
    public void WritableServersNeverRegistered_BehavesExactlyLikeNone()
    {
        var cut = Render<MainLayout>(parameters => parameters.Add(p => p.Body, Body));
        cut.WaitForAssertion(() => cut.FindAll(".svx-readonly-badge").Should().ContainSingle());

        cut.FindAll(".svx-writable-badge").Should().BeEmpty();
    }

    [Fact]
    public void AServerIsWritable_ShowsAccurateWritesEnabledBadge_NotReadOnlyBadge()
    {
        Services.AddSingleton(new WritableServers(["my-test-server"]));

        var cut = Render<MainLayout>(parameters => parameters.Add(p => p.Body, Body));
        cut.WaitForAssertion(() => cut.FindAll(".svx-writable-badge").Should().ContainSingle());

        var badge = cut.Find(".svx-writable-badge");
        badge.TextContent.Should().Contain("WRITES ENABLED");
        badge.GetAttribute("title").Should().Contain("Write access is enabled for 1 server");

        cut.FindAll(".svx-readonly-badge").Should().BeEmpty();
    }
}
