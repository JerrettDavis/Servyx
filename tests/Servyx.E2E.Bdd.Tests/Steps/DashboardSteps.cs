using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the "/" dashboard page (see Home.razor).</summary>
[Binding]
public sealed class DashboardSteps(IPage page, AssertionLedger ledger)
{
    /// <summary>
    /// The sidebar's 10 nav entries, in order — see NavCatalog.Entries. Deploy is deliberately excluded: it
    /// is always appended by NavCatalog.EntriesFor, but on this provisioning-closed demo host it renders as
    /// a disabled &lt;button&gt;, not an &lt;a class="svx-nav-link"&gt;, per NavMenu.razor.
    /// </summary>
    private static readonly string[] SidebarLabels =
        ["Dashboard", "Servers", "Hosts", "Games", "Backups", "Mods", "Plugins", "Settings", "Users", "Audit"];

    [Then(@"^the ""(.*)"" tile shows ""(.*)""$")]
    public async Task ThenTheTileShowsAsync(string title, string value)
    {
        var card = page.Locator(".stat-card", new PageLocatorOptions { HasTextString = title });
        await Expect(card.Locator(".stat-card-title")).ToHaveTextAsync(title);
        await Expect(card.Locator(".stat-card-value")).ToHaveTextAsync(value);
        ledger.Record();
    }

    [Then(@"^all 10 sidebar entries are reachable$")]
    public async Task ThenAll10SidebarEntriesAreReachableAsync()
    {
        var navLinks = page.Locator("a.svx-nav-link");
        await Expect(navLinks).ToHaveCountAsync(10);

        foreach (var label in SidebarLabels)
        {
            var link = page.Locator($"a.svx-nav-link[title='{label}']");
            await Expect(link).ToHaveCountAsync(1);
            (await link.GetAttributeAsync("href")).Should().NotBeNull();
        }

        ledger.Record();
    }
}
