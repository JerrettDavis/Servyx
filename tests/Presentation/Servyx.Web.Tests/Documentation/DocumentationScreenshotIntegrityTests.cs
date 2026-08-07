using System.Text.RegularExpressions;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Enforces that three independently-authored things about Servyx's screenshot-illustrated user guides never
/// drift apart:
/// <list type="bullet">
/// <item><description><b>Referenced</b> — every <c>../images/name.png</c> markdown image reference inside
/// <c>docs\user-guide\*.md</c>.</description></item>
/// <item><description><b>Committed</b> — every <c>*.png</c> actually checked into <c>docs\images\</c>.</description></item>
/// <item><description><b>Captured</b> — every <c>I capture the screen as "name"</c> step declared in
/// <c>tests\Servyx.E2E.Bdd.Tests\Features\*.feature</c>, the Reqnroll/Playwright scenarios that produce those
/// PNGs.</description></item>
/// </list>
/// Before this suite existed, nothing enforced that these three sets agreed — a guide could reference an image
/// nobody captures, a screenshot could go stale with no scenario left regenerating it, or a captured name could
/// have no guide pointing at it at all. This is pure file I/O over the checked-in repository; it needs no
/// browser, no Docker, and no Playwright, so it can run in CI on every push.
/// </summary>
public sealed class DocumentationScreenshotIntegrityTests
{
    private static readonly Regex ReferencedImagePattern =
        new(@"!\[[^\]]*\]\(\.\./images/([a-z0-9-]+)\.png\)", RegexOptions.Compiled);

    private static readonly Regex CapturedNamePattern =
        new("I capture the screen as \"([a-z0-9-]+)\"", RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot => RepoRootLocator.Find();

    private static DirectoryInfo UserGuideDir =>
        new(Path.Combine(RepoRoot.FullName, "docs", "user-guide"));

    private static DirectoryInfo ImagesDir =>
        new(Path.Combine(RepoRoot.FullName, "docs", "images"));

    private static DirectoryInfo FeaturesDir =>
        new(Path.Combine(RepoRoot.FullName, "tests", "Servyx.E2E.Bdd.Tests", "Features"));

    /// <summary>Image name (no extension) -> the guide file name(s) that reference it.</summary>
    private static Dictionary<string, List<string>> GetReferencedImages()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in UserGuideDir.GetFiles("*.md"))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match match in ReferencedImagePattern.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!map.TryGetValue(name, out var files))
                {
                    map[name] = files = [];
                }

                files.Add(file.Name);
            }
        }

        return map;
    }

    /// <summary>Image name (no extension) -> the committed PNG file name.</summary>
    private static Dictionary<string, string> GetCommittedScreenshots() =>
        ImagesDir.GetFiles("*.png")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), f => f.Name, StringComparer.Ordinal);

    /// <summary>Captured name -> the feature file name(s) that declare a capture step for it.</summary>
    private static Dictionary<string, List<string>> GetCapturedNames()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in FeaturesDir.GetFiles("*.feature"))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match match in CapturedNamePattern.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!map.TryGetValue(name, out var files))
                {
                    map[name] = files = [];
                }

                files.Add(file.Name);
            }
        }

        return map;
    }

    [Fact]
    public void Every_image_referenced_by_a_guide_exists_on_disk()
    {
        var referenced = GetReferencedImages();
        var committed = GetCommittedScreenshots();

        referenced.Should().NotBeEmpty("the guide scan should find real ![...](../images/x.png) references " +
                                        "— an empty set here means the glob or regex is broken, not that " +
                                        "the guides are illustration-free");
        committed.Should().NotBeEmpty("docs\\images should contain committed screenshots");

        var missing = referenced.Keys.Where(name => !committed.ContainsKey(name)).ToList();

        var detail = string.Join("; ", missing.Select(name =>
            $"'{name}.png' (referenced by {string.Join(", ", referenced[name])})"));
        missing.Should().BeEmpty(
            because: $"the following image(s) are referenced by a guide but do not exist in docs\\images: {detail}");
    }

    [Fact]
    public void Every_committed_screenshot_is_referenced_by_at_least_one_guide()
    {
        var referenced = GetReferencedImages();
        var committed = GetCommittedScreenshots();

        referenced.Should().NotBeEmpty();
        committed.Should().NotBeEmpty();

        var orphaned = committed.Keys.Where(name => !referenced.ContainsKey(name)).ToList();

        var detail = string.Join("; ", orphaned.Select(name => $"'{committed[name]}'"));
        orphaned.Should().BeEmpty(
            because: $"the following committed screenshot(s) are not referenced by any guide in docs\\user-guide: {detail}");
    }

    [Fact]
    public void Every_captured_name_in_a_feature_file_has_a_committed_png()
    {
        var captured = GetCapturedNames();
        var committed = GetCommittedScreenshots();

        captured.Should().NotBeEmpty("the feature-file scan should find real capture steps — an empty set " +
                                      "here means the glob or regex is broken, not that no scenario captures " +
                                      "screenshots");
        committed.Should().NotBeEmpty();

        var missing = captured.Keys.Where(name => !committed.ContainsKey(name)).ToList();

        var detail = string.Join("; ", missing.Select(name =>
            $"'{name}' (declared in {string.Join(", ", captured[name])})"));
        missing.Should().BeEmpty(
            because: $"the following capture step name(s) have no committed docs\\images png: {detail}");
    }

    [Fact]
    public void Every_committed_png_is_produced_by_a_feature_file_capture_step()
    {
        var captured = GetCapturedNames();
        var committed = GetCommittedScreenshots();

        captured.Should().NotBeEmpty();
        committed.Should().NotBeEmpty();

        var unproduced = committed.Keys.Where(name => !captured.ContainsKey(name)).ToList();

        var detail = string.Join("; ", unproduced.Select(name => $"'{committed[name]}'"));
        unproduced.Should().BeEmpty(
            because: "the following committed screenshot(s) have no 'I capture the screen as \"name\"' step " +
                      $"in any .feature file, so nothing regenerates them: {detail}");
    }
}
