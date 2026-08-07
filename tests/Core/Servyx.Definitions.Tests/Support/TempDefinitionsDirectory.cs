namespace Servyx.Definitions.Tests.Support;

/// <summary>
/// A throwaway directory under the OS temp path used to synthesize fixture definitions for
/// <c>FileSystemGameDefinitionProvider</c> tests, so those tests never touch the repository's real
/// <c>definitions/</c> folder. Deleted, best-effort, on <see cref="Dispose"/>.
/// </summary>
internal sealed class TempDefinitionsDirectory : IDisposable
{
    public TempDefinitionsDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "servyx-definitions-tests-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Writes a bundle-layout definition at <c>{bundleName}/definition.yaml</c> under <see cref="Root"/>.</summary>
    public string WriteBundle(string bundleName, string yaml)
    {
        var bundleDir = Path.Combine(Root, bundleName);
        Directory.CreateDirectory(bundleDir);
        var path = Path.Combine(bundleDir, "definition.yaml");
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
