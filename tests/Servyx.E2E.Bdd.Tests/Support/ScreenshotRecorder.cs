using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// Stages full-page screenshots into a per-scenario TEMP directory during a scenario, then promotes them
/// into <c>&lt;repoRoot&gt;/docs/images/</c> only if the scenario actually passed. This is what makes the
/// captured screenshots a side-effect of a PASSING scenario rather than of merely running one: a failing
/// scenario leaves the real <c>docs/images/</c> directory untouched and its staged PNGs are discarded with
/// the temp directory.
/// </summary>
public sealed partial class ScreenshotRecorder
{
    private readonly IPage _page;
    private readonly DirectoryInfo _repoRoot;
    private readonly DirectoryInfo _stagingDir;
    private readonly List<(string Name, string StagedPath)> _staged = [];

    public ScreenshotRecorder(IPage page, DirectoryInfo repoRoot)
    {
        _page = page;
        _repoRoot = repoRoot;
        _stagingDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "servyx-e2e-bdd-screenshots", Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Captures a PNG into the scenario's staging directory under <paramref name="name"/>, recording the
    /// eventual <c>docs/images/&lt;name&gt;.png</c> destination. When <paramref name="scope"/> is
    /// <see langword="null"/> (the default) the whole page is captured full-page; when it is supplied,
    /// only that element is captured — used where two scenarios on the same page/tab would otherwise
    /// produce indistinguishable full-page images (e.g. two different rows of the same settings grid).
    /// The name is validated as a lowercase-kebab-case identifier — this is documentation-facing, fixed,
    /// stable naming, not a free-text label, so a malformed name is an application/test defect, not an
    /// environment problem, and fails the scenario outright rather than silently normalizing it.
    /// </summary>
    public async Task StageAsync(string name, ILocator? scope = null)
    {
        if (!NamePattern().IsMatch(name))
        {
            Assert.Fail(
                $"Screenshot name '{name}' does not match the required pattern ^[a-z0-9]+(-[a-z0-9]+)*$ " +
                "(lowercase kebab-case). Screenshot filenames are documentation-facing and fixed — fix the " +
                "name in the feature file rather than relaxing this check.");
            return;
        }

        var stagedPath = Path.Combine(_stagingDir.FullName, $"{name}.png");

        if (scope is null)
        {
            await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = stagedPath,
                FullPage = true,
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
            });
        }
        else
        {
            await CaptureElementScopedScreenshotAsync(scope, stagedPath);
        }

        _staged.Add((name, stagedPath));
    }

    /// <summary>
    /// Blazor Server prerenders this page's markup on first response, then — the moment its SignalR circuit
    /// connects and takes over — replaces the ENTIRE render tree with fresh DOM node instances carrying
    /// identical content (see <c>InteractiveRenderModeTests</c>' remarks on the two-pass render). Most
    /// element-scoped captures in this suite never observe that swap because some earlier step already
    /// forced a server round-trip — a tab click confirmed via <c>aria-selected</c>, a Locator
    /// <c>Expect(...)</c> assertion that only succeeds against the live circuit's output — so by the time
    /// they screenshot, the swap is long finished. A scenario that screenshots as its very first assertion
    /// after navigation (nothing but an instant, non-waiting theme check in between) has no such guarantee:
    /// under a loaded full-suite run, where the shared app process is juggling many concurrent circuits, the
    /// capture can land squarely inside the swap, and Playwright reports the element it just resolved as
    /// "not attached to the DOM".
    /// </summary>
    /// <remarks>
    /// The fix is a bounded retry, not a sleep: <paramref name="scope"/> is a live Playwright
    /// <see cref="ILocator"/>, not a cached element handle, so calling <see cref="ILocator.ScreenshotAsync"/>
    /// on it again re-resolves the selector against whatever DOM is current at that moment — including
    /// Playwright's own internal actionability wait (visible + stable) run fresh each attempt. A retry that
    /// lands after the interactive swap finishes captures the settled tree. If the element genuinely never
    /// attaches — a real defect, not this race — the final attempt's exception is left uncaught and fails
    /// the scenario, exactly as an unguarded call would have.
    /// </remarks>
    private static async Task CaptureElementScopedScreenshotAsync(ILocator scope, string stagedPath)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await scope.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = stagedPath,
                    Type = ScreenshotType.Png,
                    Animations = ScreenshotAnimations.Disabled,
                    Caret = ScreenshotCaret.Hide,
                });
                return;
            }
            catch (PlaywrightException ex) when (
                attempt < maxAttempts && ex.Message.Contains("not attached to the DOM"))
            {
                // Blazor swapped the node out from under us between its own actionability wait resolving
                // and the capture running. Loop straight back to a fresh ScreenshotAsync call — no delay
                // is inserted here on purpose: Playwright's own internal actionability polling inside the
                // next ScreenshotAsync call is the wait.
            }
        }
    }

    /// <summary>
    /// Called once per scenario, after every step has run. Only when <paramref name="passed"/> is
    /// <see langword="true"/> are the staged PNGs copied into the real <c>docs/images/</c> directory
    /// (created if necessary); either way, the temp staging directory is removed.
    /// </summary>
    public void FinishScenario(bool passed)
    {
        if (passed && _staged.Count > 0)
        {
            var imagesDir = Path.Combine(_repoRoot.FullName, "docs", "images");
            Directory.CreateDirectory(imagesDir);

            foreach (var (name, stagedPath) in _staged)
            {
                File.Copy(stagedPath, Path.Combine(imagesDir, $"{name}.png"), overwrite: true);
            }
        }

        try
        {
            Directory.Delete(_stagingDir.FullName, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a per-scenario temp directory; a stray temp folder left behind is
            // not an application defect worth failing an otherwise-passed scenario over.
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();
}
