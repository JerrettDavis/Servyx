using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation for the file half of an SSH connector, backed by SFTP.
/// File-only: every exec operation throws <see cref="NotSupportedException"/> — see
/// <see cref="SshExecChannel"/> for the exec half, and <see cref="CompositeExecutionTarget"/> for how the
/// two compose (SSH exec and SFTP are independent capabilities; see <c>docs/connectors.md</c>).
/// </summary>
public sealed class SftpFileChannel : IExecutionTarget
{
    private readonly ISftpClient _client;
    private readonly bool _ownsClient;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Creates a channel backed by an already-connected <paramref name="client"/>.</summary>
    /// <param name="client">
    /// An already-connected client. Typed as <see cref="ISftpClient"/> rather than the concrete
    /// <see cref="SftpClient"/> — which every real construction site still passes, since it implements this
    /// interface — purely so tests can substitute a fake/mock without a live SSH connection.
    /// </param>
    /// <param name="ownsClient">Whether this instance disposes <paramref name="client"/> when it is itself disposed.</param>
    /// <param name="logger">
    /// Used to record non-fatal but noteworthy events — in particular, a non-atomic write fallback when the
    /// server lacks the <c>posix-rename@openssh.com</c> extension. Defaults to a no-op logger.
    /// </param>
    public SftpFileChannel(ISftpClient client, bool ownsClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _ownsClient = ownsClient;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SFTP channel has no exec capability.</exception>
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("SftpFileChannel is file-only; use SshExecChannel for command execution.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SFTP channel has no exec capability.</exception>
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        throw new NotSupportedException("SftpFileChannel is file-only; use SshExecChannel for command execution.");

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _client.ExistsAsync(ToRemotePath(path), ct);
    }

    /// <inheritdoc />
    public async Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var remotePath = ToRemotePath(path);
        ISftpFile file;
        try
        {
            file = await _client.GetAsync(remotePath, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return new FileStat(false, false, null, null, null);
        }

        return new FileStat(true, file.IsDirectory, file.IsDirectory ? null : file.Attributes.Size, file.LastWriteTimeUtc, null)
        {
            Mode = ComputePermissionBits(file.Attributes),
            Owner = null,
            Group = null,
            Uid = file.Attributes.UserId,
            Gid = file.Attributes.GroupId,
            IsReadOnlyMount = false,
            IsSymlink = file.IsSymbolicLink,
        };
    }

    /// <inheritdoc />
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="path"/> does not exist on the remote host. Translated from SSH.NET's
    /// <see cref="SftpPathNotFoundException"/> the same way <see cref="StatAsync"/> and
    /// <see cref="OpenReadAsync"/> already translate it for their own not-found cases — a caller that
    /// distinguishes "this path does not exist" from "this read otherwise failed" (see
    /// <c>Servyx.Web.Services.LiveDashboardDataService.ReadSavesAsync</c>, which treats a missing world root
    /// as a genuine empty result and anything else as a failure) needs that distinction to hold identically
    /// across every <see cref="IExecutionTarget"/> implementation, not just <see cref="Servyx.Infrastructure.Docker.DockerExecutionTarget"/>'s.
    /// Before this fix, this method alone let <see cref="SftpPathNotFoundException"/> propagate uncaught,
    /// which inverted that distinction for a missing directory specifically: a genuinely empty world root
    /// over SSH reported as a read failure instead of "no saves yet".
    /// </exception>
    public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var remotePath = ToRemotePath(path);
        var entries = new List<FileEntry>();

        try
        {
            await foreach (var file in _client.ListDirectoryAsync(remotePath, ct).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                entries.Add(new FileEntry(
                    file.Name,
                    file.IsDirectory,
                    file.IsDirectory ? null : file.Attributes.Size,
                    file.LastWriteTimeUtc));
            }
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new DirectoryNotFoundException($"Directory '{remotePath}' does not exist.", ex);
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var remotePath = ToRemotePath(path);
        try
        {
            Stream stream = _client.OpenRead(remotePath);
            return Task.FromResult(stream);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new FileNotFoundException($"File '{remotePath}' was not found.", remotePath, ex);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Writes are atomic when the server supports the <c>posix-rename@openssh.com</c> extension: content is
    /// uploaded to <c>&lt;name&gt;.servyx-tmp-{guid}</c> in the same directory as the target (a temp path
    /// under a different directory, let alone <c>/tmp</c>, would make the rename non-atomic the moment it
    /// crosses a filesystem boundary), the original file's mode and owner are applied to the temp file where
    /// possible, and then the temp file is renamed over the target with <c>SSH_FXP_EXTENDED
    /// posix-rename@openssh.com</c> — the only SFTP rename variant guaranteed to replace an existing target
    /// atomically.
    /// </para>
    /// <para>
    /// SSH.NET reports the extension's absence as a synchronous <see cref="NotSupportedException"/> (it
    /// checks the extension list the server advertised during the SFTP version exchange, before sending any
    /// request), which this method uses as the probe: attempt the posix rename, and on
    /// <see cref="NotSupportedException"/> specifically, fall back to a non-atomic truncate-and-write
    /// directly onto the target and log a warning. <see cref="FileWriteReceipt"/> has no field to carry an
    /// "this write was non-atomic" flag (that type lives in <c>Servyx.Domain.Transport</c>, out of this
    /// project's scope to modify), so the warning is surfaced via <see cref="ILogger"/> instead.
    /// </para>
    /// </remarks>
    /// <exception cref="TargetDriftException">
    /// <paramref name="options"/> specifies an <c>ExpectedPreImageHash</c> that does not match the file's
    /// current content.
    /// </exception>
    /// <exception cref="OwnershipPreservationFailedException">
    /// The target previously existed with a known owner, and that owner could not be preserved on the
    /// written content.
    /// </exception>
    public async Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfBeyondPlainAtomicRename(nameof(SftpFileChannel));

        var remotePath = ToRemotePath(path);
        var (directory, fileName) = SplitPath(remotePath);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        var postImageHash = ComputeSha256Hex(bytes);

        string? preImageHash = null;
        ISftpFile? existing = null;
        if (await _client.ExistsAsync(remotePath, ct).ConfigureAwait(false))
        {
            existing = await _client.GetAsync(remotePath, ct).ConfigureAwait(false);
            preImageHash = await ComputeRemoteSha256Async(remotePath, ct).ConfigureAwait(false);
        }

        if (options.ExpectedPreImageHash is { } expected &&
            !string.Equals(expected, preImageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new TargetDriftException(
                $"Content at '{remotePath}' has drifted since it was last observed.", path, expected, preImageHash);
        }

        var tempPath = directory.Length == 0
            ? $"{fileName}.servyx-tmp-{Guid.NewGuid():N}"
            : $"{directory}/{fileName}.servyx-tmp-{Guid.NewGuid():N}";

        await using (var uploadStream = new MemoryStream(bytes, writable: false))
        {
            await _client.UploadFileAsync(uploadStream, tempPath, ct).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            PreserveModeAndOwner(tempPath, existing);
        }

        var atomic = TryPosixRename(tempPath, remotePath);
        if (!atomic)
        {
            _logger.LogWarning(
                "SFTP server does not support posix-rename@openssh.com; write to '{Path}' fell back to a " +
                "non-atomic truncate-and-write. A failure mid-write can leave the file partially written.",
                remotePath);

            try
            {
                await using var directStream = new MemoryStream(bytes, writable: false);
                await _client.UploadFileAsync(directStream, remotePath, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteQuietly(tempPath);
            }
        }

        return new FileWriteReceipt(preImageHash, postImageHash, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _client.DeleteFileAsync(ToRemotePath(path), ct);
    }

    /// <summary>
    /// Attempts the atomic <c>posix-rename@openssh.com</c> rename of <paramref name="tempPath"/> over
    /// <paramref name="targetPath"/>. Returns <see langword="false"/> (rather than throwing) specifically
    /// when the server does not advertise the extension, which SSH.NET surfaces as a synchronous
    /// <see cref="NotSupportedException"/> raised before any request is sent — see the remarks on
    /// <see cref="WriteFileAsync"/>.
    /// </summary>
    private bool TryPosixRename(string tempPath, string targetPath)
    {
        try
        {
            _client.RenameFile(tempPath, targetPath, isPosix: true);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies <paramref name="existing"/>'s mode and, where permitted, owner to the file at
    /// <paramref name="tempPath"/>. Mode failures are logged and swallowed (a config file that ends up
    /// slightly too permissive is not the dangerous failure mode here); an owner that cannot be preserved
    /// throws <see cref="OwnershipPreservationFailedException"/>, refusing the write outright, per
    /// <c>docs/control-plane.md</c>'s rung-1 rule that a write must never silently produce a file the game
    /// process can no longer read after restart.
    /// </summary>
    private void PreserveModeAndOwner(string tempPath, ISftpFile existing)
    {
        try
        {
            _client.ChangePermissions(tempPath, (short)ComputePermissionBits(existing.Attributes));
        }
        catch (Exception ex) when (ex is SshException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not preserve file mode on '{Path}'.", tempPath);
        }

        var originalUid = existing.Attributes.UserId;
        var originalGid = existing.Attributes.GroupId;

        try
        {
            var tempFile = _client.Get(tempPath);
            if (tempFile.Attributes.UserId == originalUid && tempFile.Attributes.GroupId == originalGid)
            {
                return; // Already correct (e.g. same-user write) — nothing to do.
            }

            tempFile.Attributes.UserId = originalUid;
            tempFile.Attributes.GroupId = originalGid;
            tempFile.UpdateStatus();
        }
        catch (Exception ex) when (ex is SshException or UnauthorizedAccessException)
        {
            TryDeleteQuietly(tempPath);
            throw new OwnershipPreservationFailedException(tempPath, originalUid, originalGid, ex);
        }
    }

    private void TryDeleteQuietly(string path)
    {
        try
        {
            _client.DeleteFile(path);
        }
        catch (Exception ex) when (ex is SshException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Could not clean up temporary file '{Path}'.", path);
        }
    }

    private async Task<string> ComputeRemoteSha256Async(string remotePath, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await _client.DownloadFileAsync(remotePath, buffer, ct).ConfigureAwait(false);
        return ComputeSha256Hex(buffer.ToArray());
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Reconstructs the low-9-bit POSIX permission mode (<c>rwxrwxrwx</c>) from <paramref name="attributes"/>'
    /// public boolean accessors. <see cref="SftpFileAttributes"/> does not expose its raw permissions bitfield
    /// publicly, only these per-bit booleans, so the mode is rebuilt bit by bit to match the encoding
    /// <see cref="FileStat.Mode"/> and <see cref="FileStat.PermitsWriteBy"/> document (owner write = <c>0x80</c>
    /// / 0200 octal, and so on).
    /// </summary>
    private static int ComputePermissionBits(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.OwnerCanRead)
        {
            mode |= 0x100; // 0400
        }

        if (attributes.OwnerCanWrite)
        {
            mode |= 0x080; // 0200
        }

        if (attributes.OwnerCanExecute)
        {
            mode |= 0x040; // 0100
        }

        if (attributes.GroupCanRead)
        {
            mode |= 0x020; // 0040
        }

        if (attributes.GroupCanWrite)
        {
            mode |= 0x010; // 0020
        }

        if (attributes.GroupCanExecute)
        {
            mode |= 0x008; // 0010
        }

        if (attributes.OthersCanRead)
        {
            mode |= 0x004; // 0004
        }

        if (attributes.OthersCanWrite)
        {
            mode |= 0x002; // 0002
        }

        if (attributes.OthersCanExecute)
        {
            mode |= 0x001; // 0001
        }

        return mode;
    }

    private static (string Directory, string FileName) SplitPath(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0
            ? (string.Empty, trimmed)
            : (trimmed[..lastSlash], trimmed[(lastSlash + 1)..]);
    }

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
