using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the "/servers" list page (see ServersList.razor).</summary>
/// <remarks>
/// All "its ..." steps assert against the specific server row located by the preceding "the server ... is
/// listed for game ..." step, not the page as a whole. The mock estate seeds two servers side by side (see
/// <see cref="Servyx.Web.Services.MockDashboardDataService"/>), so an unscoped, page-wide locator like
/// <c>.state-badge</c> resolves to more than one element and trips Playwright's strict-mode check.
/// </remarks>
[Binding]
public sealed class ServersListSteps(IPage page, AssertionLedger ledger)
{
    private ILocator? _row;

    [Then(@"^the server ""(.*)"" is listed for game ""(.*)""$")]
    public async Task ThenTheServerIsListedForGameAsync(string serverName, string game)
    {
        var row = page.Locator("a.svx-row-link", new PageLocatorOptions { HasTextString = serverName });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.Locator("[data-col-label='Game']")).ToHaveTextAsync(game);
        _row = row;
        ledger.Record();
    }

    [Then(@"^its state is shown as ""(.*)"" and its health as ""(.*)""$")]
    public async Task ThenItsStateAndHealthAreShownAsAsync(string state, string health)
    {
        var stateBadge = Row().Locator(".state-badge");
        var healthBadge = Row().Locator(".health-badge");

        await Expect(stateBadge).ToContainTextAsync(state);
        await Expect(healthBadge).ToContainTextAsync(health);
        await Expect(stateBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($"state-{state.ToLowerInvariant()}"));
        await Expect(healthBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex($"health-{health.ToLowerInvariant()}"));
        ledger.Record();
    }

    [Then(@"^its players are shown as ""(.*)""$")]
    public async Task ThenItsPlayersAreShownAsAsync(string players)
    {
        await Expect(Row().Locator("[data-col-label='Players']")).ToHaveTextAsync(players);
        ledger.Record();
    }

    [Then(@"^its uptime is shown$")]
    public async Task ThenItsUptimeIsShownAsync()
    {
        var uptime = await Row().Locator("[data-col-label='Uptime']").InnerTextAsync();
        uptime.Should().NotBeNullOrWhiteSpace();
        uptime.Should().NotBe("—");
        ledger.Record();
    }

    [Then(@"^its published ports ""(.*)"" and ""(.*)"" are listed$")]
    public async Task ThenItsPublishedPortsAreListedAsync(string firstPort, string secondPort)
    {
        var portsCell = Row().Locator("[data-col-label='Ports']");
        await Expect(portsCell).ToContainTextAsync(firstPort);
        await Expect(portsCell).ToContainTextAsync(secondPort);
        await Expect(portsCell.Locator(".port-published")).ToHaveCountAsync(2);
        ledger.Record();
    }

    private ILocator Row() => _row ?? throw new InvalidOperationException(
        "No server row has been located yet — a preceding 'the server \"...\" is listed for game \"...\"' " +
        "step must run first in this scenario.");
}
