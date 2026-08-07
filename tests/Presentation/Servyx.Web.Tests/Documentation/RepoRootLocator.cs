namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Locates the repository root by walking up from this test assembly's own output directory until a
/// directory containing <c>Servyx.sln</c> is found. Never uses <see cref="Environment.CurrentDirectory"/>,
/// which depends on how the test runner itself was launched and is not a reliable anchor.
/// </summary>
/// <remarks>
/// This is a deliberate, intentionally tiny duplicate of
/// <c>tests\Servyx.E2E.Bdd.Tests\Support\RepoRoot.cs</c>. <c>Servyx.Web.Tests</c> is in <c>Servyx.sln</c>
/// and runs in CI; the E2E BDD project is neither, and referencing it from here would drag Reqnroll,
/// Playwright, and browser-driven test infrastructure into a project that must stay hermetic (no browser,
/// no Docker). Extracting a brand-new shared class library for one 15-line, unchanging helper would add a
/// project, a solution entry, and a reference edge for both consumers to keep in sync — more coupling than
/// the ~15 duplicated lines it would save. Duplicating this file is the lowest-coupling option.
/// </remarks>
internal static class RepoRootLocator
{
    public static DirectoryInfo Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Servyx.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"Could not locate the repository root (a directory containing Servyx.sln) above " +
                $"'{AppContext.BaseDirectory}'.");
        }

        return dir;
    }
}
