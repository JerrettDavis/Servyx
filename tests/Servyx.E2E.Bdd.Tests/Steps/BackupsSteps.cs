using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions against both the server detail page's "Backups" tab (see ServerBackupsTab.razor) and the
/// estate-wide "/backups" overview page (see BackupsPage.razor) — both render foreign backups the same way.
/// </summary>
[Binding]
public sealed class BackupsSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^every backup on this server is labelled ""(.*)""$")]
    public async Task ThenEveryBackupOnThisServerIsLabelledAsync(string label)
    {
        var foreignBadges = page.Locator(".foreign-badge");
        await Expect(foreignBadges.First).ToBeVisibleAsync();
        await Expect(foreignBadges.First).ToContainTextAsync(label);
        (await foreignBadges.CountAsync()).Should().BeGreaterThan(0);
        ledger.Record();
    }

    [Then(@"^no delete, prune or restore control is present anywhere on the panel$")]
    public async Task ThenNoDeletePruneOrRestoreControlIsPresentAsync()
    {
        var panel = page.Locator("div[role='tabpanel']");
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Delete" }).CountAsync()).Should().Be(0);
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Prune" }).CountAsync()).Should().Be(0);
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Restore" }).CountAsync()).Should().Be(0);
        ledger.Record();
    }

    [Then(@"^(\d+) backups are listed, each showing its server, filename, created time and size$")]
    public async Task ThenNBackupsAreListedAsync(int expectedCount)
    {
        var rows = page.Locator("div[aria-label='Backups']").Locator(".svx-row-link");
        await Expect(rows).ToHaveCountAsync(expectedCount);

        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var row = rows.Nth(i);
            (await row.Locator("[data-col-label='Server']").InnerTextAsync()).Should().NotBeNullOrWhiteSpace();
            (await row.Locator("[data-col-label='File']").InnerTextAsync()).Should().Contain(".tar.gz");
            (await row.Locator("[data-col-label='Created']").InnerTextAsync()).Should().NotBeNullOrWhiteSpace();
            (await row.Locator("[data-col-label='Size']").InnerTextAsync()).Should().Contain("MB");
        }

        ledger.Record();
    }
}
