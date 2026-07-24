// Project rule for this whole suite: NO helper may return, continue, or log-and-proceed on a failed
// condition. Every guard resolves to exactly one of two things:
//   - Skip.IfNot / Skip.If  -> an ENVIRONMENT problem (e.g. Chromium isn't installed). The scenario is
//     reported as SKIPPED, never as a silent pass.
//   - Assert.Fail           -> an APPLICATION defect (the thing under test is actually broken). The
//     scenario is reported as FAILED, never swallowed.
// If you are tempted to add a helper that returns a bool/null for the caller to "maybe" check, don't:
// make it throw one of the two ways above instead.

using Microsoft.Playwright;
using Reqnroll;
using Reqnroll.BoDi;

namespace Servyx.E2E.Bdd.Tests.Support;

[Binding]
public sealed class ScenarioHooks(IObjectContainer container)
{
    private IBrowserContext? _browserContext;
    private ScreenshotRecorder? _recorder;

    [BeforeScenario(Order = 0)]
    public void SkipIfBrowsersUnavailable()
    {
        var fixture = TestRunContext.Fixture;
        Skip.IfNot(
            fixture.BrowsersAvailable,
            $"Playwright's Chromium browser is not installed/available in this environment " +
            $"({fixture.SkipReason}). This is an environment problem, not an application defect.");
    }

    [BeforeScenario(Order = 10)]
    public async Task CreatePerScenarioBrowserContextAsync()
    {
        var fixture = TestRunContext.Fixture;

        _browserContext = await fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = fixture.App.ServerAddress,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            DeviceScaleFactor = 1,
            ColorScheme = ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,
            Locale = "en-US",
            TimezoneId = "UTC",
        });

        var page = await _browserContext.NewPageAsync();
        _recorder = new ScreenshotRecorder(page, RepoRoot.Find());
        var ledger = new AssertionLedger();

        container.RegisterInstanceAs(page);
        container.RegisterInstanceAs(_recorder);
        container.RegisterInstanceAs(ledger);
    }

    [AfterScenario(Order = 0)]
    public void FailIfNothingWasAsserted(ScenarioContext scenarioContext, AssertionLedger ledger)
    {
        if (scenarioContext.TestError is null && ledger.Count == 0)
        {
            Assert.Fail(
                "This scenario is about to be reported as PASSED but recorded zero assertions — every " +
                "[Then] step must call AssertionLedger.Record(). A scenario that asserts nothing proves " +
                "nothing, regardless of how green it looks.");
        }
    }

    [AfterScenario(Order = 10)]
    public void FinishScreenshots(ScenarioContext scenarioContext)
    {
        _recorder?.FinishScenario(passed: scenarioContext.TestError is null);
    }

    [AfterScenario(Order = 20)]
    public async Task CloseBrowserContextAsync()
    {
        if (_browserContext is not null)
        {
            await _browserContext.CloseAsync();
        }
    }
}
