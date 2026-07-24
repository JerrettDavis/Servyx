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
            await scope.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = stagedPath,
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
            });
        }

        _staged.Add((name, stagedPath));
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
