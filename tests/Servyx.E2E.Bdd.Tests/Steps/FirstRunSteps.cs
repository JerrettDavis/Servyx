using Microsoft.Playwright;
using Reqnroll;
using Servyx.E2E.Bdd.Tests.Support;
using static Microsoft.Playwright.Assertions;

namespace Servyx.E2E.Bdd.Tests.Steps;

/// <summary>
/// Steps for <c>FirstRun.feature</c> — the one scenario in the suite that needs a Servyx host started with
/// authentication ON and no operator password ever set, to capture the sign-in / first-run page an operator
/// actually sees before reaching any dashboard.
/// </summary>
/// <remarks>
/// Deliberately self-contained: this class owns its own <see cref="IBrowserContext"/>, <see cref="IPage"/>
/// and <see cref="ScreenshotRecorder"/> instead of taking the ones <c>ScenarioHooks</c> registers into the
/// scenario container for every scenario (those stay pointed at the default, authentication-off app — see
/// <see cref="AuthenticationEnabledAppFixture"/>). That keeps this feature purely additive: no other step
/// class, hook, or scenario changes behavior because this one exists.
/// </remarks>
[Binding]
public sealed class FirstRunSteps(AssertionLedger ledger, ScenarioContext scenarioContext)
{
    // Lazily started on first use, exactly like TestRunContext's write-enabled fixture, so every run that
    // doesn't execute this scenario never pays for a second app process at all.
    private static AuthenticationEnabledAppFixture? _fixture;
    private static readonly SemaphoreSlim FixtureLock = new(1, 1);

    private IBrowserContext? _browserContext;
    private IPage? _page;
    private ScreenshotRecorder? _recorder;

    private static async Task<AuthenticationEnabledAppFixture> GetFixtureAsync()
    {
        if (_fixture is not null)
        {
            return _fixture;
        }

        await FixtureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_fixture is null)
            {
                var fixture = new AuthenticationEnabledAppFixture();
                await fixture.InitializeAsync().ConfigureAwait(false);
                _fixture = fixture;
            }
        }
        finally
        {
            FixtureLock.Release();
        }

        return _fixture;
    }

    [AfterTestRun]
    public static async Task DisposeFixtureIfStartedAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Given(@"^Servyx is running with authentication enabled and no operator password set$")]
    public async Task GivenServyxIsRunningWithAuthenticationEnabledAsync()
    {
        var fixture = await GetFixtureAsync().ConfigureAwait(false);
        var runContextFixture = TestRunContext.Fixture;

        Skip.IfNot(
            runContextFixture.BrowsersAvailable,
            $"Playwright's Chromium browser is not installed/available in this environment " +
            $"({runContextFixture.SkipReason}). This is an environment problem, not an application defect.");

        var dark = scenarioContext.ScenarioInfo.Tags.Contains(ThemedBrowserContextFactory.DarkTag);
        _browserContext = await ThemedBrowserContextFactory.CreateAsync(
            runContextFixture.Browser!, fixture.App.ServerAddress, dark);

        _page = await _browserContext.NewPageAsync().ConfigureAwait(false);
        _recorder = new ScreenshotRecorder(_page, RepoRoot.Find());
    }

    [When(@"^I visit Servyx for the first time$")]
    public async Task WhenIVisitServyxForTheFirstTimeAsync() => await Page.GotoAsync("/").ConfigureAwait(false);

    [Then(@"^I am redirected to the sign-in page$")]
    public async Task ThenIAmRedirectedToTheSignInPageAsync()
    {
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/login(\?.*)?$"));
        ledger.Record();
    }

    [Then(@"^the page asks me to set the first operator password$")]
    public async Task ThenThePageAsksMeToSetTheFirstOperatorPasswordAsync()
    {
        await Expect(Page.Locator("[data-testid='setup-lede']")).ToBeVisibleAsync();
        await Expect(Page.Locator("form[data-testid='setup-form']")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid='new-password']")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid='confirm-password']")).ToBeVisibleAsync();
        ledger.Record();
    }

    // Scoped to @login-first-run so this doesn't collide with NavigationSteps' identical, unscoped
    // "I capture the screen as ..." step (which stages via the shared, container-registered
    // ScreenshotRecorder — the wrong instance here, since this scenario has its own page/recorder). Reqnroll
    // prefers a tag-scoped match over an unscoped one for a scenario carrying that tag, so this step wins
    // for @login-first-run scenarios and every other scenario is unaffected.
    [Scope(Tag = "login-first-run")]
    [Then(@"^I capture the screen as ""(.*)""$")]
    public async Task ThenICaptureTheScreenAsAsync(string name)
    {
        await Recorder.StageAsync(name).ConfigureAwait(false);
        ledger.Record();
    }

    // Same scoping rationale as the capture step above: this scenario's page is this class's own _page, not
    // the container-registered IPage ThemeSteps reads, so the dark login scenario needs its own tag-scoped
    // copy of the guard rather than reusing ThemeSteps.ThenThePageIsInThemeAsync.
    [Scope(Tag = "login-first-run")]
    [Then(@"^the page is in ""(light|dark)"" theme$")]
    public async Task ThenThePageIsInThemeAsync(string expectedTheme)
    {
        var actual = await Page.EvaluateAsync<string>(
            "() => document.documentElement.getAttribute('data-theme')").ConfigureAwait(false);
        actual.Should().Be(expectedTheme);
        ledger.Record();
    }

    [AfterScenario]
    public void PromoteScreenshotAndCloseBrowserContext(ScenarioContext context)
    {
        // Mirrors ScenarioHooks.FinishScreenshots/CloseBrowserContextAsync exactly, but against this
        // scenario's own recorder/context — a no-op for every other scenario, since both fields stay null
        // unless this class's Given step actually ran.
        _recorder?.FinishScenario(passed: context.TestError is null);
        _browserContext?.CloseAsync().GetAwaiter().GetResult();
    }

    private IPage Page => _page ?? throw new InvalidOperationException(
        "The 'Given Servyx is running with authentication enabled...' step must run before any other FirstRunSteps step.");

    private ScreenshotRecorder Recorder => _recorder ?? throw new InvalidOperationException(
        "The 'Given Servyx is running with authentication enabled...' step must run before any other FirstRunSteps step.");
}
