using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation for a single Docker container, reached through the
/// Docker Engine API.
/// </summary>
/// <remarks>
/// <para>
/// File reads are implemented via the container archive API (<c>GetArchiveFromContainerAsync</c>, the same
/// mechanism behind <c>docker cp</c>) rather than by shelling out.
/// <see cref="ExecuteAsync"/>/<see cref="ExecuteStreamingAsync"/> — the general-purpose
/// <c>docker exec</c> command channel — remain out of scope until M2 and still throw
/// <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// <b>Writes are refused unless this instance was constructed write-capable.</b> The
/// <c>writeMode</c> constructor parameter defaults to <see cref="WriteMode.ReadOnly"/>, so every existing
/// construction site — and every caller who does not deliberately opt in — gets exactly the M1 behaviour:
/// <see cref="WriteFileAsync"/> and <see cref="DeleteAsync"/> throw <see cref="WritesDisabledException"/>
/// before any I/O occurs. Being constructed with <see cref="WriteMode.Enabled"/> only makes this instance
/// <em>capable</em>; the structural enforcement that decides which servers get such an instance is
/// <see cref="WriteGuardedExecutionTarget"/>, which every transport-produced session is wrapped in.
/// </para>
/// <para>
/// A permitted write uses the narrowest exec this type is willing to perform: content is placed as a
/// temporary sibling with <c>ExtractArchiveToContainerAsync</c> and then moved over the target with a
/// single <c>mv -f</c>, because the Engine API has no rename endpoint and an in-place archive extraction is
/// not atomic. <see cref="DeleteAsync"/> likewise runs a single <c>rm -f</c>. Neither goes through
/// <see cref="ExecuteAsync"/>: those two argv vectors are fixed, and the general command channel staying
/// closed until M2 is a separate promise from this one.
/// </para>
/// </remarks>
public sealed class DockerExecutionTarget : IExecutionTarget
{
    private readonly IDockerClient _client;
    private readonly string _containerRef;
    private readonly string _containerRootPath;
    private readonly bool _ownsClient;
    private readonly WriteMode _writeMode;
    private bool _disposed;

    /// <summary>Creates a target bound to a specific container.</summary>
    /// <param name="client">The Docker client to issue requests through.</param>
    /// <param name="containerRef">The container's id or name.</param>
    /// <param name="containerRootPath">
    /// The absolute in-container path that every <see cref="TargetPath"/> passed to this instance is
    /// relative to (e.g. <c>/palworld</c>). Defaults to <c>/</c>.
    /// </param>
    /// <param name="ownsClient">Whether this instance disposes <paramref name="client"/> when it is itself disposed.</param>
    /// <param name="writeMode">
    /// The write posture this instance was constructed for. Defaults to <see cref="WriteMode.ReadOnly"/>,
    /// under which <see cref="WriteFileAsync"/> and <see cref="DeleteAsync"/> refuse before any I/O.
    /// </param>
    public DockerExecutionTarget(
        IDockerClient client,
        string containerRef,
        string containerRootPath = "/",
        bool ownsClient = false,
        WriteMode writeMode = WriteMode.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRef);

        _client = client;
        _containerRef = containerRef;
        _containerRootPath = string.IsNullOrWhiteSpace(containerRootPath) ? "/" : containerRootPath;
        _ownsClient = ownsClient;
        _writeMode = writeMode;
    }

    /// <summary>The write posture this instance was constructed for.</summary>
    public WriteMode WriteMode => _writeMode;

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

            return new FileStat(true, IsDirectoryMode(stat.Mode), stat.Size, stat.Mtime, null)
            {
                Mode = (int)(stat.Mode & PosixPermissionBitsMask),
                IsSymlink = IsSymlinkMode(stat.Mode),

                // Docker's archive-stat header (the statOnly:true response GetPathStatAsync returns) is
                // Go's os.FileMode plus size/mtime/name only — unlike a full tar entry (as read by
                // ListDirectoryAsync/OpenReadAsync below), it carries no uid/gid/owner-name and no mount
                // metadata. Populating those here would require fetching the whole archive
                // (statOnly:false) on every stat call, which this read-only milestone does not do, so
                // Owner/Group/Uid/Gid/IsReadOnlyMount stay at their FileStat defaults (null/false).
            };
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
    /// <remarks>
    /// The temporary sibling is written into the same directory as the target — a temp path anywhere else
    /// would make the final <c>mv</c> a cross-device copy and therefore non-atomic — and is cleaned up if
    /// the move fails. The pre-image is read and hashed before anything is placed, so a mismatched
    /// <see cref="FileWriteOptions.ExpectedPreImageHash"/> aborts with no temporary file ever created.
    /// </remarks>
    /// <exception cref="WritesDisabledException">
    /// This instance was not constructed with <see cref="WriteMode.Enabled"/>. Thrown synchronously, before
    /// <paramref name="content"/> is read and before any request reaches the daemon.
    /// </exception>
    /// <exception cref="TargetDriftException">
    /// <paramref name="options"/> specifies an <c>ExpectedPreImageHash</c> that does not match the file's
    /// current content. Thrown before any mutating request is issued.
    /// </exception>
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfWritesDisabled("write file", path);

        return WriteFileCoreAsync(path, content, options, ct);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// This instance was not constructed with <see cref="WriteMode.Enabled"/>. Thrown synchronously, before
    /// any request reaches the daemon.
    /// </exception>
    /// <exception cref="FileNotFoundException">The path does not exist in the container.</exception>
    /// <exception cref="IOException">The path is a directory, or <c>rm</c> reported a failure.</exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfWritesDisabled("delete", path);

        return DeleteCoreAsync(path, ct);
    }

    private async Task<FileWriteReceipt> WriteFileCoreAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct)
    {
        var containerPath = ToContainerPath(path);
        var (directory, leaf) = SplitContainerPath(containerPath);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var postImageHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var existing = await StatAsync(path, ct).ConfigureAwait(false);
        var preImage = await TryReadPreImageAsync(path, ct).ConfigureAwait(false);
        var preImageHash = preImage is null ? null : Convert.ToHexStringLower(SHA256.HashData(preImage));

        if (options.ExpectedPreImageHash is { } expected &&
            !string.Equals(expected, preImageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetDriftException(
                $"Content at '{containerPath}' in container '{_containerRef}' has drifted since it was last observed.",
                path,
                expected,
                preImageHash);
        }

        var tempLeaf = $"{leaf}.servyx-tmp-{Guid.NewGuid():N}";
        var tempPath = directory.Length == 0 ? "/" + tempLeaf : directory + "/" + tempLeaf;

        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, tempLeaf)
            {
                DataStream = new MemoryStream(bytes, writable: false),

                // Preserve the mode the file already had; a fresh file gets rw-r--r--. Getting this wrong is
                // how a config write silently makes a file the game server itself can no longer read.
                Mode = existing.Exists && existing.Mode is { } mode ? (UnixFileMode)mode : DefaultFileMode,
            };

            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }

        tar.Position = 0;
        await _client.Containers.ExtractArchiveToContainerAsync(
            _containerRef,
            new ContainerPathStatParameters { Path = directory.Length == 0 ? "/" : directory },
            tar,
            ct).ConfigureAwait(false);

        var (exitCode, standardError) = await RunFixedCommandAsync(["mv", "-f", "--", tempPath, containerPath], ct)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            await TryRemoveQuietlyAsync(tempPath, ct).ConfigureAwait(false);
            throw new IOException(
                $"Failed to move '{tempPath}' onto '{containerPath}' in container '{_containerRef}' " +
                $"(exit code {exitCode}){FormatDetail(standardError)}. The target file is unchanged.");
        }

        return new FileWriteReceipt(preImageHash, postImageHash, DateTimeOffset.UtcNow);
    }

    private async Task DeleteCoreAsync(TargetPath path, CancellationToken ct)
    {
        var containerPath = ToContainerPath(path);

        var stat = await StatAsync(path, ct).ConfigureAwait(false);
        if (!stat.Exists)
        {
            throw new FileNotFoundException(
                $"File '{containerPath}' was not found in container '{_containerRef}'.", containerPath);
        }

        if (stat.IsDirectory)
        {
            throw new IOException(
                $"'{containerPath}' in container '{_containerRef}' is a directory. " +
                "IExecutionTarget.DeleteAsync deletes files only — recursive directory removal is deliberately " +
                "not reachable through this seam.");
        }

        var (exitCode, standardError) = await RunFixedCommandAsync(["rm", "-f", "--", containerPath], ct)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new IOException(
                $"Failed to delete '{containerPath}' in container '{_containerRef}' " +
                $"(exit code {exitCode}){FormatDetail(standardError)}.");
        }
    }

    /// <summary>
    /// Reads the current bytes at <paramref name="path"/>, or <see langword="null"/> when it does not
    /// exist. Only used to compute the pre-image hash, which is why "missing" is a value rather than a
    /// fault: a write that creates a file is a legitimate write with a null pre-image.
    /// </summary>
    private async Task<byte[]?> TryReadPreImageAsync(TargetPath path, CancellationToken ct)
    {
        try
        {
            await using var stream = await OpenReadAsync(path, ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one fixed argv vector in the container and returns its exit code and stderr.
    /// </summary>
    /// <remarks>
    /// Deliberately private and deliberately not routed through <see cref="ExecuteAsync"/>. The only two
    /// call sites pass literal argv arrays (<c>mv</c> and <c>rm</c>) whose only variable parts are paths
    /// already normalized by <see cref="SandboxedPathResolver"/>, and each is passed as its own argv
    /// element, so there is no shell, no word splitting, and no glob expansion. Opening the general command
    /// channel is M2's decision to make, not a side effect of file writes landing.
    /// </remarks>
    private async Task<(long ExitCode, string StandardError)> RunFixedCommandAsync(IReadOnlyList<string> argv, CancellationToken ct)
    {
        var created = await _client.Exec.ExecCreateContainerAsync(
            _containerRef,
            new ContainerExecCreateParameters
            {
                Cmd = argv.ToList(),
                AttachStdin = false,
                AttachStdout = true,
                AttachStderr = true,
                Detach = false,
                Tty = false,
            },
            ct).ConfigureAwait(false);

        if (created?.ID is not { Length: > 0 } execId)
        {
            throw new IOException(
                $"The Docker daemon did not return an exec id for container '{_containerRef}'.");
        }

        var standardError = string.Empty;
        using (var stream = await _client.Exec.StartAndAttachContainerExecAsync(execId, tty: false, ct).ConfigureAwait(false))
        {
            if (stream is not null)
            {
                // Draining to end is also how completion is awaited: the daemon closes the attached stream
                // when the process exits, so the exit code inspected below is never read while still running.
                var (_, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
                standardError = stderr ?? string.Empty;
            }
        }

        var inspect = await _client.Exec.InspectContainerExecAsync(execId, ct).ConfigureAwait(false);
        return (inspect?.ExitCode ?? 0, standardError);
    }

    /// <summary>
    /// Best-effort removal of a temporary sibling left behind by a failed move. A failure here is
    /// swallowed: the caller is already throwing about the real failure, and replacing that message with a
    /// cleanup error would hide it.
    /// </summary>
    private async Task TryRemoveQuietlyAsync(string containerPath, CancellationToken ct)
    {
        try
        {
            await RunFixedCommandAsync(["rm", "-f", "--", containerPath], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Intentionally ignored — see the summary.
        }
    }

    private void ThrowIfWritesDisabled(string operation, TargetPath path)
    {
        if (_writeMode == Domain.Transport.WriteMode.Enabled)
        {
            return;
        }

        throw new WritesDisabledException(
            $"Refusing to {operation} '{path.Value}' in container '{_containerRef}': this Docker execution " +
            $"target was constructed with WriteMode.{_writeMode}. Writes are enabled per server, never globally.");
    }

    /// <summary>rw-r--r--, the mode a newly created file gets when there is no pre-image mode to preserve.</summary>
    private const UnixFileMode DefaultFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static string FormatDetail(string standardError) =>
        string.IsNullOrWhiteSpace(standardError) ? string.Empty : $": {standardError.Trim()}";

    /// <summary>Splits an absolute container path into its parent directory and leaf name.</summary>
    private static (string Directory, string Leaf) SplitContainerPath(string containerPath)
    {
        var trimmed = containerPath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0
            ? (string.Empty, trimmed.TrimStart('/'))
            : (trimmed[..lastSlash], trimmed[(lastSlash + 1)..]);
    }

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

    /// <summary>The low 9 bits of a Go <c>os.FileMode</c> value, which carry POSIX <c>rwxrwxrwx</c> permission bits.</summary>
    private const uint PosixPermissionBitsMask = 0x1FFu;

    /// <summary>
    /// Whether a raw stat mode value denotes a symbolic link. Go serializes <c>os.ModeSymlink</c> at bit
    /// position 27 (<c>1&lt;&lt;27</c>) of <c>os.FileMode</c>.
    /// </summary>
    private static bool IsSymlinkMode(uint mode) => (mode & 0x0800_0000u) != 0;

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
