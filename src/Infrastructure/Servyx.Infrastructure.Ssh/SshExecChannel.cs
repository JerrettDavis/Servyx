using System.Diagnostics;
using System.Runtime.CompilerServices;
using Renci.SshNet;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// <see cref="IExecutionTarget"/> implementation that runs <see cref="CommandSpec"/> argv arrays over an
/// already-connected SSH <c>exec</c> session. Exec-only: every file operation throws
/// <see cref="NotSupportedException"/>, since a bare SSH exec channel has no native file-transfer
/// capability of its own (see <see cref="ShellFileChannel"/> for a shell-synthesized fallback, and
/// <see cref="CompositeExecutionTarget"/> for how the two compose).
/// </summary>
/// <remarks>
/// <b>Never builds a shell string by concatenating caller-controlled text.</b> Every argument in a
/// <see cref="CommandSpec"/> is individually quoted via <see cref="PosixArgv.QuoteArgument"/> before being
/// joined into the single command-line string the SSH <c>exec</c> request carries — see the remarks on
/// <see cref="PosixArgv"/> for why that is unavoidable (SSH exec has no argv-vector form) and why
/// single-quote wrapping alone is sufficient to neutralize shell metacharacters in a hostile argument.
/// </remarks>
public sealed class SshExecChannel : IExecutionTarget
{
    private readonly SshClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _defaultCommandTimeout;
    private bool _disposed;

    /// <summary>Creates a channel that runs commands over an already-connected <paramref name="client"/>.</summary>
    /// <param name="client">An already-connected <see cref="SshClient"/>.</param>
    /// <param name="ownsClient">Whether this instance disposes <paramref name="client"/> when it is itself disposed.</param>
    /// <param name="defaultCommandTimeout">Applied to a <see cref="CommandSpec"/> that does not specify its own <see cref="CommandSpec.Timeout"/>.</param>
    public SshExecChannel(SshClient client, bool ownsClient, TimeSpan defaultCommandTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _ownsClient = ownsClient;
        _defaultCommandTimeout = defaultCommandTimeout;
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);

        var commandLine = BuildCommandLine(spec);

        using var command = _client.CreateCommand(commandLine);
        command.CommandTimeout = spec.Timeout ?? _defaultCommandTimeout;

        var stopwatch = Stopwatch.StartNew();
        await command.ExecuteAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();

        return new CommandResult(command.ExitStatus ?? -1, command.Result, command.Error, stopwatch.Elapsed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// SSH.NET exposes a completed command's stdout/stderr as <see cref="Renci.SshNet.SshCommand.OutputStream"/>
    /// and <see cref="Renci.SshNet.SshCommand.ExtendedOutputStream"/> — <c>PipeStream</c>s that support
    /// concurrent read-while-write in principle, but whose blocking-read semantics make a safe, truly
    /// incremental drain loop (without risking a read that blocks past command completion) more machinery
    /// than this milestone's callers need. This implementation runs the command to completion and then
    /// yields at most one <see cref="OutputStream.StdOut"/> chunk followed by at most one
    /// <see cref="OutputStream.StdErr"/> chunk — correct output, delivered as a whole rather than
    /// incrementally. True incremental streaming (for live console attach) is expected to layer a
    /// <c>ShellStream</c>-based implementation on top of this later, without changing this method's
    /// signature or contract.
    /// </remarks>
    public async IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(spec);

        var commandLine = BuildCommandLine(spec);

        using var command = _client.CreateCommand(commandLine);
        command.CommandTimeout = spec.Timeout ?? _defaultCommandTimeout;

        await command.ExecuteAsync(ct).ConfigureAwait(false);

        if (command.Result.Length > 0)
        {
            yield return new OutputChunk(OutputStream.StdOut, command.Result, DateTimeOffset.UtcNow);
        }

        if (command.Error.Length > 0)
        {
            yield return new OutputChunk(OutputStream.StdErr, command.Error, DateTimeOffset.UtcNow);
        }
    }

    private static string BuildCommandLine(CommandSpec spec) =>
        PosixArgv.BuildCommandLine(spec.Executable, spec.Arguments, spec.WorkingDirectory, spec.EnvironmentOverrides);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no file-read capability.</exception>
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no file-read capability.</exception>
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no directory-listing capability.</exception>
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no file-read capability.</exception>
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no file-write capability.</exception>
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown: a bare SSH exec channel has no file-delete capability.</exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        throw new NotSupportedException("SshExecChannel is exec-only; use SftpFileChannel or ShellFileChannel for file operations.");

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
