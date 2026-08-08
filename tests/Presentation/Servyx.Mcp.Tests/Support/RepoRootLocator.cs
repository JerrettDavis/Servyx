namespace Servyx.Mcp.Tests.Support;

/// <summary>
/// Locates the repository root by walking up from this test assembly's own output directory until a
/// directory containing <c>Servyx.sln</c> is found. Never uses <see cref="Environment.CurrentDirectory"/>,
/// which depends on how the test runner itself was launched and is not a reliable anchor.
/// </summary>
/// <remarks>
/// A deliberate duplicate of <c>tests/Presentation/Servyx.Web.Tests/Documentation/RepoRootLocator.cs</c> (and
/// its own further twin in the E2E BDD suite) — see that file's own remarks for why copying this ~15-line
/// helper is the lowest-coupling option here, rather than extracting a shared test-infrastructure project.
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
