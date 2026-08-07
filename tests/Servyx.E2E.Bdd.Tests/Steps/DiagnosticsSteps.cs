using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Assertions for the diagnostics-facing surfaces: the top bar's Docker connection status (see
/// <c>MainLayout.razor</c>) and the Console tab's "no RCON control channel configured" state (see
/// <c>ServerConsoleTab.razor</c>). Both are genuinely produced by the demonstration host — the mock reports
/// a real, transport-shaped probe detail for the connection status, and the demo host wires no RCON channel
/// for any server, so neither capture in this file fabricates a state the app cannot actually reach.
/// </summary>
[Binding]
public sealed class DiagnosticsSteps(IPage page, ScreenshotRecorder recorder, AssertionLedger ledger)
{
    [Then(@"^the connection status shows ""(.*)""$")]
    public async Task ThenTheConnectionStatusShowsAsync(string status)
    {
        var connection = page.Locator(".svx-connection");
        await Expect(connection).ToContainTextAsync($"Docker host: {status}");
        await Expect(connection).ToHaveClassAsync(new Regex($"svx-connection-{status.ToLowerInvariant()}"));
        ledger.Record();
    }

    [Then(@"^the connection tooltip reports the transport's own probe detail$")]
    public async Task ThenTheConnectionTooltipReportsProbeDetailAsync()
    {
        var title = await page.Locator(".svx-connection").GetAttributeAsync("title");
        title.Should().NotBeNullOrWhiteSpace();
        title.Should().Contain("Docker 27.3.1");
        ledger.Record();
    }

    [Then(@"^I capture the screen as ""(.*)"", focused on the connection status$")]
    public async Task ThenICaptureFocusedOnTheConnectionStatusAsync(string name)
    {
        // MainLayout resolves the real connection info asynchronously in OnInitializedAsync, so the
        // element Playwright first locates during Blazor Server's prerender pass can be replaced once the
        // interactive circuit's own render lands — the same "circuit not settled yet" race NavigationSteps
        // tolerates for tab selection. Retrying the element-scoped capture rides out that one extra render.
        await CaptureStableAsync(() => page.Locator(".svx-connection"), name);
    }

    [Then(@"^the console reports that no RCON control channel is configured for this server$")]
    public async Task ThenTheConsoleReportsNoRconChannelAsync()
    {
        var notice = page.Locator("[data-testid='console-no-channel']");
        await Expect(notice).ToBeVisibleAsync();
        await Expect(notice).ToContainTextAsync("No RCON control channel is configured for this server.");
        ledger.Record();
    }

    [Then(@"^I capture the screen as ""(.*)"", focused on the command panel$")]
    public async Task ThenICaptureFocusedOnTheCommandPanelAsync(string name)
    {
        await CaptureStableAsync(() => page.Locator("[data-testid='console-command-panel']"), name);
    }

    /// <summary>
    /// Stages an element-scoped screenshot, retrying a few times when Playwright reports the located
    /// element as no longer attached — the element resolved, then got replaced by a later Blazor Server
    /// render before the screenshot actually ran. Re-locating (via <paramref name="locate"/>) and trying
    /// again rides out that race instead of failing the scenario over a rendering timing artifact.
    /// </summary>
    private async Task CaptureStableAsync(Func<ILocator> locate, string name)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Expect(locate()).ToBeVisibleAsync();
                await recorder.StageAsync(name, locate());
                ledger.Record();
                return;
            }
            catch (PlaywrightException) when (attempt < maxAttempts)
            {
                await page.WaitForTimeoutAsync(200);
            }
        }
    }
}
