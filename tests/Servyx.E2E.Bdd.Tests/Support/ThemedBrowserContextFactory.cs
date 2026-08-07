using Microsoft.Playwright;

namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// The single place a Playwright <see cref="IBrowserContext"/> gets constructed for this suite. Both
/// <see cref="ScenarioHooks"/> (every ordinary scenario) and <see cref="Steps.FirstRunSteps"/> (the one
/// scenario that runs against its own, authentication-enabled app process) route through this factory rather
/// than constructing <see cref="BrowserNewContextOptions"/> themselves, so the two call sites cannot drift
/// apart on viewport, scaling, or — the reason this factory exists at all — how a scenario's theme is forced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why seeding localStorage, not just <see cref="BrowserNewContextOptions.ColorScheme"/>.</b> Servyx's
/// theme bootstrap (see <c>wwwroot/js/servyx-theme.js</c> and the inline &lt;head&gt; script duplicated in
/// <c>App.razor</c>/<c>LoginPage.razor</c>) resolves <c>data-theme</c> from an explicitly stored
/// <c>svx-theme</c> preference first, and only falls back to <c>prefers-color-scheme</c> when nothing is
/// stored. There is deliberately no <c>@media (prefers-color-scheme)</c> rule in the CSS itself. Setting
/// <see cref="ColorScheme"/> alone therefore only works on a context with no stored preference — which is
/// never true here, since <see cref="CreateAsync"/> always seeds one — so the explicit
/// <c>localStorage.setItem('svx-theme', ...)</c> init script is what actually determines the rendered theme,
/// deterministically, before Servyx's own bootstrap script ever runs.
/// </para>
/// </remarks>
public static class ThemedBrowserContextFactory
{
    /// <summary>The Reqnroll scenario tag that selects the dark variant.</summary>
    public const string DarkTag = "dark";

    public static async Task<IBrowserContext> CreateAsync(IBrowser browser, string baseUrl, bool dark)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl,
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            DeviceScaleFactor = 1,
            ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,
            Locale = "en-US",
            TimezoneId = "UTC",
        });

        // Runs before any page script on every document/navigation in this context — including the very
        // first one — so Servyx's own before-first-paint bootstrap always sees a stored preference rather
        // than racing it.
        await context.AddInitScriptAsync(
            $"window.localStorage.setItem('svx-theme', '{(dark ? "dark" : "light")}');");

        return context;
    }
}
