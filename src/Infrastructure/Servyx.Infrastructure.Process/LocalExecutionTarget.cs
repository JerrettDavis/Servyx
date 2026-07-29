using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Servyx.Domain.Transport;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Servyx.Infrastructure.Process;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation for a workload running directly on the machine Servyx
/// itself is running on: commands become <see cref="DiagnosticsProcess"/> instances, and files are read and
/// written through the local filesystem, sandboxed to a single root directory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Commands are argv arrays, and there is no shell anywhere in this file.</strong> Every argument in
/// a <see cref="CommandSpec"/> is added to <see cref="ProcessStartInfo.ArgumentList"/>, which the runtime
/// escapes itself when it builds the OS-level command line, and <see cref="ProcessStartInfo.UseShellExecute"/>
/// is <see langword="false"/>, so no command interpreter is ever involved. This is strictly stronger than the
/// SSH transport's position: SSH <c>exec</c> has no argv-vector wire form at all, so
/// <c>Servyx.Infrastructure.Ssh.PosixArgv</c> has to quote each element into a single line and rely on the
/// remote shell honouring the quoting. Here there is no line to quote — an argument containing
/// <c>; rm -rf /</c> or <c>&amp; del /f</c> is handed to the OS as one opaque element and has nowhere to be
/// parsed as syntax.
/// </para>
/// <para>
/// <strong>The sandbox root is enforced, not advisory.</strong> Every <see cref="TargetPath"/> is combined
/// with the configured root and re-checked for containment before any I/O, and the path is additionally
/// walked for symlinks/junctions whose final target leaves the root — the canonicalisation step
/// <see cref="SandboxedPathResolver"/>'s own remarks require of infrastructure but that a purely lexical
/// resolver cannot perform. Note the contrast with <c>SftpFileChannel</c>, which re-prepends <c>/</c> to a
/// <see cref="TargetPath.Value"/> and therefore treats the root as a naming convention rather than a fence.
/// </para>
/// </remarks>
public sealed class LocalExecutionTarget : IExecutionTarget
{
    /// <summary>The default timeout applied to a <see cref="CommandSpec"/> that does not specify its own.</summary>
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The infix every temporary file an atomic write creates carries, so a leftover from an interrupted
    /// write is recognisable (and so a test can assert none was left behind).
    /// </summary>
    public const string TemporaryFileInfix = ".servyx-tmp-";

    private readonly string _root;
    private readonly string _rootTrimmed;
    private readonly SandboxedPathResolver _paths;
    private readonly TimeSpan _defaultCommandTimeout;
    private bool _disposed;

    /// <summary>Creates a target sandboxed to <paramref name="rootPath"/>.</summary>
    /// <param name="rootPath">
    /// The directory every <see cref="TargetPath"/> passed to this instance is relative to, and outside which
    /// no I/O is permitted. Normalised (and, if it is itself a link, canonicalised) at construction.
    /// </param>
    /// <param name="defaultCommandTimeout">
    /// Applied to a <see cref="CommandSpec"/> that does not specify its own <see cref="CommandSpec.Timeout"/>.
    /// Defaults to <see cref="DefaultCommandTimeout"/>; a non-positive value means "no timeout".
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is null, empty, or whitespace.</exception>
    public LocalExecutionTarget(string rootPath, TimeSpan? defaultCommandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _root = CanonicalizeRoot(rootPath);
        _rootTrimmed = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _paths = new SandboxedPathResolver(_root);
        _defaultCommandTimeout = defaultCommandTimeout ?? DefaultCommandTimeout;
    }

    /// <summary>The canonicalised absolute directory this session is sandboxed to.</summary>
    public string RootPath => _root;

    /// <summary>
    /// The sanctioned way to obtain a <see cref="TargetPath"/> scoped to this session's root. Delegates to
    /// <see cref="SandboxedPathResolver"/> rather than introducing a second sandboxing mechanism, so a path
    /// that would escape the root is rejected at <see cref="TargetPath"/> construction time.
    /// </summary>
    /// <exception cref="PathEscapesSandboxException"><paramref name="relativeOrAbsolute"/> leaves the root.</exception>
    public TargetPath Resolve(string relativeOrAbsolute) => _paths.Resolve(relativeOrAbsolute);

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> a <see cref="CommandSpec"/> becomes. Exposed because it is
    /// the single point at which Servyx's argv array meets the OS, and therefore the thing an injection test
    /// must be able to inspect directly: <see cref="ProcessStartInfo.Arguments"/> is left empty and every
    /// element goes into <see cref="ProcessStartInfo.ArgumentList"/>, which is what makes a hostile argument
    /// inert.
    /// </summary>
    /// <param name="spec">The command to render.</param>
    /// <param name="workingDirectory">The already-resolved working directory to start the process in.</param>
    public static ProcessStartInfo BuildStartInfo(CommandSpec spec, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.EnvironmentOverrides is not null)
        {
            foreach (var pair in spec.EnvironmentOverrides)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }

    /// <inheritdoc />
    /// <remarks>
    /// stdout and stderr are drained concurrently with the wait, so a command that fills a pipe buffer cannot
    /// deadlock the wait. A missing executable surfaces as the runtime's own
    /// <see cref="System.ComponentModel.Win32Exception"/> rather than being reshaped into a Servyx exception:
    /// "this binary is not installed" is not a transport failure and should not be reported as one.
    /// </remarks>
    /// <exception cref="TimeoutException">The command did not complete within its effective timeout.</exception>
    public async Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);

        var workingDirectory = ResolveWorkingDirectory(spec);
        var timeout = EffectiveTimeout(spec);

        using var process = new DiagnosticsProcess { StartInfo = BuildStartInfo(spec, workingDirectory) };
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        var stopwatch = Stopwatch.StartNew();
        process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            stopwatch.Stop();

            return new CommandResult(process.ExitCode, output, error, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveAsync(stdout, stderr).ConfigureAwait(false);

            if (!ct.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"Command '{spec.Executable}' did not complete within {timeout}.");
            }

            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Genuinely incremental: stdout and stderr are consumed through
    /// <see cref="DiagnosticsProcess.OutputDataReceived"/>/<see cref="DiagnosticsProcess.ErrorDataReceived"/>
    /// and published to an unbounded channel the enumerator drains, so a chunk is yielded as soon as the
    /// process emits it rather than after it exits. Chunking is per line, because that is the granularity the
    /// runtime's async readers deliver; the <see cref="OutputChunk.Text"/> carries no trailing newline.
    /// Abandoning the enumeration early kills the process rather than leaving it running unattended.
    /// </remarks>
    public async IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);

        var workingDirectory = ResolveWorkingDirectory(spec);
        var timeout = EffectiveTimeout(spec);

        using var process = new DiagnosticsProcess { StartInfo = BuildStartInfo(spec, workingDirectory) };
        var channel = Channel.CreateUnbounded<OutputChunk>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        process.OutputDataReceived += (_, e) => Publish(channel, OutputStream.StdOut, e.Data);
        process.ErrorDataReceived += (_, e) => Publish(channel, OutputStream.StdErr, e.Data);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completion = PumpAsync(process, channel, spec.Executable, timeout, linked.Token, ct);

        try
        {
            await foreach (var chunk in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
        finally
        {
            TryKill(process);
        }

        await completion.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var local = ToLocalPath(path);
        return Task.FromResult(File.Exists(local) || Directory.Exists(local));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="FileStat.Sha256"/> is left null, matching <c>SftpFileChannel</c> and
    /// <c>DockerExecutionTarget</c>: a stat is a metadata read, and hashing every file a caller merely asked
    /// about would turn it into a full content read. <see cref="FileStat.Owner"/>/<see cref="FileStat.Group"/>
    /// and the numeric uid/gid stay null because .NET exposes no portable accessor for them;
    /// <see cref="FileStat.Mode"/> is populated from the POSIX mode on platforms that have one and left null
    /// on Windows, which is exactly what <see cref="FileStat.PermitsWriteBy"/> documents as the Windows shape.
    /// </remarks>
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var local = ToLocalPath(path);

        if (Directory.Exists(local))
        {
            var directory = new DirectoryInfo(local);
            return Task.FromResult(new FileStat(true, true, null, directory.LastWriteTimeUtc, null)
            {
                Mode = TryReadPosixMode(directory),
                IsSymlink = directory.LinkTarget is not null,
            });
        }

        if (File.Exists(local))
        {
            var file = new FileInfo(local);
            return Task.FromResult(new FileStat(true, false, TryReadLength(file), file.LastWriteTimeUtc, null)
            {
                Mode = TryReadPosixMode(file),
                IsSymlink = file.LinkTarget is not null,
            });
        }

        return Task.FromResult(new FileStat(false, false, null, null, null));
    }

    /// <inheritdoc />
    /// <exception cref="DirectoryNotFoundException"><paramref name="path"/> is not an existing directory.</exception>
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var local = ToLocalPath(path);
        if (!Directory.Exists(local))
        {
            throw new DirectoryNotFoundException($"Directory '{local}' does not exist.");
        }

        var entries = new List<FileEntry>();
        foreach (var child in new DirectoryInfo(local).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();

            var isDirectory = child is DirectoryInfo;
            entries.Add(new FileEntry(
                child.Name,
                isDirectory,
                child is FileInfo file ? TryReadLength(file) : null,
                child.LastWriteTimeUtc));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return Task.FromResult<IReadOnlyList<FileEntry>>(entries);
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var local = ToLocalPath(path);
        Stream stream = new FileStream(
            local,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });

        return Task.FromResult(stream);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Atomic by the same construction <c>SftpFileChannel</c> uses, and for the same reason: content is
    /// written to <c>&lt;name&gt;.servyx-tmp-{guid}</c> <em>in the target's own directory</em> — a temp file
    /// elsewhere would make the rename non-atomic the moment it crossed a filesystem boundary — and then
    /// renamed over the target. <see cref="File.Move(string, string, bool)"/> is <c>rename(2)</c> on Unix and
    /// <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c> on Windows, both of which replace an existing
    /// destination atomically within a volume. A failure before the rename leaves the original file untouched
    /// and deletes the temp file.
    /// </para>
    /// <para>
    /// The drift check is performed <em>before</em> anything is written: the pre-image is hashed, compared,
    /// and the write refused outright, so a refused write creates no temp file and mutates nothing. The one
    /// asymmetry with the SFTP channel is ownership — that implementation must re-apply the original uid/gid
    /// because it may be writing as a different user over SSH, and refuses the write when it cannot. A local
    /// write is performed by the very process Servyx runs as, so there is no ownership to restore; only the
    /// POSIX mode is carried across, where the platform has one.
    /// </para>
    /// </remarks>
    /// <exception cref="TargetDriftException">
    /// <paramref name="options"/> specifies an <c>ExpectedPreImageHash</c> that does not match the file's
    /// current content. Thrown before any write occurs.
    /// </exception>
    public async Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var local = ToLocalPath(path);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var postImageHash = ComputeSha256Hex(bytes);

        var existed = File.Exists(local);
        var preImageHash = existed ? await ComputeFileSha256Async(local, ct).ConfigureAwait(false) : null;

        if (options.ExpectedPreImageHash is { } expected &&
            !string.Equals(expected, preImageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetDriftException(
                $"Content at '{local}' has drifted since it was last observed.", path, expected, preImageHash);
        }

        var directory = Path.GetDirectoryName(local) ?? _root;
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(local)}{TemporaryFileInfix}{Guid.NewGuid():N}");

        var renamed = false;
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, ct).ConfigureAwait(false);
            PreservePosixMode(local, temporaryPath, existed);
            File.Move(temporaryPath, local, overwrite: true);
            renamed = true;
        }
        finally
        {
            if (!renamed)
            {
                TryDeleteQuietly(temporaryPath);
            }
        }

        return new FileWriteReceipt(preImageHash, postImageHash, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deleting an absent file throws rather than succeeding silently, matching <c>SftpFileChannel</c> (whose
    /// underlying SFTP request errors) so that callers written against one transport behave identically on the
    /// other. <see cref="File.Delete(string)"/> on its own is a no-op for a missing path, which would have made
    /// the two transports disagree about whether anything was removed.
    /// </remarks>
    /// <exception cref="FileNotFoundException">No file exists at <paramref name="path"/>.</exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        var local = ToLocalPath(path);
        if (!File.Exists(local))
        {
            throw new FileNotFoundException($"File '{local}' was not found.", local);
        }

        File.Delete(local);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// There is no connection, handle, or client to release: a local session owns nothing beyond the resolved
    /// root string, and every process it starts is disposed by the call that started it. Disposal is still
    /// observed, so use-after-dispose fails loudly rather than silently continuing to work.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static void Publish(Channel<OutputChunk> channel, OutputStream stream, string? data)
    {
        // A null payload is the runtime's end-of-stream signal, not an empty line.
        if (data is not null)
        {
            channel.Writer.TryWrite(new OutputChunk(stream, data, DateTimeOffset.UtcNow));
        }
    }

    private static async Task PumpAsync(
        DiagnosticsProcess process,
        Channel<OutputChunk> channel,
        string executable,
        TimeSpan timeout,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        Exception? failure = null;
        try
        {
            await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);

            // The synchronous overload returns immediately for an already-exited process, and is the
            // documented way to guarantee the async output readers have been flushed before we stop reading.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!callerToken.IsCancellationRequested)
            {
                failure = new TimeoutException($"Command '{executable}' did not complete within {timeout}.");
            }
        }
        finally
        {
            channel.Writer.TryComplete(failure);
        }
    }

    private static async Task ObserveAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Observed purely so the faulted read tasks are not left unobserved; the caller is already
            // throwing a more meaningful exception about the cancellation or timeout that caused this.
        }
    }

    private static void TryKill(DiagnosticsProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process is already gone, or the OS refused the kill. Either way there is nothing further
            // this method can do, and it is only ever called on a path that is already failing.
        }
    }

    private TimeSpan EffectiveTimeout(CommandSpec spec)
    {
        var timeout = spec.Timeout ?? _defaultCommandTimeout;
        return timeout > TimeSpan.Zero ? timeout : Timeout.InfiniteTimeSpan;
    }

    private string ResolveWorkingDirectory(CommandSpec spec)
    {
        var directory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? _root
            : ToLocalPath(_paths.Resolve(spec.WorkingDirectory));

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Working directory '{directory}' does not exist, so '{spec.Executable}' cannot be started in it.");
        }

        return directory;
    }

    /// <summary>
    /// Turns a <see cref="TargetPath"/> into the absolute local path it names, re-verifying containment
    /// lexically and then through the filesystem's own link resolution.
    /// </summary>
    private string ToLocalPath(TargetPath path)
    {
        if (path.Value is null)
        {
            throw new ArgumentException(
                "A default-initialized TargetPath is not a validated path. Obtain one from Resolve(string).",
                nameof(path));
        }

        var full = path.Value.Length == 0
            ? _root
            : Path.GetFullPath(Path.Combine(_root, path.Value.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsWithinRoot(full))
        {
            throw new PathEscapesSandboxException(
                $"Path '{path.Value}' resolves to '{full}', which is outside the sandbox root '{_root}'.", path.Value);
        }

        EnsureNoLinkEscape(full);
        return full;
    }

    private bool IsWithinRoot(string full)
    {
        var comparison = PathComparison;
        return full.Equals(_root, comparison)
            || full.Equals(_rootTrimmed, comparison)
            || full.StartsWith(_rootTrimmed + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Walks <paramref name="full"/> back to the sandbox root, and for every path component that currently
    /// exists, resolves it to its final link target and re-checks containment.
    /// </summary>
    /// <remarks>
    /// <see cref="SandboxedPathResolver"/> is explicitly lexical and its own remarks require that
    /// "infrastructure implementations that turn a <see cref="TargetPath"/> into real I/O MUST canonicalize
    /// the fully resolved path ... and re-verify containment". A local target is the one transport that can
    /// actually honour that, because the filesystem in question is right here:
    /// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> follows symlinks, Windows junctions, and reparse
    /// points to their final target. A component whose link cannot be resolved at all is refused rather than
    /// assumed contained — an unreadable link is not evidence of safety.
    /// </remarks>
    private void EnsureNoLinkEscape(string full)
    {
        var comparison = PathComparison;
        var current = full;

        while (!string.IsNullOrEmpty(current))
        {
            if (current.Equals(_root, comparison) || current.Equals(_rootTrimmed, comparison))
            {
                return;
            }

            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            if (entry is not null)
            {
                FileSystemInfo? linkTarget;
                try
                {
                    linkTarget = entry.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new PathEscapesSandboxException(
                        $"The link at '{current}' could not be resolved, so containment within '{_root}' cannot be established.",
                        full);
                }

                if (linkTarget is not null && !IsWithinRoot(Path.GetFullPath(linkTarget.FullName)))
                {
                    throw new PathEscapesSandboxException(
                        $"Path '{full}' resolves through a link at '{current}' whose target leaves the sandbox root '{_root}'.",
                        full);
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, comparison))
            {
                return;
            }

            current = parent;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string CanonicalizeRoot(string rootPath)
    {
        var full = Path.GetFullPath(rootPath);
        try
        {
            var directory = new DirectoryInfo(full);
            if (directory.Exists && directory.ResolveLinkTarget(returnFinalTarget: true) is { } resolved)
            {
                full = Path.GetFullPath(resolved.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The root itself could not be canonicalised (it may not exist yet — a provisioner legitimately
            // targets a directory it is about to create). The lexical form is still a valid fence; every
            // subsequent I/O re-checks link containment anyway.
        }

        return full;
    }

    private static long? TryReadLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            // A dangling link, or an entry removed between enumeration and stat. Reporting an unknown size is
            // better than failing an entire directory listing.
            return null;
        }
    }

    private static int? TryReadPosixMode(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return (int)info.UnixFileMode & 0x1FF;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void PreservePosixMode(string targetPath, string temporaryPath, bool targetExisted)
    {
        if (OperatingSystem.IsWindows() || !targetExisted)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(targetPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A file that ends up with the process's default mode rather than the original's is not the
            // dangerous failure mode here; refusing the write over it would be worse.
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp file on a path that is already failing.
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            });

        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
