using System.Globalization;

namespace Servyx.Infrastructure.Process.Tests;

/// <summary>
/// A throwaway directory under the machine's temp path, deleted on <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// Every test in this assembly touches the real filesystem — that is the point of a local transport — so
/// every test that does gets its own root and removes it afterwards. Nothing is shared between tests, and
/// nothing is written outside the temp path.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory(string label = "servyx-local")
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    /// <summary>The absolute path of this directory.</summary>
    internal string Root { get; }

    /// <summary>Composes an absolute path under <see cref="Root"/>.</summary>
    internal string At(params string[] segments) => System.IO.Path.Combine([Root, .. segments]);

    /// <summary>Creates a file (and any missing parents) under <see cref="Root"/>, returning its absolute path.</summary>
    internal string WriteFile(string relativePath, string content)
    {
        var full = At(relativePath);
        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// A stable, comparable description of everything under <see cref="Root"/>: every path, whether it is a
    /// directory, its length, and its last-write time. Two snapshots comparing equal means nothing was
    /// created, removed, rewritten, or touched in between.
    /// </summary>
    internal IReadOnlyList<string> Snapshot()
    {
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
            var length = info is FileInfo file ? file.Length.ToString(CultureInfo.InvariantCulture) : "dir";
            entries.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{path}|{length}|{info.LastWriteTimeUtc:O}"));
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }
}
