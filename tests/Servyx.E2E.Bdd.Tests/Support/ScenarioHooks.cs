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

    /// <summary>
    /// A scenario tagged <c>@write-enabled-host</c> runs against <see cref="TestRunContext.GetWriteEnabledFixtureAsync"/>
    /// instead of the default, always-provisioning-closed <see cref="TestRunContext.Fixture"/> app — see
    /// <see cref="WriteEnabledAppFixture"/> for exactly what it grants and why every other scenario is
    /// provably unaffected by it existing.
    /// </summary>
    private const string WriteEnabledHostTag = "write-enabled-host";

    /// <summary>
    /// A scenario tagged <c>@requires-docker</c> additionally needs <see cref="WriteEnabledAppFixture"/>'s
    /// real Docker stub container (see its own remarks for why) to have provisioned successfully. Only the
    /// <c>Enabled</c>-controls scenario carries this tag — the <c>PreviewOnly</c> one never touches a
    /// container's lifecycle, so it stays runnable even where Docker is unavailable.
    /// </summary>
    private const string RequiresDockerTag = "requires-docker";

    [BeforeScenario(Order = 5)]
    public async Task SkipIfDockerStubUnavailableAsync(ScenarioContext scenarioContext)
    {
        if (!scenarioContext.ScenarioInfo.Tags.Contains(RequiresDockerTag))
        {
            return;
        }

        var writeEnabledFixture = await TestRunContext.GetWriteEnabledFixtureAsync();
        Skip.IfNot(
            writeEnabledFixture.DockerAvailable,
            $"This scenario needs a real Docker daemon to provision its stub container " +
            $"({writeEnabledFixture.DockerSkipReason}). This is an environment problem, not an application defect.");
    }

    [BeforeScenario(Order = 10)]
    public async Task CreatePerScenarioBrowserContextAsync(ScenarioContext scenarioContext)
    {
        var fixture = TestRunContext.Fixture;

        var baseUrl = scenarioContext.ScenarioInfo.Tags.Contains(WriteEnabledHostTag)
            ? (await TestRunContext.GetWriteEnabledFixtureAsync()).App.ServerAddress
            : fixture.App.ServerAddress;

        // CombinedTags, not Tags: Theming.feature declares "@dark" once at the FEATURE level rather than
        // repeating it on all 25 scenarios (see that file's header comment), and ScenarioInfo.Tags is
        // documented as "direct tags of the scenario" only — it does not inherit feature-level tags.
        // CombinedTags does ("tags inherited from the feature and the rule"), so it is the one that must be
        // checked here. Using Tags silently built every Theming.feature scenario as a LIGHT context.
        var dark = scenarioContext.ScenarioInfo.CombinedTags.Contains(ThemedBrowserContextFactory.DarkTag);
        _browserContext = await ThemedBrowserContextFactory.CreateAsync(fixture.Browser!, baseUrl, dark);

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
