using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Navigation and assertions for the screens that had no scenario anywhere in the suite before this task:
/// the four still-placeholder sidebar pages (Mods, Plugins, Users, application-level Settings — see
/// <c>ModsPage.razor</c>, <c>PluginsPage.razor</c>, <c>UsersPage.razor</c>, <c>AppSettingsPage.razor</c>) and
/// the Not Found page (<c>NotFound.razor</c>, wired as the Router's <c>NotFoundPage</c> in
/// <c>Routes.razor</c> and also independently routable at its own <c>/not-found</c> address). Used by both
/// <c>Coverage.feature</c> (the light captures) and <c>Theming.feature</c> (the dark twins), exactly like
/// every other Steps class in this suite is shared between the pre-existing light features and the new
/// dark-tagged scenarios.
/// </summary>
[Binding]
public sealed class CoverageSteps(IPage page, AssertionLedger ledger)
{
    [When(@"^I open the mods page$")]
    public async Task WhenIOpenTheModsPageAsync() => await page.GotoAsync("mods");

    [When(@"^I open the plugins page$")]
    public async Task WhenIOpenThePluginsPageAsync() => await page.GotoAsync("plugins");

    [When(@"^I open the users page$")]
    public async Task WhenIOpenTheUsersPageAsync() => await page.GotoAsync("users");

    // "the app settings page" rather than plain "the settings page" — this suite already has a distinct,
    // established phrase for a SERVER's own Settings tab ("I open the "Settings" tab", see NavigationSteps),
    // and this is the separate, application-wide /settings page (see AppSettingsPage.razor).
    [When(@"^I open the app settings page$")]
    public async Task WhenIOpenTheAppSettingsPageAsync() => await page.GotoAsync("settings");

    [When(@"^I open a page that does not exist$")]
    public async Task WhenIOpenAPageThatDoesNotExistAsync() => await page.GotoAsync("not-found");

    // Error.razor (Components/Pages/Error.razor) carries its own "@page "/Error"" directive, so it is
    // directly routable like any other page — no thrown exception or UseExceptionHandler re-execution is
    // needed to reach it. RequestId is populated from Activity.Current/HttpContext.TraceIdentifier, both of
    // which are always present on a real HTTP request, so "Request ID:" renders for an operator hitting this
    // URL directly exactly as it would for a real unhandled-exception redirect in production.
    [When(@"^I navigate directly to the error page$")]
    public async Task WhenINavigateDirectlyToTheErrorPageAsync() => await page.GotoAsync("Error");

    /// <summary>
    /// Each of the five pages this class navigates to renders exactly one &lt;h3&gt; — either
    /// <c>.svx-empty-state h3</c> (Mods/Plugins/Users/Settings) or a bare one (NotFound.razor's "Not Found")
    /// — so one locator works for all of them without needing a per-page step.
    /// </summary>
    [Then(@"^the page heading reads ""(.*)""$")]
    public async Task ThenThePageHeadingReadsAsync(string heading)
    {
        await Expect(page.Locator("h3")).ToHaveTextAsync(heading);
        ledger.Record();
    }

    [Then(@"^the error page reports a request id$")]
    public async Task ThenTheErrorPageReportsARequestIdAsync()
    {
        await Expect(page.Locator("h1.text-danger")).ToHaveTextAsync("Error.");
        await Expect(page.GetByText("Request ID:")).ToBeVisibleAsync();
        ledger.Record();
    }
}
