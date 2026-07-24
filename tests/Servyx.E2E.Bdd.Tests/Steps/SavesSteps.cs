using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the server detail page's "Saves" tab (see ServerSavesTab.razor).</summary>
[Binding]
public sealed class SavesSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the world id ""(.*)"" is shown$")]
    public async Task ThenTheWorldIdIsShownAsync(string worldId)
    {
        await Expect(page.GetByText(worldId)).ToBeVisibleAsync();
        ledger.Record();
    }

    [Then(@"^the level file size ""(.*)"" is shown$")]
    public async Task ThenTheLevelFileSizeIsShownAsync(string size)
    {
        await Expect(page.GetByText(size)).ToBeVisibleAsync();
        ledger.Record();
    }

    [Then(@"^(\d+) player saves are listed$")]
    public async Task ThenPlayerSavesAreListedAsync(int expectedCount)
    {
        var rows = page.Locator("div[aria-label='Player save files']").Locator(".svx-row-link");
        await Expect(rows).ToHaveCountAsync(expectedCount);
        ledger.Record();
    }
}
