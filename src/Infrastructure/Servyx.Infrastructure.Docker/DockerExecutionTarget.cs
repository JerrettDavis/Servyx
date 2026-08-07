using System.Diagnostics;
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
/// mechanism behind <c>docker cp</c>) rather than by shelling out. <see cref="ExecuteAsync"/> runs a
/// <c>docker exec</c> to completion via <c>Docker.DotNet</c>'s exec API (create, attach, drain, inspect for
/// the exit code) with no shell involved — <see cref="CommandSpec.Arguments"/> reach the container as
/// discrete argv elements, never joined into a shell line. <see cref="ExecuteStreamingAsync"/> — the
/// incremental, chunk-as-it-arrives variant — is not implemented; see its own remarks for what that would
/// take.
/// </para>
/// <para>
/// <b>Writes are refused unless this instance was constructed write-capable.</b> The
/// <c>writeMode</c> constructor parameter defaults to <see cref="WriteMode.ReadOnly"/>, so every existing
/// construction site — and every caller who does not deliberately opt in — gets exactly the M1 behaviour:
/// <see cref="WriteFileAsync"/> and <see cref="DeleteAsync"/> throw <see cref="WritesDisabledException"/>
/// before any I/O occurs. Being constructed with <see cref="WriteMode.Enabled"/> only makes this instance
/// <em>capable</em>; the structural enforcement that decides which servers get such an instance is
/// <see cref="WriteGuardedExecutionTarget"/>, which every transport-produced session is wrapped in.
/// <see cref="ExecuteAsync"/> itself carries no such internal check — unlike the two methods above, its
/// mutating-ness is not inherent to the method but declared per call via <see cref="CommandSpec.Intent"/>,
/// and gating that is <see cref="WriteGuardedExecutionTarget"/>'s job, not this class's: it never parses or
/// guesses at argv, so it would have nothing but "assume mutating" to gate on if it tried.
/// </para>
/// <para>
/// A file write uses the narrowest exec this type issues on its own behalf: content is placed as a
/// temporary sibling with <c>ExtractArchiveToContainerAsync</c> and then moved over the target with a
/// single <c>mv -f</c>, because the Engine API has no rename endpoint and an in-place archive extraction is
/// not atomic. <see cref="DeleteAsync"/> likewise runs a single <c>rm -f</c>. Both of those now go through
/// <see cref="ExecuteAsync"/> itself — there is one exec path, not two — but only after
/// <see cref="WriteFileAsync"/>/<see cref="DeleteAsync"/>'s own write-mode check has already passed, so
/// routing through the now-open general channel does not reopen the write gate they enforce.
/// </para>
/// <para>
/// <b>Which of these need the container to be running.</b> The archive endpoints
/// (<c>GetArchiveFromContainerAsync</c>, <c>ExtractArchiveToContainerAsync</c> — <c>GET</c>/<c>HEAD</c> and
/// <c>PUT /containers/{id}/archive</c>, the pair behind <c>docker cp</c>) are served by the daemon against
/// the container's filesystem, so stat, list, read, and archive extraction all work on a container that has
/// been created but never started. <c>docker exec</c> does not: it starts a process <em>inside</em> a
/// running container, so every member that reaches <see cref="ExecuteAsync"/> — including the <c>mv</c>
/// that finalizes a <see cref="FileWriteStrategy.AtomicRename"/> write — requires a running container. That
/// is why <see cref="FileWriteStrategy.DirectPlacement"/> exists: it is the same write with the rename
/// removed, and it is the only shape a write into a not-yet-started container can take. The choice is the
/// caller's, declared on <see cref="FileWriteOptions.Strategy"/>; this type never inspects the container's
/// state and never downgrades one strategy to the other.
/// </para>
/// </remarks>
public sealed class DockerExecutionTarget : IExecutionTarget, IContainerLifecycle
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
    /// <remarks>
    /// Runs <paramref name="spec"/> as a <c>docker exec</c>: <c>ExecCreateContainerAsync</c> to create it,
    /// <c>StartAndAttachContainerExecAsync</c> to run it and drain stdout/stderr to completion, then
    /// <c>InspectContainerExecAsync</c> for the exit code. <see cref="CommandSpec.Executable"/> and
    /// <see cref="CommandSpec.Arguments"/> are passed straight through as the exec's argv — there is no
    /// shell, so no interpolation, word-splitting, or glob expansion happens on either side.
    /// </remarks>
    public async Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);

        var stopwatch = Stopwatch.StartNew();
        var (exitCode, standardOutput, standardError) = await RunExecAsync(
            spec.Executable, spec.Arguments, spec.WorkingDirectory, spec.EnvironmentOverrides, ct).ConfigureAwait(false);
        stopwatch.Stop();

        return new CommandResult(exitCode, standardOutput, standardError, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: streaming exec is not implemented. <see cref="ExecuteAsync"/> already drains a
    /// <c>Docker.DotNet</c> <see cref="MultiplexedStream"/> to completion before returning; a streaming
    /// variant would need to read that same multiplexed stream incrementally — demultiplexing stdout/stderr
    /// frames as they arrive rather than after the process exits — and yield each as an
    /// <see cref="OutputChunk"/>. That incremental-read/yield loop does not exist yet.
    /// </exception>
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "Docker streaming exec is not implemented. ExecuteAsync drains the exec's multiplexed stream to " +
            "completion before returning; a streaming variant needs to demultiplex and yield stdout/stderr " +
            "frames incrementally as they arrive instead.");

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
    /// <para>
    /// Under the default <see cref="FileWriteStrategy.AtomicRename"/>, the temporary sibling is written into
    /// the same directory as the target — a temp path anywhere else would make the final <c>mv</c> a
    /// cross-device copy and therefore non-atomic — and is cleaned up if the move fails. The pre-image is
    /// read and hashed before anything is placed, so a mismatched
    /// <see cref="FileWriteOptions.ExpectedPreImageHash"/> aborts with no temporary file ever created.
    /// </para>
    /// <para>
    /// Under <see cref="FileWriteStrategy.DirectPlacement"/> the archive carries the target's own leaf name
    /// and no <c>mv</c> follows, so the whole write is one <c>PUT /containers/{id}/archive</c> and the
    /// container never has to be running. Nothing else changes: the drift check, the pre-image hash, and the
    /// receipt are computed exactly as above, all from archive reads that are equally happy against a
    /// created-but-not-started container.
    /// </para>
    /// <para>
    /// <see cref="FileWriteOptions.Mode"/>, when set, is carried in the tar entry header of that same
    /// archive, so the file exists with its declared permissions from the instant it exists — there is no
    /// window in which a credential sits at a wider mode, and no separate <c>chmod</c> exec that a stopped
    /// container could not have run anyway. When it is not set, an existing file's mode is preserved and a
    /// new file gets <see cref="DefaultFileMode"/>, as before.
    /// </para>
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

        var mode = ResolveMode(options, existing);

        // DirectPlacement lands on the target's own name and stops there. AtomicRename lands on a temp
        // sibling first and finalizes with the mv below. Which one runs is decided by what the caller
        // declared, never by anything observed about the container.
        var placedLeaf = options.Strategy == FileWriteStrategy.DirectPlacement
            ? leaf
            : $"{leaf}.servyx-tmp-{Guid.NewGuid():N}";

        await ExtractOneFileAsync(directory, placedLeaf, bytes, mode, ct).ConfigureAwait(false);

        if (options.Strategy == FileWriteStrategy.AtomicRename)
        {
            var tempPath = JoinContainerPath(directory, placedLeaf);
            var move = await ExecuteAsync(new CommandSpec("mv", ["-f", "--", tempPath, containerPath]), ct)
                .ConfigureAwait(false);
            if (move.ExitCode != 0)
            {
                await TryRemoveQuietlyAsync(tempPath, ct).ConfigureAwait(false);
                throw new IOException(
                    $"Failed to move '{tempPath}' onto '{containerPath}' in container '{_containerRef}' " +
                    $"(exit code {move.ExitCode}){FormatDetail(move.StandardError)}. The target file is unchanged.");
            }
        }

        return new FileWriteReceipt(preImageHash, postImageHash, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The permission bits the written file must end up with: what the caller declared, else the mode the
    /// file already had, else <see cref="DefaultFileMode"/>. Getting the middle case wrong is how a config
    /// write silently makes a file the workload itself can no longer read.
    /// </summary>
    private static UnixFileMode ResolveMode(FileWriteOptions options, FileStat existing) => options.Mode switch
    {
        { } requested => (UnixFileMode)requested,
        _ when existing.Exists && existing.Mode is { } current => (UnixFileMode)current,
        _ => DefaultFileMode,
    };

    /// <summary>
    /// Sends one regular-file tar entry to <c>PUT /containers/{id}/archive</c>, to be extracted into
    /// <paramref name="directory"/> under the name <paramref name="leaf"/> with mode
    /// <paramref name="mode"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="bytes"/> is handed to the tar writer as a stream over the array and is never decoded
    /// into text on the way. That matters because the case this path exists for is a credential seeded
    /// before a container's first start: a <see cref="string"/> anywhere in here would be one interpolation
    /// away from a log line, and unerasable once made.
    /// </remarks>
    private async Task ExtractOneFileAsync(
        string directory, string leaf, byte[] bytes, UnixFileMode mode, CancellationToken ct)
    {
        using var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, leaf)
            {
                DataStream = new MemoryStream(bytes, writable: false),
                Mode = mode,
            };

            await writer.WriteEntryAsync(entry, ct).ConfigureAwait(false);
        }

        tar.Position = 0;
        await _client.Containers.ExtractArchiveToContainerAsync(
            _containerRef,
            new ContainerPathStatParameters { Path = directory.Length == 0 ? "/" : directory },
            tar,
            ct).ConfigureAwait(false);
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

        var delete = await ExecuteAsync(new CommandSpec("rm", ["-f", "--", containerPath]), ct)
            .ConfigureAwait(false);
        if (delete.ExitCode != 0)
        {
            throw new IOException(
                $"Failed to delete '{containerPath}' in container '{_containerRef}' " +
                $"(exit code {delete.ExitCode}){FormatDetail(delete.StandardError)}.");
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
    /// Runs one command in the container via <c>docker exec</c> and returns its exit code, stdout, and
    /// stderr. The one exec path in this class: <see cref="ExecuteAsync"/> calls this directly for
    /// caller-supplied commands, and <see cref="WriteFileCoreAsync"/>/<see cref="DeleteCoreAsync"/> reach it
    /// through <see cref="ExecuteAsync"/> rather than duplicating it, so there is exactly one place that
    /// talks to <c>Docker.DotNet</c>'s exec API.
    /// </summary>
    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunExecAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        CancellationToken ct)
    {
        var cmd = new List<string>(arguments.Count + 1) { executable };
        cmd.AddRange(arguments);

        var created = await _client.Exec.ExecCreateContainerAsync(
            _containerRef,
            new ContainerExecCreateParameters
            {
                Cmd = cmd,
                AttachStdin = false,
                AttachStdout = true,
                AttachStderr = true,
                Detach = false,
                Tty = false,
                WorkingDir = workingDirectory,
                Env = environmentOverrides is { Count: > 0 }
                    ? environmentOverrides.Select(kv => $"{kv.Key}={kv.Value}").ToList()
                    : null,
            },
            ct).ConfigureAwait(false);

        if (created?.ID is not { Length: > 0 } execId)
        {
            throw new IOException(
                $"The Docker daemon did not return an exec id for container '{_containerRef}'.");
        }

        var standardOutput = string.Empty;
        var standardError = string.Empty;
        using (var stream = await _client.Exec.StartAndAttachContainerExecAsync(execId, tty: false, ct).ConfigureAwait(false))
        {
            if (stream is not null)
            {
                // Draining to end is also how completion is awaited: the daemon closes the attached stream
                // when the process exits, so the exit code inspected below is never read while still running.
                var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
                standardOutput = stdout ?? string.Empty;
                standardError = stderr ?? string.Empty;
            }
        }

        var inspect = await _client.Exec.InspectContainerExecAsync(execId, ct).ConfigureAwait(false);
        return ((int)(inspect?.ExitCode ?? 0), standardOutput, standardError);
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
            await ExecuteAsync(new CommandSpec("rm", ["-f", "--", containerPath]), ct).ConfigureAwait(false);
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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Maps each <see cref="ContainerLifecycleVerb"/> onto the matching <c>Docker.DotNet</c> container API
    /// — <c>StartContainerAsync</c>, <c>StopContainerAsync</c>, <c>RestartContainerAsync</c>,
    /// <c>KillContainerAsync</c> — never <c>docker exec</c>: you cannot exec into a container that is not
    /// running, so <see cref="ContainerLifecycleVerb.Start"/> in particular could never be expressed as one.
    /// </para>
    /// <para>
    /// <b>Errors are reported through the result, not thrown.</b> Unlike the file/exec surface above (which
    /// signals failure via .NET exceptions — <see cref="FileNotFoundException"/>,
    /// <see cref="IOException"/> — because that is what an <see cref="IExecutionTarget"/> caller expects),
    /// <see cref="ContainerLifecycleResult"/> exists precisely to carry an operation's outcome without one. A
    /// <see cref="DockerApiException"/> raised by the daemon (including
    /// <see cref="DockerContainerNotFoundException"/>, which derives from it) is caught and surfaced as
    /// <c>Success: false</c> with the exception's message in <see cref="ContainerLifecycleResult.Detail"/>,
    /// rather than propagating or being swallowed.
    /// </para>
    /// <para>
    /// On success, the container is inspected once more to populate <see cref="ContainerLifecycleResult.State"/>
    /// (and, for <see cref="ContainerLifecycleVerb.Stop"/>/<see cref="ContainerLifecycleVerb.Kill"/>, its
    /// <see cref="ContainerLifecycleResult.ExitCode"/>) — cheap relative to the lifecycle call itself, and a
    /// failure there is likewise absorbed into a null state rather than turning an otherwise-successful
    /// transition into a reported failure.
    /// </para>
    /// </remarks>
    public async Task<ContainerLifecycleResult> InvokeAsync(ContainerLifecycleRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            switch (request.Verb)
            {
                case ContainerLifecycleVerb.Start:
                    await _client.Containers.StartContainerAsync(
                        request.ContainerRef, new ContainerStartParameters(), ct).ConfigureAwait(false);
                    break;

                case ContainerLifecycleVerb.Stop:
                    await _client.Containers.StopContainerAsync(
                        request.ContainerRef,
                        new ContainerStopParameters { WaitBeforeKillSeconds = ToWaitBeforeKillSeconds(request.GracePeriod) },
                        ct).ConfigureAwait(false);
                    break;

                case ContainerLifecycleVerb.Restart:
                    await _client.Containers.RestartContainerAsync(
                        request.ContainerRef,
                        new ContainerRestartParameters { WaitBeforeKillSeconds = ToWaitBeforeKillSeconds(request.GracePeriod) },
                        ct).ConfigureAwait(false);
                    break;

                case ContainerLifecycleVerb.Kill:
                    await _client.Containers.KillContainerAsync(
                        request.ContainerRef,
                        new ContainerKillParameters { Signal = request.Signal },
                        ct).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(request), request.Verb, "Unknown container lifecycle verb.");
            }
        }
        catch (DockerApiException ex)
        {
            return new ContainerLifecycleResult(
                false,
                $"Docker refused to {request.Verb.ToString().ToLowerInvariant()} container '{request.ContainerRef}': {ex.Message}");
        }

        var (state, exitCode) = await TryInspectAfterLifecycleAsync(request.ContainerRef, request.Verb, ct).ConfigureAwait(false);
        return new ContainerLifecycleResult(
            true, $"Container '{request.ContainerRef}' {PastTense(request.Verb)}.", exitCode, state);
    }

    /// <summary>Converts a grace period into the whole-seconds shape Docker.DotNet's lifecycle parameters take.</summary>
    private static uint? ToWaitBeforeKillSeconds(TimeSpan? gracePeriod) =>
        gracePeriod is { } grace ? (uint)Math.Max(0, Math.Round(grace.TotalSeconds)) : null;

    private static string PastTense(ContainerLifecycleVerb verb) => verb switch
    {
        ContainerLifecycleVerb.Start => "started",
        ContainerLifecycleVerb.Stop => "stopped",
        ContainerLifecycleVerb.Restart => "restarted",
        ContainerLifecycleVerb.Kill => "killed",
        _ => verb.ToString(),
    };

    /// <summary>
    /// Inspects the container after a successful lifecycle transition to report its resulting state (and,
    /// for verbs that terminate the container, its exit code). Best-effort: an inspection failure here does
    /// not turn an otherwise-successful lifecycle call into a reported failure, it just leaves the extra
    /// detail unpopulated.
    /// </summary>
    private async Task<(string? State, int? ExitCode)> TryInspectAfterLifecycleAsync(
        string containerRef, ContainerLifecycleVerb verb, CancellationToken ct)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(containerRef, ct).ConfigureAwait(false);
            var state = inspect?.State?.Status;
            int? exitCode = verb is ContainerLifecycleVerb.Stop or ContainerLifecycleVerb.Kill
                ? (int)(inspect?.State?.ExitCode ?? 0)
                : null;

            return (state, exitCode);
        }
        catch (DockerApiException)
        {
            return (null, null);
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

    /// <summary>Rejoins a parent directory and a leaf name into an absolute container path.</summary>
    private static string JoinContainerPath(string directory, string leaf) =>
        directory.Length == 0 ? "/" + leaf : directory + "/" + leaf;

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
