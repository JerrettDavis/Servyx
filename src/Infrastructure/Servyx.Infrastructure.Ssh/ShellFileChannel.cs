using System.Globalization;
using Renci.SshNet;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation that synthesizes file operations over a plain SSH exec
/// channel, for the "sftp subsystem disabled but a shell is available" deployment shape described in
/// <c>docs/connectors.md</c>, "SSH and SFTP are independent". Exec-only servers are a genuinely common
/// configuration, not a hypothetical: this is the "take what we can get" path for that class of user.
/// </summary>
/// <remarks>
/// <para>
/// Writes use <c>cat &gt; path</c>, piping content through the command's stdin — this is binary-safe
/// (unlike echoing through command-line arguments, which would both blow past argv length limits and
/// mangle bytes the shell treats specially). Reads use <c>base64 -w0 -- path</c> to survive the shell's
/// text-mode assumptions (a raw <c>cat</c> of binary content through <see cref="Renci.SshNet.SshCommand.Result"/>
/// — a <see cref="string"/> — would corrupt anything that isn't valid text in the connection's encoding).
/// Metadata uses <c>stat -c '%f %u %g %s %Y' -- path</c>.
/// </para>
/// <para>
/// This channel is capped at <see cref="MaxFileSizeBytes"/> per file, and refuses to write any content it
/// detects as binary (a synthesized binary-safe <em>write</em> over a text shell channel is not attempted —
/// see <see cref="LooksBinary"/>). Both limits exist because this is deliberately a reduced-capability
/// fallback: slower, size-capped, and reported as such, rather than an attempt to make an exec-only host
/// look like a full SFTP one.
/// </para>
/// </remarks>
public sealed class ShellFileChannel : IExecutionTarget
{
    /// <summary>The maximum file size, in bytes, this channel will read or write.</summary>
    public const long MaxFileSizeBytes = 8 * 1024 * 1024;

    private readonly SshClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _commandTimeout;
    private bool _disposed;

    /// <summary>Creates a channel that synthesizes file operations over an already-connected <paramref name="client"/>.</summary>
    /// <param name="client">An already-connected <see cref="SshClient"/> with a working shell.</param>
    /// <param name="ownsClient">Whether this instance disposes <paramref name="client"/> when it is itself disposed.</param>
    /// <param name="commandTimeout">Timeout applied to each synthesized shell command.</param>
    public ShellFileChannel(SshClient client, bool ownsClient, TimeSpan commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _ownsClient = ownsClient;
        _commandTimeout = commandTimeout;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: use <see cref="SshExecChannel"/> for command execution.</exception>
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("ShellFileChannel is file-only; use SshExecChannel for command execution.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: use <see cref="SshExecChannel"/> for command execution.</exception>
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("ShellFileChannel is file-only; use SshExecChannel for command execution.");

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var result = await RunAsync($"test -e {Quote(ToRemotePath(path))}", ct).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    /// <inheritdoc />
    public async Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var remotePath = ToRemotePath(path);
        var result = await RunAsync($"stat -c '%f %u %g %s %Y' -- {Quote(remotePath)} 2>/dev/null", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new FileStat(false, false, null, null, null);
        }

        var parts = result.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawMode) ||
            !int.TryParse(parts[1], out var uid) ||
            !int.TryParse(parts[2], out var gid) ||
            !long.TryParse(parts[3], out var size) ||
            !long.TryParse(parts[4], out var epochSeconds))
        {
            return new FileStat(false, false, null, null, null);
        }

        const uint FormatMask = 0xF000;
        const uint DirectoryFormat = 0x4000;
        const uint SymlinkFormat = 0xA000;

        var isDirectory = (rawMode & FormatMask) == DirectoryFormat;
        var isSymlink = (rawMode & FormatMask) == SymlinkFormat;
        var modifiedAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);

        return new FileStat(true, isDirectory, isDirectory ? null : size, modifiedAt, null)
        {
            Mode = (int)(rawMode & 0x1FF),
            Uid = uid,
            Gid = gid,
            IsSymlink = isSymlink,
        };
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: a plain shell has no efficient, bounded way to list a directory's entries with
    /// metadata short of parsing <c>ls</c> output, which this deliberately reduced-capability channel does
    /// not attempt.
    /// </exception>
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("ShellFileChannel does not support directory listing.");

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var remotePath = ToRemotePath(path);
        var sizeResult = await RunAsync($"stat -c %s -- {Quote(remotePath)}", ct).ConfigureAwait(false);
        if (sizeResult.ExitCode != 0)
        {
            throw new FileNotFoundException($"File '{remotePath}' was not found.", remotePath);
        }

        if (long.TryParse(sizeResult.StandardOutput.Trim(), out var size) && size > MaxFileSizeBytes)
        {
            throw new NotSupportedException(
                $"'{remotePath}' is {size} bytes, exceeding ShellFileChannel's {MaxFileSizeBytes}-byte cap for the exec-only fallback path.");
        }

        var result = await RunAsync($"base64 -w0 -- {Quote(remotePath)}", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Reading '{remotePath}' failed: {result.StandardError}");
        }

        var bytes = Convert.FromBase64String(result.StandardOutput.Trim());
        return new MemoryStream(bytes, writable: false);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// <paramref name="content"/> exceeds <see cref="MaxFileSizeBytes"/>, or is detected as binary — see
    /// <see cref="LooksBinary"/>.
    /// </exception>
    /// <exception cref="TargetDriftException">
    /// <paramref name="options"/> specifies an <c>ExpectedPreImageHash</c> that does not match the file's
    /// current content.
    /// </exception>
    public async Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var remotePath = ToRemotePath(path);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        if (bytes.LongLength > MaxFileSizeBytes)
        {
            throw new NotSupportedException(
                $"Content for '{remotePath}' is {bytes.LongLength} bytes, exceeding ShellFileChannel's {MaxFileSizeBytes}-byte cap for the exec-only fallback path.");
        }

        if (LooksBinary(bytes))
        {
            throw new NotSupportedException(
                $"Content for '{remotePath}' was detected as binary; ShellFileChannel refuses to synthesize a binary-safe write over a text shell channel.");
        }

        string? preImageHash = null;
        var existsResult = await RunAsync($"test -e {Quote(remotePath)}", ct).ConfigureAwait(false);
        if (existsResult.ExitCode == 0)
        {
            var current = await OpenReadAsync(path, ct).ConfigureAwait(false);
            await using (current)
            {
                using var currentBuffer = new MemoryStream();
                await current.CopyToAsync(currentBuffer, ct).ConfigureAwait(false);
                preImageHash = ComputeSha256Hex(currentBuffer.ToArray());
            }
        }

        if (options.ExpectedPreImageHash is { } expected &&
            !string.Equals(expected, preImageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetDriftException(
                $"Content at '{remotePath}' has drifted since it was last observed.", path, expected, preImageHash);
        }

        using var command = _client.CreateCommand($"cat > {Quote(remotePath)}");
        command.CommandTimeout = _commandTimeout;

        var executeTask = command.ExecuteAsync(ct);
        await using (var stdin = command.CreateInputStream())
        {
            await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        await executeTask.ConfigureAwait(false);

        if (command.ExitStatus != 0)
        {
            throw new IOException($"Writing '{remotePath}' failed: {command.Error}");
        }

        return new FileWriteReceipt(preImageHash, ComputeSha256Hex(bytes), DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var result = await RunAsync($"rm -f -- {Quote(ToRemotePath(path))}", ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new IOException($"Deleting '{ToRemotePath(path)}' failed: {result.StandardError}");
        }
    }

    /// <summary>
    /// A simple, conservative binary-content heuristic: content containing a NUL byte anywhere, or an
    /// excessive proportion of other non-printable, non-whitespace control bytes in its first 8000 bytes, is
    /// treated as binary. This deliberately errs toward refusing borderline content — a false "binary" on a
    /// genuinely-text file only costs the user a fallback to a real SFTP/direct write path; a false "text" on
    /// genuinely-binary content risks a byte-mangling write that silently corrupts a save file or archive.
    /// </summary>
    private static bool LooksBinary(byte[] content)
    {
        var sampleLength = Math.Min(content.Length, 8000);
        var controlByteCount = 0;

        for (var i = 0; i < sampleLength; i++)
        {
            var b = content[i];
            if (b == 0)
            {
                return true;
            }

            var isPrintableOrCommonWhitespace = b is 9 or 10 or 13 || b is >= 32 and < 127 || b >= 128;
            if (!isPrintableOrCommonWhitespace)
            {
                controlByteCount++;
            }
        }

        return sampleLength > 0 && controlByteCount * 100 / sampleLength > 5;
    }

    private async Task<CommandResult> RunAsync(string commandLine, CancellationToken ct)
    {
        using var command = _client.CreateCommand(commandLine);
        command.CommandTimeout = _commandTimeout;
        await command.ExecuteAsync(ct).ConfigureAwait(false);
        return new CommandResult(command.ExitStatus ?? -1, command.Result, command.Error, TimeSpan.Zero);
    }

    private static string Quote(string value) => PosixArgv.QuoteArgument(value);

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ToRemotePath(TargetPath path) => "/" + path.Value;

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
