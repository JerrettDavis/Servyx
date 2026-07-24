using Reqnroll;
using Servyx.E2E.Tests;

namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// Owns the ONE <see cref="PlaywrightFixture"/> for the entire Reqnroll test run: it starts the real
/// Servyx.Web subprocess and launches Chromium exactly once, in <see cref="BeforeTestRunAsync"/>, and tears
/// both down exactly once, in <see cref="AfterTestRunAsync"/> — never per scenario. Every scenario (see
/// <see cref="ScenarioHooks"/>) only ever creates a fresh, isolated <see cref="Microsoft.Playwright.IBrowserContext"/>
/// against this one shared browser/app host.
/// </summary>
[Binding]
public static class TestRunContext
{
    /// <summary>The single shared app-process + browser fixture for the whole test run.</summary>
    public static PlaywrightFixture Fixture { get; private set; } = null!;

    [BeforeTestRun]
    public static async Task BeforeTestRunAsync()
    {
        Fixture = new PlaywrightFixture();
        await Fixture.InitializeAsync();
    }

    [AfterTestRun]
    public static async Task AfterTestRunAsync()
    {
        await Fixture.DisposeAsync();
    }
}
