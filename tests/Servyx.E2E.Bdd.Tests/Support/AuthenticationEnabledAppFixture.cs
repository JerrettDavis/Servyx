using Servyx.E2E.Tests;

namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// A second, independent <see cref="ServyxAppProcess"/> — started with
/// <c>Servyx:Authentication:Enabled=true</c> — so the one scenario that needs to see the operator sign-in /
/// first-run page can do so without turning authentication on for every other scenario in the suite. Every
/// other scenario keeps running against <see cref="TestRunContext.Fixture"/>'s app, which is started with
/// authentication explicitly OFF (see <see cref="ServyxAppProcess.StartAsync"/>'s documented defaults) so the
/// dashboard is reachable with no sign-in preamble.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately self-contained rather than routed through <see cref="ScenarioHooks"/>'s existing
/// write-enabled-host tag dispatch: this fixture owns its own <see cref="Microsoft.Playwright.IBrowserContext"/>
/// and its own <see cref="Support.ScreenshotRecorder"/> instance (created and disposed by
/// <see cref="Steps.FirstRunSteps"/>), rather than the container-registered <c>IPage</c>/<c>ScreenshotRecorder"</c>
/// every other step class shares. That keeps this addition entirely additive — it does not change what any
/// existing scenario's <c>IPage</c> resolves to.
/// </para>
/// <para>
/// Reuses the shared Chromium instance from <see cref="TestRunContext.Fixture"/> — only the app process
/// differs, not the browser — for the same reason <see cref="WriteEnabledAppFixture"/> does: launching a
/// second Chromium instance for one scenario would be pure overhead.
/// </para>
/// </remarks>
public sealed class AuthenticationEnabledAppFixture : IAsyncDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EnvironmentOverrides = new Dictionary<string, string>
    {
        ["Servyx__Authentication__Enabled"] = "true",
    };

    public ServyxAppProcess App { get; } = new();

    public async Task InitializeAsync() =>
        await App.StartAsync(environmentOverrides: EnvironmentOverrides).ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await App.DisposeAsync().ConfigureAwait(false);
}
