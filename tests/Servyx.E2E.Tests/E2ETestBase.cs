using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Servyx.E2E.Tests;

/// <summary>
/// Common per-test setup for browser-driven scenarios: a fresh, isolated <see cref="IBrowserContext"/>
/// and <see cref="Page"/> per test method (never shared — cookies/localStorage/SignalR circuits must not
/// leak between scenarios), reusing the one shared browser/app host from <see cref="PlaywrightFixture"/>.
/// </summary>
[Collection(E2ECollection.Name)]
public abstract class E2ETestBase(PlaywrightFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private IBrowserContext? _context;

    protected PlaywrightFixture Fixture { get; } = fixture;

    protected IPage Page { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!Fixture.BrowsersAvailable)
        {
            return;
        }

        _context = await Fixture.Browser!.NewContextAsync(new BrowserNewContextOptions { BaseURL = Fixture.App.ServerAddress });
        Page = await _context.NewPageAsync();
        Page.Console += (_, msg) => output.WriteLine($"[browser console:{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => output.WriteLine($"[browser page error] {err}");
    }

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
        }
    }

    /// <summary>Writes a diagnostic line to the test's output (visible with <c>--logger "console;verbosity=detailed"</c>).</summary>
    protected void Log(string message) => output.WriteLine(message);

    /// <summary>
    /// Call as the first line of every <c>[SkippableFact]</c>. Issues a genuine xUnit <c>Skip</c> (not a
    /// silent pass) when Playwright's Chromium binary is not available in this environment — an
    /// environment problem, not an application defect, so it must be reported as SKIPPED rather than
    /// PASSED or FAILED.
    /// </summary>
    protected void SkipIfBrowsersUnavailable()
    {
        Skip.IfNot(
            Fixture.BrowsersAvailable,
            $"Playwright's Chromium browser is not installed/available in this environment " +
            $"({Fixture.SkipReason}). Run `pwsh bin/Debug/net10.0/playwright.ps1 install chromium` in " +
            $"tests/Servyx.E2E.Tests and re-run to execute this scenario for real.");
    }
}
