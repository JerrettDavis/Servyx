using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the "/games" catalogue page (see GamesPage.razor).</summary>
[Binding]
public sealed class GamesSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the ""(.*)"" game lists (\d+) deployment profiles$")]
    public async Task ThenTheGameListsNDeploymentProfilesAsync(string gameName, int expectedCount)
    {
        var card = page.Locator(".game-card", new PageLocatorOptions { HasTextString = gameName });
        await Expect(card.Locator("h3")).ToHaveTextAsync(gameName);
        await Expect(card.Locator(".deployment-profile-list li")).ToHaveCountAsync(expectedCount);
        ledger.Record();
    }
}
