using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Tests;

/// <summary>
/// Real-browser scenarios against the Blazor Server app running with <c>Servyx:DataSource=Mock</c> (see
/// <see cref="ServyxAppProcess"/> for how it is hosted, and <see cref="E2ETestBase"/> for why every
/// scenario opens with <see cref="E2ETestBase.SkipIfBrowsersUnavailable"/>). Waits use Playwright's
/// auto-waiting <c>Locator</c>/<c>Expect</c> assertions against elements that only exist post-render —
/// never a fixed <c>Task.Delay</c>, and never <c>WaitUntil.NetworkIdle</c>, which Blazor Server's
/// persistent SignalR WebSocket makes permanently unreliable (the network is never "idle").
/// </summary>
[Trait("Category", "e2e")]
public sealed class DashboardE2ETests(PlaywrightFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    private const string SeededServerId = "palygondwanaland";
    private const string SeededServerName = "Palygondwanaland";

    /// <summary>
    /// Clicks a detail-page tab by its accessible id and confirms it actually became the selected tab,
    /// tolerating a couple of retries in case a click lands in the narrow window before the SignalR
    /// circuit finishes connecting (the tab buttons are DOM-actionable from the very first,
    /// static-prerendered paint, before their <c>@@onclick</c> handler is wired up server-side).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> once the tab is confirmed selected. <see langword="false"/> if it never
    /// switches within the retry window — this should not happen now that <c>App.razor</c> applies
    /// <c>@@rendermode InteractiveServer</c> to both <c>HeadOutlet</c> and <c>Routes</c>, and
    /// <c>Program.cs</c> maps <c>.AddInteractiveServerRenderMode()</c> accordingly, so every
    /// <c>@@onclick</c> handler — including these tab switchers — is wired up server-side. If this ever
    /// returns <see langword="false"/> again, treat it as a genuine interactivity regression (see
    /// <see cref="FailTabNeverSwitched"/>), not a test flake.
    /// </returns>
    private async Task<bool> ClickTabAsync(string tabName)
    {
        var tab = Page.Locator($"#tab-{tabName}");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await tab.ClickAsync();
            try
            {
                await Expect(tab).ToHaveAttributeAsync("aria-selected", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 750 });
                return true;
            }
            catch (PlaywrightException)
            {
                // Not selected yet — retry, tolerating the circuit-not-connected-yet race.
            }
        }

        return false;
    }

    /// <summary>
    /// Fails the calling scenario loudly when tab-switching isn't functional (see
    /// <see cref="ClickTabAsync"/>). Unlike missing browsers, a tab that never switches is not an
    /// environment problem — it means server-side interactivity is broken — so this must never be a skip
    /// or a silent pass.
    /// </summary>
    private static void FailTabNeverSwitched(string tabName) =>
        Assert.Fail($"The '{tabName}' tab never became selected after repeated clicks — interactivity is not working.");

    [SkippableFact]
    public async Task Dashboard_SidebarShowsAllNineNavigationEntries()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync("/");

        var navLinks = Page.Locator("a.svx-nav-link");
        await Expect(navLinks).ToHaveCountAsync(9);
    }

    [SkippableFact]
    public async Task NavigatingToServers_MarksItCurrent_AndShowsTheSeededServer()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync("/");
        await Page.Locator("a.svx-nav-link[href='servers']").ClickAsync();

        await Expect(Page.Locator("a.svx-nav-link[href='servers']")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.Locator($"a.svx-row-link[href='servers/{SeededServerId}']")).ToContainTextAsync(SeededServerName);
    }

    [SkippableFact]
    public async Task ServerDetail_RendersStateAndHealthAsSeparateBadges()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{SeededServerId}");

        var stateBadge = Page.Locator(".state-badge");
        var healthBadge = Page.Locator(".health-badge");

        await Expect(stateBadge).ToBeVisibleAsync();
        await Expect(healthBadge).ToBeVisibleAsync();
        // Two distinct DOM elements, never one element trying to carry both signals.
        (await stateBadge.ElementHandleAsync()).Should().NotBe(await healthBadge.ElementHandleAsync());
        await Expect(stateBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("state-running"));
        await Expect(healthBadge).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("health-unhealthy"));
    }

    [SkippableFact]
    public async Task SettingsTab_ShowsFourColumns_DriftBadgeOnTheDriftedSetting_AndMaskedSecrets()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{SeededServerId}");
        if (!await ClickTabAsync("Settings"))
        {
            FailTabNeverSwitched("Settings");
        }

        var playersRow = Page.Locator("div.settings-row[data-setting-key='PLAYERS']");
        await Expect(playersRow.Locator("[data-col-label='Desired']")).ToBeVisibleAsync();
        await Expect(playersRow.Locator("[data-col-label='Authoritative (.env)']")).ToBeVisibleAsync();
        await Expect(playersRow.Locator("[data-col-label='Rendered (INI)']")).ToBeVisibleAsync();
        await Expect(playersRow.Locator("[data-col-label='Runtime']")).ToBeVisibleAsync();

        // PLAYERS is the one seeded setting whose authoritative/rendered values disagree.
        await Expect(playersRow.Locator(".drift-present")).ToBeVisibleAsync();

        var passwordRow = Page.Locator("div.settings-row[data-setting-key='ADMIN_PASSWORD']");
        await Expect(passwordRow.Locator("input")).ToHaveAttributeAsync("type", "password");
        await Expect(passwordRow.Locator("[data-col-label='Authoritative (.env)']")).ToContainTextAsync("********");
    }

    [SkippableFact]
    public async Task EveryPowerControl_IsDisabled_AndExplainsWhy()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{SeededServerId}");

        var powerButtons = Page.Locator("[data-testid='gated-button']");
        await Expect(powerButtons).ToHaveCountAsync(4); // Start, Restart, Stop, Kill

        var count = await powerButtons.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var button = powerButtons.Nth(i);
            await Expect(button).ToBeDisabledAsync();
            var title = await button.GetAttributeAsync("title");
            title.Should().Contain("read-only mode");
        }
    }

    [SkippableFact]
    public async Task BackupsTab_ListsForeignBackups_WithNoDestructiveControlPresent()
    {
        SkipIfBrowsersUnavailable();

        await Page.GotoAsync($"/servers/{SeededServerId}");
        if (!await ClickTabAsync("Backups"))
        {
            FailTabNeverSwitched("Backups");
        }

        var foreignBadges = Page.Locator(".foreign-badge");
        await Expect(foreignBadges.First).ToBeVisibleAsync();
        (await foreignBadges.CountAsync()).Should().BeGreaterThan(0);

        // No delete/prune/restore control anywhere on the panel — not even a disabled one — for a
        // backup Servyx does not own.
        var panel = Page.Locator("div[role='tabpanel']");
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Delete" }).CountAsync()).Should().Be(0);
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Prune" }).CountAsync()).Should().Be(0);
        (await panel.Locator("button", new LocatorLocatorOptions { HasTextString = "Restore" }).CountAsync()).Should().Be(0);
    }
}
