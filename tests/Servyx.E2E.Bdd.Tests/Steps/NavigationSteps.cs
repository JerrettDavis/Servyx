using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Navigation between pages/tabs, expressed as relative paths (an <c>IBrowserContext.BaseURL</c> is set
/// per scenario in <see cref="ScenarioHooks"/>, so every navigation here is relative), plus the shared
/// screenshot-capture step used by every scenario.
/// </summary>
[Binding]
public sealed class NavigationSteps(IPage page, ScreenshotRecorder recorder, AssertionLedger ledger)
{
    /// <summary>
    /// Maps a seeded demo server's display name to its route id. Both mock servers happen to derive their
    /// id from their display name the same way — lowercased, spaces replaced with hyphens
    /// ("Example Remote Palworld" -&gt; "example-remote-palworld") — see <c>MockDashboardDataService</c>.
    /// </summary>
    private static string ServerIdFor(string serverName) => serverName.ToLowerInvariant().Replace(' ', '-');

    [Given(@"^Servyx is running against the demonstration host$")]
    public async Task GivenServyxIsRunningAsync()
    {
        // A readiness check, not a business assertion: confirms the shared app+browser fixture (started
        // once for the whole run, see TestRunContext) actually responds before any scenario proceeds.
        await page.GotoAsync("/");
        await Expect(page.Locator("nav.svx-nav")).ToBeVisibleAsync();
    }

    /// <summary>
    /// The <c>@write-enabled-host</c> counterpart of <see cref="GivenServyxIsRunningAsync"/> — same
    /// readiness check, against the second app process <see cref="ScenarioHooks"/> points this scenario's
    /// browser context at (see <see cref="WriteEnabledAppFixture"/>). A distinct step, not a reused one, so
    /// the feature file names out loud which host a scenario is running against.
    /// </summary>
    [Given(@"^Servyx is running with provisioning enabled and per-server write grants$")]
    public async Task GivenServyxIsRunningWithWriteGrantsAsync()
    {
        await page.GotoAsync("/");
        await Expect(page.Locator("nav.svx-nav")).ToBeVisibleAsync();
    }

    [When(@"^I open the dashboard$")]
    public async Task WhenIOpenTheDashboardAsync() => await page.GotoAsync("/");

    [When(@"^I open the servers list$")]
    public async Task WhenIOpenTheServersListAsync() => await page.GotoAsync("servers");

    [When(@"^I open the server detail page for ""(.*)""$")]
    public async Task WhenIOpenTheServerDetailPageForAsync(string serverName) =>
        await page.GotoAsync($"servers/{ServerIdFor(serverName)}");

    [When(@"^I open the backups overview page$")]
    public async Task WhenIOpenTheBackupsOverviewPageAsync() => await page.GotoAsync("backups");

    [When(@"^I open the games page$")]
    public async Task WhenIOpenTheGamesPageAsync() => await page.GotoAsync("games");

    [When(@"^I open the deploy page$")]
    public async Task WhenIOpenTheDeployPageAsync() => await page.GotoAsync("deploy");

    [When(@"^I open the audit page$")]
    public async Task WhenIOpenTheAuditPageAsync() => await page.GotoAsync("audit");

    [When(@"^I open the ""(.*)"" tab$")]
    public async Task WhenIOpenTheTabAsync(string tabName)
    {
        var tab = page.Locator($"#tab-{tabName}");
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            await tab.ClickAsync();
            try
            {
                await Expect(tab).ToHaveAttributeAsync(
                    "aria-selected", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 750 });
                return;
            }
            catch (PlaywrightException)
            {
                // Not selected yet — retry, tolerating the circuit-not-connected-yet race (see the
                // identical rationale on the original ClickTabAsync in DashboardE2ETests).
            }
        }

        // Unlike the ported original, this never hands a bool back for a caller to (potentially) ignore:
        // exhausting the retry window is an application defect — server-side interactivity not working —
        // so it fails the scenario outright.
        Assert.Fail($"The '{tabName}' tab never became selected after repeated clicks — interactivity is not working.");
    }

    [Then(@"^I capture the screen as ""(.*)""$")]
    public async Task ThenICaptureTheScreenAsAsync(string name)
    {
        await recorder.StageAsync(name);
        ledger.Record();
    }

    /// <summary>
    /// A narrower variant of the capture step for the handful of scenarios that share a page/tab with a
    /// sibling scenario: without scoping to a specific element, both would produce byte-identical
    /// full-page screenshots that fail to illustrate their own distinct concept.
    /// </summary>
    [Then(@"^I capture the screen as ""(.*)"", focused on the masked setting row$")]
    public async Task ThenICaptureTheScreenFocusedOnTheMaskedSettingRowAsync(string name)
    {
        var maskedRow = page.Locator("div.settings-row[data-setting-key='ADMIN_PASSWORD']");
        await recorder.StageAsync(name, maskedRow);
        ledger.Record();
    }

    [Then(@"^I capture the screen as ""(.*)"", focused on the power controls$")]
    public async Task ThenICaptureTheScreenFocusedOnThePowerControlsAsync(string name)
    {
        // The Overview tab's first card is always the Power card (Start/Restart/Stop/Kill).
        var powerCard = page.Locator(".svx-card").First;
        await recorder.StageAsync(name, powerCard);
        ledger.Record();
    }
}
