using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Tests;

/// <summary>
/// Real-browser scenarios for a remote (ssh+docker) server appearing alongside the local one, against the
/// Blazor Server app running with <c>Servyx:DataSource=Mock</c> (see <see cref="ServyxAppProcess"/>).
/// Until now every E2E scenario only ever saw the single seeded local server ("Palygondwanaland") — these
/// add the first browser-driven coverage of a remote-hosted server, its <c>ssh+docker</c> host label, its
/// unhealthy-but-actually-fine status, and that its lifecycle controls stay disabled just like the local
/// server's, per <see cref="DashboardE2ETests.EveryPowerControl_IsDisabled_AndExplainsWhy"/>'s pattern.
/// </summary>
[Trait("Category", "e2e")]
public sealed class RemoteHostE2ETests(PlaywrightFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    private const string LocalServerId = "palygondwanaland";
    private const string LocalServerName = "Palygondwanaland";
    private const string RemoteServerId = "example-remote-palworld";
    private const string RemoteServerName = "Example Remote Palworld";

    [SkippableFact]
    public async Task Servers_page_lists_both_the_local_and_remote_servers()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync("/servers");

        await Expect(Page.Locator($"a.svx-row-link[href='servers/{LocalServerId}']")).ToContainTextAsync(LocalServerName);
        await Expect(Page.Locator($"a.svx-row-link[href='servers/{RemoteServerId}']")).ToContainTextAsync(RemoteServerName);

        var rows = Page.Locator("a.svx-row-link");
        await Expect(rows).ToHaveCountAsync(2);

        // The remote row is labelled with the transport it was discovered over, distinguishing it from
        // the local Docker daemon row.
        var remoteRow = Page.Locator($"a.svx-row-link[href='servers/{RemoteServerId}']");
        await Expect(remoteRow.Locator("[data-col-label='Host']")).ToHaveTextAsync("ssh+docker");
    }

    [SkippableFact]
    public async Task Remote_server_detail_page_opens_and_shows_the_unhealthy_explanation()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{RemoteServerId}");

        await Expect(Page.Locator("h2")).ToContainTextAsync(RemoteServerName);

        var healthBadge = Page.Locator(".health-badge");
        await Expect(healthBadge).ToBeVisibleAsync();
        await Expect(healthBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("health-unhealthy"));
        await Expect(healthBadge).ToContainTextAsync("Unhealthy");

        // The false-negative explanation (the container's own HEALTHCHECK gets 401 Unauthorized while the
        // Palworld server itself is healthy) reaches the DOM via the badge's title attribute.
        var title = await healthBadge.GetAttributeAsync("title");
        title.Should().Contain("401 Unauthorized");
        title.Should().Contain("Palworld server itself is healthy");
    }

    [SkippableFact]
    public async Task Lifecycle_controls_are_disabled_on_the_remote_server()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{RemoteServerId}");

        var powerButtons = Page.Locator("[data-testid='gated-button']");
        await Expect(powerButtons).ToHaveCountAsync(4); // Start, Restart, Stop, Kill

        var count = await powerButtons.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var button = powerButtons.Nth(i);
            await Expect(button).ToBeDisabledAsync();
            var title = await button.GetAttributeAsync("title");
            title.Should().Contain("read-only mode");
        }
    }
}
