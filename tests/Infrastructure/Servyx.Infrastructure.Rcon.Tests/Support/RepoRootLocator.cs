namespace Servyx.Infrastructure.Rcon.Tests.Support;

/// <summary>
/// Locates the repository root by walking up from this test assembly's own output directory until a
/// directory containing <c>Servyx.sln</c> is found. Never uses <see cref="Environment.CurrentDirectory"/>,
/// which depends on how the test runner itself was launched and is not a reliable anchor.
/// </summary>
/// <remarks>
/// A deliberate, intentionally tiny duplicate of the same helper in
/// <c>tests\Core\Servyx.Definitions.Tests\Support</c> and <c>tests\Presentation\Servyx.Web.Tests\Documentation</c>
/// — see those files' remarks for why duplicating this ~15-line helper is preferred over extracting a shared
/// project for it.
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
                "Could not locate the repository root (a directory containing Servyx.sln) above "
                + $"'{AppContext.BaseDirectory}'.");
        }

        return dir;
    }
}
