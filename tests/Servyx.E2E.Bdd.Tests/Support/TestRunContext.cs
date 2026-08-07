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

    // Started lazily, on the first scenario that actually needs a write-enabled host (see ScenarioHooks),
    // rather than unconditionally in BeforeTestRunAsync: every other scenario in the suite must be provably
    // unaffected by this fixture existing, and the simplest proof is that most runs never start it at all.
    private static WriteEnabledAppFixture? _writeEnabledFixture;
    private static readonly SemaphoreSlim WriteEnabledFixtureLock = new(1, 1);

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

        if (_writeEnabledFixture is not null)
        {
            await _writeEnabledFixture.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns the write-enabled fixture, starting it (its second <see cref="ServyxAppProcess"/>, and its
    /// Docker stub container — see <see cref="WriteEnabledAppFixture"/>) on first use. Safe to call
    /// concurrently — Reqnroll can execute scenarios in parallel — via the same
    /// double-checked-locking-over-a-semaphore shape, so it is started exactly once regardless of how many
    /// scenarios request it.
    /// </summary>
    public static async Task<WriteEnabledAppFixture> GetWriteEnabledFixtureAsync()
    {
        if (_writeEnabledFixture is not null)
        {
            return _writeEnabledFixture;
        }

        await WriteEnabledFixtureLock.WaitAsync();
        try
        {
            if (_writeEnabledFixture is null)
            {
                var fixture = new WriteEnabledAppFixture();
                await fixture.InitializeAsync();
                _writeEnabledFixture = fixture;
            }
        }
        finally
        {
            WriteEnabledFixtureLock.Release();
        }

        return _writeEnabledFixture;
    }
}
