using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>Assertions against the server detail page's "Overview" tab (see ServerOverviewTab.razor).</summary>
[Binding]
public sealed class ServerOverviewSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the state badge shows ""(.*)""$")]
    public async Task ThenTheStateBadgeShowsAsync(string state)
    {
        var stateBadge = page.Locator(".state-badge");
        await Expect(stateBadge).ToBeVisibleAsync();
        await Expect(stateBadge).ToContainTextAsync(state);
        await Expect(stateBadge).ToHaveClassAsync(new Regex($"state-{state.ToLowerInvariant()}"));
        ledger.Record();
    }

    [Then(@"^the health badge shows ""(.*)""$")]
    public async Task ThenTheHealthBadgeShowsAsync(string health)
    {
        var healthBadge = page.Locator(".health-badge");
        await Expect(healthBadge).ToBeVisibleAsync();
        await Expect(healthBadge).ToContainTextAsync(health);
        await Expect(healthBadge).ToHaveClassAsync(new Regex($"health-{health.ToLowerInvariant()}"));
        ledger.Record();
    }

    [Then(@"^the state and health badges are distinct elements$")]
    public async Task ThenTheStateAndHealthBadgesAreDistinctElementsAsync()
    {
        var stateHandle = await page.Locator(".state-badge").ElementHandleAsync();
        var healthHandle = await page.Locator(".health-badge").ElementHandleAsync();
        stateHandle.Should().NotBe(healthHandle);
        ledger.Record();
    }

    [Then(@"^the power controls ""(.*)"", ""(.*)"", ""(.*)"" and ""(.*)"" are all present and disabled$")]
    public async Task ThenThePowerControlsAreAllPresentAndDisabledAsync(string a, string b, string c, string d)
    {
        var powerButtons = page.Locator("[data-testid='gated-button']");
        await Expect(powerButtons).ToHaveCountAsync(4);
        var count = await powerButtons.CountAsync();

        // Matched by exact label text (not Playwright's substring HasText), since "Start" is itself a
        // substring of "Restart" and a naive HasText filter resolves both as a strict-mode violation.
        foreach (var label in new[] { a, b, c, d })
        {
            var matchIndex = -1;
            for (var i = 0; i < count; i++)
            {
                var text = (await powerButtons.Nth(i).Locator(".gated-button-text").InnerTextAsync()).Trim();
                if (text == label)
                {
                    matchIndex = i;
                    break;
                }
            }

            matchIndex.Should().BeGreaterThanOrEqualTo(0, $"expected a power control labelled exactly '{label}'");
            await Expect(powerButtons.Nth(matchIndex)).ToBeDisabledAsync();
        }

        ledger.Record();
    }

    [Then(@"^each disabled power control explains it is because of read-only mode$")]
    public async Task ThenEachDisabledPowerControlExplainsReadOnlyModeAsync()
    {
        var powerButtons = page.Locator("[data-testid='gated-button']");
        var count = await powerButtons.CountAsync();
        count.Should().Be(4);

        for (var i = 0; i < count; i++)
        {
            var title = await powerButtons.Nth(i).GetAttributeAsync("title");
            title.Should().Contain("read-only mode");
        }

        ledger.Record();
    }

    /// <summary>
    /// The <c>WriteMode.Enabled</c> counterpart of <see cref="ThenThePowerControlsAreAllPresentAndDisabledAsync"/>:
    /// the same four controls, none of them locked. Only checks the rendered, clickable state of each
    /// control — see the feature file's safety note for why nothing here ever clicks one.
    /// </summary>
    [Then(@"^the power controls ""(.*)"", ""(.*)"", ""(.*)"" and ""(.*)"" are all present and enabled$")]
    public async Task ThenThePowerControlsAreAllPresentAndEnabledAsync(string a, string b, string c, string d)
    {
        var powerButtons = page.Locator("[data-testid='gated-button']");
        await Expect(powerButtons).ToHaveCountAsync(4);
        var count = await powerButtons.CountAsync();

        foreach (var label in new[] { a, b, c, d })
        {
            var matchIndex = -1;
            for (var i = 0; i < count; i++)
            {
                var text = (await powerButtons.Nth(i).Locator(".gated-button-text").InnerTextAsync()).Trim();
                if (text == label)
                {
                    matchIndex = i;
                    break;
                }
            }

            matchIndex.Should().BeGreaterThanOrEqualTo(0, $"expected a power control labelled exactly '{label}'");
            await Expect(powerButtons.Nth(matchIndex)).ToBeEnabledAsync();
        }

        ledger.Record();
    }

    /// <summary>
    /// Asserts <c>WriteMode.PreviewOnly</c>'s Power card: the ordered stop-escalation ladder, and — the
    /// point of the whole state — no power control of any kind, not even a locked one (see
    /// ServerOverviewTab.razor's own remarks on why offering even a disabled button here would be
    /// misleading).
    /// </summary>
    [Then(@"^the stop-escalation ladder is shown in order, with no power controls present$")]
    public async Task ThenTheStopEscalationLadderIsShownWithNoPowerControlsAsync()
    {
        await Expect(page.Locator("[data-testid='gated-button']")).ToHaveCountAsync(0);

        var stages = page.Locator("[data-testid='lifecycle-stop-stage']");
        await Expect(stages).ToHaveCountAsync(4);

        var expectedSubstrings = new[] { "RCON 'shutdown'", "RCON 'doexit'", "Signal SIGINT", "Force kill" };
        for (var i = 0; i < expectedSubstrings.Length; i++)
        {
            await Expect(stages.Nth(i)).ToContainTextAsync(expectedSubstrings[i]);
        }

        ledger.Record();
    }
}
