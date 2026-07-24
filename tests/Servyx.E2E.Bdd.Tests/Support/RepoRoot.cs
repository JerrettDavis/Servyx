namespace Servyx.E2E.Bdd.Tests.Support;

/// <summary>
/// Locates the repository root by walking up from the test assembly's own output directory until a
/// directory containing <c>Servyx.sln</c> is found. Never uses <see cref="Environment.CurrentDirectory"/>,
/// which depends on how the test runner itself was launched and is not a reliable anchor.
/// </summary>
public static class RepoRoot
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
