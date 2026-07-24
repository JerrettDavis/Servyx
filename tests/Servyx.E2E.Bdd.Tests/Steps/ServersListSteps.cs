using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the "/servers" list page (see ServersList.razor).</summary>
[Binding]
public sealed class ServersListSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the server ""(.*)"" is listed for game ""(.*)""$")]
    public async Task ThenTheServerIsListedForGameAsync(string serverName, string game)
    {
        var row = page.Locator("a.svx-row-link", new PageLocatorOptions { HasTextString = serverName });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-col-label='Game']")).ToHaveTextAsync(game);
        ledger.Record();
    }

    [Then(@"^its state is shown as ""(.*)"" and its health as ""(.*)""$")]
    public async Task ThenItsStateAndHealthAreShownAsAsync(string state, string health)
    {
        var stateBadge = page.Locator(".state-badge");
        var healthBadge = page.Locator(".health-badge");

        await Expect(stateBadge).ToContainTextAsync(state);
        await Expect(healthBadge).ToContainTextAsync(health);
        await Expect(stateBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($"state-{state.ToLowerInvariant()}"));
        await Expect(healthBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($"health-{health.ToLowerInvariant()}"));
        ledger.Record();
    }

    [Then(@"^its players are shown as ""(.*)""$")]
    public async Task ThenItsPlayersAreShownAsAsync(string players)
    {
        await Expect(page.Locator("[data-col-label='Players']")).ToHaveTextAsync(players);
        ledger.Record();
    }

    [Then(@"^its uptime is shown$")]
    public async Task ThenItsUptimeIsShownAsync()
    {
        var uptime = await page.Locator("[data-col-label='Uptime']").InnerTextAsync();
        uptime.Should().NotBeNullOrWhiteSpace();
        uptime.Should().NotBe("—");
        ledger.Record();
    }

    [Then(@"^its published ports ""(.*)"" and ""(.*)"" are listed$")]
    public async Task ThenItsPublishedPortsAreListedAsync(string firstPort, string secondPort)
    {
        var portsCell = page.Locator("[data-col-label='Ports']");
        await Expect(portsCell).ToContainTextAsync(firstPort);
        await Expect(portsCell).ToContainTextAsync(secondPort);
        await Expect(portsCell.Locator(".port-published")).ToHaveCountAsync(2);
        ledger.Record();
    }
}
