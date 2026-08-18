using Microsoft.Playwright;

namespace Servyx.E2E.Tests;

/// <summary>
/// Shared, once-per-test-run fixture owning the real Kestrel-hosted app (<see cref="App"/>) and a
/// headless Chromium instance (<see cref="Browser"/>). Playwright's browser binaries are a heavyweight,
/// separately-installed prerequisite (see <c>docs/testing.md</c>) that may simply not be present in a
/// given environment (offline CI runner, a dev box that never ran <c>playwright install</c>, etc). Rather
/// than letting every E2E scenario fail with a confusing "executable doesn't exist" error in that case,
/// this fixture attempts the launch exactly once, records why it failed if it did, and every scenario
/// checks <see cref="BrowsersAvailable"/> as its first line and skips itself cleanly — the suite stays
/// green either way. See docs/testing.md for exactly what was observed when this was authored.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public ServyxAppProcess App { get; } = new();

    public IPlaywright? Playwright { get; private set; }

    public IBrowser? Browser { get; private set; }

    /// <summary>Whether a headless Chromium instance was successfully launched.</summary>
    public bool BrowsersAvailable { get; private set; }

    /// <summary>Human-readable explanation, populated only when <see cref="BrowsersAvailable"/> is false.</summary>
    public string? SkipReason { get; private set; }

    private string? _dbDirectory;

    public async Task InitializeAsync()
    {
        // Every ServyxAppProcess this suite launches shares the SAME Servyx.Web.dll and therefore the SAME
        // AppContext.BaseDirectory (see ServyxAppProcess.LocateServyxWebDll) — so without an explicit
        // override here, this fixture's app, WriteEnabledAppFixture's, and AuthenticationEnabledAppFixture's
        // all default to the identical on-disk SQLite file (see ServyxCoreCompositionExtensions' own
        // fallback: "{AppContext.BaseDirectory}/servyx-data/servyx.db"), and a user account created against
        // one is silently visible to the others. Isolating this fixture's database is what makes it safe for
        // BDD scenarios to create a real operator account (see AdminSessionSteps) without leaking it into
        // FirstRun.feature's own, separately-configured "no account exists yet" fixture.
        _dbDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "servyx-e2e-db", Guid.NewGuid().ToString("N"))).FullName;

        // Starting the real app is not the optional/best-effort part — if this throws, it's a genuine
        // failure (e.g. Servyx.Web isn't built), so it's allowed to fail the whole run rather than being
        // caught and turned into a skip.
        await App.StartAsync(environmentOverrides: new Dictionary<string, string>
        {
            ["Servyx__Persistence__ConnectionString"] = $"Data Source={Path.Combine(_dbDirectory, "servyx.db")}",
        });

        try
        {
            Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            BrowsersAvailable = true;
        }
        catch (Exception ex)
        {
            BrowsersAvailable = false;
            SkipReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        Playwright?.Dispose();
        await App.DisposeAsync();

        if (_dbDirectory is not null)
        {
            try
            {
                Directory.Delete(_dbDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a per-run temp directory; a stray temp folder left behind is not
                // worth failing teardown over — matches WriteEnabledAppFixture's identical handling.
            }
        }
    }
}

/// <summary>Groups every E2E test class onto one shared <see cref="PlaywrightFixture"/> (one browser, one app host for the whole run).</summary>
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "Servyx E2E";
}
