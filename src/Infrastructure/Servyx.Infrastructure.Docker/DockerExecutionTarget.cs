using System.Formats.Tar;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation for a single Docker container, reached through the
/// Docker Engine API.
/// </summary>
/// <remarks>
/// This milestone is strictly read-only. File reads are implemented via the container archive API
/// (<c>GetArchiveFromContainerAsync</c>, the same mechanism behind <c>docker cp</c>) rather than by
/// shelling out. <see cref="ExecuteAsync"/>/<see cref="ExecuteStreamingAsync"/> require <c>docker exec</c>,
/// which is out of scope until M2, and <see cref="WriteFileAsync"/>/<see cref="DeleteAsync"/> are
/// unconditionally disabled.
/// </remarks>
public sealed class DockerExecutionTarget : IExecutionTarget
{
    private readonly IDockerClient _client;
    private readonly string _containerRef;
    private readonly string _containerRootPath;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>Creates a target bound to a specific container.</summary>
    /// <param name="client">The Docker client to issue requests through.</param>
    /// <param name="containerRef">The container's id or name.</param>
    /// <param name="containerRootPath">
    /// The absolute in-container path that every <see cref="TargetPath"/> passed to this instance is
    /// relative to (e.g. <c>/palworld</c>). Defaults to <c>/</c>.
    /// </param>
    /// <param name="ownsClient">Whether this instance disposes <paramref name="client"/> when it is itself disposed.</param>
    public DockerExecutionTarget(IDockerClient client, string containerRef, string containerRootPath = "/", bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRef);

        _client = client;
        _containerRef = containerRef;
        _containerRootPath = string.IsNullOrWhiteSpace(containerRootPath) ? "/" : containerRootPath;
        _ownsClient = ownsClient;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: <c>docker exec</c>-based command execution is out of scope for this read-only
    /// milestone (M1). It lands in M2, once exec-based control channels are implemented.
    /// </exception>
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Docker exec-based command execution is out of scope for this milestone (M1), which is read-only. It arrives in M2.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: <c>docker exec</c>-based command execution is out of scope for this read-only
    /// milestone (M1). It lands in M2, once exec-based control channels are implemented.
    /// </exception>
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Docker exec-based command execution is out of scope for this milestone (M1), which is read-only. It arrives in M2.");

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try
        {
            var stat = await GetPathStatAsync(path, ct).ConfigureAwait(false);
            return stat is not null;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        try
        {
            var stat = await GetPathStatAsync(path, ct).ConfigureAwait(false);
            if (stat is null)
            {
                return new FileStat(false, false, null, null, null);
            }

            return new FileStat(true, IsDirectoryMode(stat.Mode), stat.Size, stat.Mtime, null);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new FileStat(false, false, null, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var containerPath = ToContainerPath(path);
        GetArchiveFromContainerResponse response;
        try
        {
            response = await _client.Containers.GetArchiveFromContainerAsync(
                _containerRef,
                new GetArchiveFromContainerParameters { Path = containerPath },
                statOnly: false,
                ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DirectoryNotFoundException($"Directory '{containerPath}' does not exist in container '{_containerRef}'.");
        }

        var entries = new Dictionary<string, FileEntry>(StringComparer.Ordinal);

        // TarReader's default leaveOpen:false disposes the underlying stream itself; no separate
        // "await using" over response.Stream is needed (and would double-dispose it).
        await using (var tarReader = new TarReader(response.Stream))
        {
            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
            {
                ProcessArchiveEntryForListing(entry, entries);
            }
        }

        return entries.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Folds a single tar entry from the archive returned for a directory into the immediate-children
    /// map. Docker's archive API always returns the full recursive subtree rooted at the requested
    /// directory; <see cref="ListDirectoryAsync"/> is deliberately non-recursive, so only entries one
    /// path segment below the requested directory are surfaced, with deeper descendants collapsed into
    /// their owning immediate child (marked as a directory).
    /// </summary>
    private static void ProcessArchiveEntryForListing(TarEntry entry, Dictionary<string, FileEntry> entries)
    {
        var name = entry.Name.Trim('/');
        var firstSlash = name.IndexOf('/');
        if (firstSlash < 0)
        {
            // This is the root entry itself (the requested directory or, degenerately, a bare file) — not a child.
            return;
        }

        var rest = name[(firstSlash + 1)..];
        if (rest.Length == 0)
        {
            return;
        }

        var nextSlash = rest.IndexOf('/');
        var childName = nextSlash < 0 ? rest : rest[..nextSlash];
        var isDescendant = nextSlash >= 0;
        var isDirectory = isDescendant || entry.EntryType == TarEntryType.Directory;

        if (entries.TryGetValue(childName, out var existing))
        {
            if (isDirectory && !existing.IsDirectory)
            {
                entries[childName] = existing with { IsDirectory = true };
            }

            return;
        }

        long? size = !isDescendant && entry.EntryType is TarEntryType.RegularFile or TarEntryType.ContiguousFile
            ? entry.Length
            : null;
        DateTimeOffset? modified = isDescendant ? null : entry.ModificationTime;

        entries[childName] = new FileEntry(childName, isDirectory, size, modified);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var containerPath = ToContainerPath(path);
        GetArchiveFromContainerResponse response;
        try
        {
            response = await _client.Containers.GetArchiveFromContainerAsync(
                _containerRef,
                new GetArchiveFromContainerParameters { Path = containerPath },
                statOnly: false,
                ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"File '{containerPath}' was not found in container '{_containerRef}'.", containerPath);
        }

        // Docker's archive for a single requested path always contains that path's own entry named by
        // its bare leaf (e.g. requesting ".../bar.txt" yields a tar whose entry is named "bar.txt", with
        // no path prefix and no slash). Matching on that exact leaf name — rather than returning the
        // first regular-file entry encountered — is what stops a directory request from silently
        // returning some arbitrary descendant file's bytes: a directory's archive contains many entries,
        // and the first one is not necessarily the requested path itself.
        var expectedLeaf = containerPath.TrimEnd('/');
        var lastSlash = expectedLeaf.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            expectedLeaf = expectedLeaf[(lastSlash + 1)..];
        }

        // TarReader's default leaveOpen:false disposes the underlying stream itself; no separate
        // "await using" over response.Stream is needed (and would double-dispose it).
        await using (var tarReader = new TarReader(response.Stream))
        {
            TarEntry? entry;
            while ((entry = await tarReader.GetNextEntryAsync(copyData: true, ct).ConfigureAwait(false)) is not null)
            {
                var name = entry.Name.Trim('/');
                if (!string.Equals(name, expectedLeaf, StringComparison.Ordinal))
                {
                    continue; // Some other entry (a descendant, if the requested path is a directory).
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.ContiguousFile))
                {
                    throw new IOException($"'{containerPath}' in container '{_containerRef}' is a directory, not a regular file.");
                }

                var buffer = new MemoryStream();
                if (entry.DataStream is not null)
                {
                    await entry.DataStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                }

                buffer.Position = 0;
                return buffer;
            }
        }

        throw new FileNotFoundException($"'{containerPath}' in container '{_containerRef}' was not found in its own archive.", containerPath);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">Always thrown: file writes are disabled in this milestone.</exception>
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        throw new WritesDisabledException(
            "Docker execution target file writes are disabled in this milestone (M1), which is strictly read-only.");

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">Always thrown: file deletes are disabled in this milestone.</exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        throw new WritesDisabledException(
            "Docker execution target file deletes are disabled in this milestone (M1), which is strictly read-only.");

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<ContainerPathStatResponse?> GetPathStatAsync(TargetPath path, CancellationToken ct)
    {
        var response = await _client.Containers.GetArchiveFromContainerAsync(
            _containerRef,
            new GetArchiveFromContainerParameters { Path = ToContainerPath(path) },
            statOnly: true,
            ct).ConfigureAwait(false);

        if (response.Stream is not null)
        {
            await response.Stream.DisposeAsync().ConfigureAwait(false);
        }

        return response.Stat;
    }

    /// <summary>
    /// Whether a raw stat mode value (as returned by the Docker archive-stat header) denotes a
    /// directory. Docker serializes Go's <c>os.FileMode</c>, whose top bit (<c>1&lt;&lt;31</c>) is the
    /// directory flag (<c>os.ModeDir</c>).
    /// </summary>
    private static bool IsDirectoryMode(uint mode) => (mode & 0x8000_0000u) != 0;

    private string ToContainerPath(TargetPath path)
    {
        var root = _containerRootPath.TrimEnd('/');
        if (string.IsNullOrEmpty(path.Value))
        {
            return string.IsNullOrEmpty(root) ? "/" : root;
        }

        return string.IsNullOrEmpty(root) ? "/" + path.Value : root + "/" + path.Value;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
