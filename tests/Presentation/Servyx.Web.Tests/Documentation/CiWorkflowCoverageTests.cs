using System.Text.RegularExpressions;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Guards against the exact failure mode that let <c>tests/Core/Servyx.Definitions.Tests</c> go unrun by CI
/// for as long as it did: <c>.github/workflows/ci.yml</c> drives its Test step from a hand-maintained bash
/// array rather than <c>dotnet test Servyx.sln</c> (deliberately — see that file's own header comment on why
/// a broad solution-scoped run is not used), which means a newly added <c>*.Tests.csproj</c> has no
/// automatic path into CI at all. Nothing before this test asserted that every test project on disk is
/// either in that array or in a documented, justified exclusion list; a project could be added, never wired
/// in, and no signal anywhere would say so.
/// </summary>
/// <remarks>
/// <see cref="DocumentedExclusions"/> is the same three projects <c>ci.yml</c>'s own header comment already
/// excludes, for the same reasons stated there (Playwright/browser-binary dependencies for the two E2E
/// projects; a real, live, production game server for <c>Servyx.Remote.Tests</c>). This test does not
/// second-guess those exclusions — it only guards against a *fourth* project silently joining neither list.
/// </remarks>
public sealed class CiWorkflowCoverageTests
{
    /// <summary>
    /// Test projects <c>ci.yml</c> deliberately does not run, each with the same justification as that
    /// file's own header comment. Keep this in sync with that comment by hand — this test's whole point is
    /// to make an *undocumented* gap loud, not to make documented exclusions invisible.
    /// </summary>
    private static readonly IReadOnlySet<string> DocumentedExclusions = new HashSet<string>(StringComparer.Ordinal)
    {
        // References Microsoft.Playwright directly and launches Servyx.Web as a real subprocess driven by a
        // real browser; browser binaries are not installed in this workflow's environment.
        "tests/Servyx.E2E.Tests",

        // Reqnroll/Gherkin E2E suite that itself references Microsoft.Playwright and project-references
        // Servyx.E2E.Tests. Same reasoning as above.
        "tests/Servyx.E2E.Bdd.Tests",

        // Talks to a REAL, LIVE, PRODUCTION game server over SSH. Gated behind SERVYX_REMOTE_E2E plus every
        // SERVYX_REMOTE_* coordinate, none of which belongs in a shared CI environment — see that project's
        // own remarks and docs/testing.md's "Testing the ssh+docker transport" section.
        "tests/Servyx.Remote.Tests",
    };

    /// <summary>Matches one quoted project path inside the Test step's bash array, e.g. <c>"tests/Core/Servyx.Domain.Tests"</c>.</summary>
    private static readonly Regex ProjectPathPattern = new(@"""(tests/[^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_test_project_on_disk_is_either_run_by_ci_or_a_documented_exclusion()
    {
        var repoRoot = RepoRootLocator.Find();

        var onDisk = FindTestProjectsOnDisk(repoRoot);
        var ciProjects = ExtractCiProjectList(repoRoot);

        // Anti-vacuity: if either set came back empty, the discovery logic itself is broken (wrong glob, wrong
        // regex, wrong path), and the assertion below would otherwise pass by having nothing to compare.
        onDisk.Should().NotBeEmpty("the tests/ directory glob should find real test projects — an empty set means the discovery path is wrong, not that no test projects exist");
        ciProjects.Should().NotBeEmpty("the ci.yml project-array regex should find real entries — an empty set means the parser is wrong, not that ci.yml runs nothing");

        var accountedFor = new HashSet<string>(ciProjects, StringComparer.Ordinal);
        accountedFor.UnionWith(DocumentedExclusions);

        var orphaned = onDisk.Where(p => !accountedFor.Contains(p)).ToList();
        orphaned.Should().BeEmpty(
            because: "every test project under tests/ must appear in ci.yml's Test step array or in "
                + $"{nameof(CiWorkflowCoverageTests)}.{nameof(DocumentedExclusions)} with a stated reason — "
                + "found orphaned project(s) that CI silently never runs and this test never excused");

        // The reverse direction: ci.yml or the exclusion list naming a project that does not exist on disk
        // (a typo, or a project that was since deleted or renamed) is equally a lie about what actually runs.
        var phantom = accountedFor.Where(p => !onDisk.Contains(p)).ToList();
        phantom.Should().BeEmpty(
            because: "ci.yml's project array and the documented exclusion list must name only test projects "
                + "that actually exist — found entries with no matching *.Tests.csproj on disk");
    }

    /// <summary>
    /// Every <c>tests/**/*.Tests.csproj</c>'s own <em>directory</em>, as the project-relative path
    /// <c>ci.yml</c>'s own array and <see cref="DocumentedExclusions"/> both use (forward slashes, the
    /// project directory — e.g. <c>tests/Servyx.Bdd.Tests</c> — matching what <c>dotnet test "&lt;path&gt;"</c>
    /// accepts, not the <c>.csproj</c> file itself).
    /// </summary>
    private static IReadOnlySet<string> FindTestProjectsOnDisk(DirectoryInfo repoRoot)
    {
        var testsDir = new DirectoryInfo(Path.Combine(repoRoot.FullName, "tests"));

        return testsDir.EnumerateFiles("*.Tests.csproj", SearchOption.AllDirectories)
            .Select(f => f.DirectoryName ?? throw new InvalidOperationException($"'{f.FullName}' has no parent directory."))
            .Select(d => Path.GetRelativePath(repoRoot.FullName, d))
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every project path named inside <c>ci.yml</c>'s Test step bash array.</summary>
    private static IReadOnlySet<string> ExtractCiProjectList(DirectoryInfo repoRoot)
    {
        var ciYamlPath = Path.Combine(repoRoot.FullName, ".github", "workflows", "ci.yml");
        var text = File.ReadAllText(ciYamlPath);

        return ProjectPathPattern.Matches(text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
