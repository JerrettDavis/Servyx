using System.Text.RegularExpressions;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Catches "wrote a guide, never illustrated it" — every page under <c>docs\user-guide\</c> must contain
/// at least one <c>![alt](../images/name.png)</c> screenshot reference. This is deliberately independent of
/// <see cref="DocumentationScreenshotIntegrityTests"/>, which checks that referenced/committed/captured
/// screenshot *names* agree; this suite instead checks per-page coverage, so a brand-new guide page with zero
/// screenshots fails loudly by name instead of silently passing (an empty per-page reference set contributes
/// nothing to, and is invisible in, the aggregate sets that suite compares).
/// </summary>
public sealed class GuideCoverageTests
{
    private static readonly Regex ImageReferencePattern =
        new(@"!\[[^\]]*\]\(\.\./images/[a-z0-9-]+\.png\)", RegexOptions.Compiled);

    private static DirectoryInfo UserGuideDir =>
        new(Path.Combine(RepoRootLocator.Find().FullName, "docs", "user-guide"));

    public static IEnumerable<object[]> GuideFileNames() =>
        UserGuideDir.GetFiles("*.md").Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(GuideFileNames))]
    public void Guide_page_has_at_least_one_screenshot_reference(string guideFileName)
    {
        var path = Path.Combine(UserGuideDir.FullName, guideFileName);
        var text = File.ReadAllText(path);

        ImageReferencePattern.IsMatch(text).Should().BeTrue(
            $"guide page '{guideFileName}' should illustrate at least one step with a " +
            "'![alt](../images/name.png)' screenshot reference, but none was found");
    }

    [Fact]
    public void At_least_one_guide_page_was_discovered()
    {
        // Guards against a vacuous pass: if UserGuideDir's path were wrong, GuideFileNames() would yield no
        // theory cases at all, and a Theory with zero cases reports as passing.
        GuideFileNames().Should().NotBeEmpty(
            "the docs\\user-guide glob should find real guide pages — an empty set means the path is wrong, " +
            "not that no guides exist");
    }
}
