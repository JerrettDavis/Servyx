namespace Servyx.Web.Tests.Definitions.Support;

/// <summary>
/// A throwaway directory under the OS temp path used to synthesize fixture definitions for
/// <c>FileSystemGameDefinitionProvider</c>/<c>GameDefinitionCatalog</c>-backed tests in this project, so
/// those tests never touch the repository's real <c>definitions/</c> folder. Deleted, best-effort, on
/// <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// This is a deliberate, intentionally tiny duplicate of
/// <c>tests\Core\Servyx.Definitions.Tests\Support\TempDefinitionsDirectory.cs</c> — see
/// <c>RepoRootLocator</c>'s own remarks in this project for why duplicating a ~20-line helper is the
/// lowest-coupling option rather than referencing across test projects.
/// </remarks>
internal sealed class TempDefinitionsDirectory : IDisposable
{
    public TempDefinitionsDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "servyx-web-definitions-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>The directory a <c>FileSystemGameDefinitionProvider</c> under test should be rooted at.</summary>
    public string Root { get; }

    /// <summary>Writes a flat <c>{fileName}</c> definition directly under <see cref="Root"/>.</summary>
    public string WriteFlat(string fileName, string yaml)
    {
        var path = Path.Combine(Root, fileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp directory is harmless and Windows sometimes still holds
            // a brief handle on a file this test process just closed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
