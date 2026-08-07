using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions specific to the seeded <c>ssh+docker</c> remote host ("Example Remote Palworld", id
/// "example-remote-palworld" — see <see cref="Servyx.Web.Services.MockDashboardDataService"/>): the servers
/// list's Host column, and the Overview tab's port-publication, mount, and network facts. Each "the port ..."
/// / "the mount ..." / "the network ..." step re-locates its own target rather than depending on a previously
/// located row from another Steps class, since <see cref="ServersListSteps"/>'s row-scoping field is private
/// to that binding instance.
/// </summary>
[Binding]
public sealed class RemoteHostSteps(IPage page, ScreenshotRecorder recorder, AssertionLedger ledger)
{
    [When(@"^I open the server detail page with id ""(.*)""$")]
    public async Task WhenIOpenTheServerDetailPageWithIdAsync(string id) => await page.GotoAsync($"servers/{id}");

    [Then(@"^the server ""(.*)"" has host ""(.*)""$")]
    public async Task ThenTheServerHasHostAsync(string serverName, string host)
    {
        var row = page.Locator("a.svx-row-link", new PageLocatorOptions { HasTextString = serverName });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-col-label='Host']")).ToHaveTextAsync(host);
        ledger.Record();
    }

    [Then(@"^the port ""(.*)"" is shown as published to host$")]
    public async Task ThenThePortIsShownAsPublishedAsync(string portLabel)
    {
        var row = PortRow(portLabel);
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-col-label='Published']")).ToContainTextAsync("Published to host");
        await Expect(row.Locator("[data-col-label='Published'] .port-published")).ToHaveCountAsync(1);
        ledger.Record();
    }

    [Then(@"^the port ""(.*)"" is shown as not published to host$")]
    public async Task ThenThePortIsShownAsNotPublishedAsync(string portLabel)
    {
        var row = PortRow(portLabel);
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-col-label='Published']")).ToContainTextAsync("Not published to host");
        await Expect(row.Locator("[data-col-label='Published'] .port-published")).ToHaveCountAsync(0);
        ledger.Record();
    }

    [Then(@"^the mount ""(.*)"" maps to ""(.*)""$")]
    public async Task ThenTheMountMapsToAsync(string hostPath, string containerPath)
    {
        var mountRow = page.Locator(".svx-dl div", new PageLocatorOptions { HasTextString = "Mount" });
        await Expect(mountRow).ToContainTextAsync(hostPath);
        await Expect(mountRow).ToContainTextAsync(containerPath);
        ledger.Record();
    }

    [Then(@"^the network is shown as ""(.*)""$")]
    public async Task ThenTheNetworkIsShownAsAsync(string network)
    {
        var networkRow = page.Locator(".svx-dl div", new PageLocatorOptions { HasTextString = "Network" });
        await Expect(networkRow).ToContainTextAsync(network);
        ledger.Record();
    }

    [Then(@"^the health badge's tooltip explains the false-negative health signal$")]
    public async Task ThenTheHealthBadgeTooltipExplainsFalseNegativeAsync()
    {
        var badge = page.Locator(".health-badge");
        var title = await badge.GetAttributeAsync("title");
        title.Should().NotBeNullOrWhiteSpace();
        title.Should().Contain("401 Unauthorized");
        title.Should().Contain("Servyx derives readiness from its own authenticated detectors");
        ledger.Record();
    }

    [Then(@"^I capture the screen as ""(.*)"", focused on the status card$")]
    public async Task ThenICaptureFocusedOnTheStatusCardAsync(string name)
    {
        var statusCard = page.Locator(".svx-card", new PageLocatorOptions { HasTextString = "Status" });
        await recorder.StageAsync(name, statusCard);
        ledger.Record();
    }

    private ILocator PortRow(string portLabel) =>
        page.Locator("div[aria-label='Ports'] .svx-row-link", new PageLocatorOptions { HasTextString = portLabel });
}
