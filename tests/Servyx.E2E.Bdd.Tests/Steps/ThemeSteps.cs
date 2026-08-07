using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Asserts the resolved theme actually landed on <c>&lt;html data-theme="..."&gt;</c> (see
/// <c>wwwroot/js/servyx-theme.js</c>). This exists specifically to guard <see cref="ThemedBrowserContextFactory"/>'s
/// localStorage seeding: <c>Theming.feature</c>'s dark scenarios are otherwise indistinguishable, by the
/// documentation integrity tests, from a scenario whose seeding silently failed and captured a light-rendered
/// page under a "-dark" name — those tests only ever compare capture NAMES, never pixels. Used unscoped here
/// for every scenario that shares the container-registered <see cref="IPage"/>; see
/// <see cref="FirstRunSteps"/> for the <c>@login-first-run</c>-scoped duplicate against that scenario's own,
/// separately-owned page.
/// </summary>
[Binding]
public sealed class ThemeSteps(IPage page, AssertionLedger ledger)
{
    [Then(@"^the page is in ""(light|dark)"" theme$")]
    public async Task ThenThePageIsInThemeAsync(string expectedTheme)
    {
        var actual = await page.EvaluateAsync<string>(
            "() => document.documentElement.getAttribute('data-theme')");
        actual.Should().Be(expectedTheme);
        ledger.Record();
    }
}
