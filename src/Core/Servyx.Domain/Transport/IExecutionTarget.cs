namespace Servyx.Domain.Transport;

/// <summary>
/// An established session against a target, exposing the operations available once connected.
/// Implementations must be safe to hold open across multiple calls and must release underlying
/// resources on <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </summary>
public interface IExecutionTarget : IAsyncDisposable
{
    /// <summary>Executes a command to completion and returns its result.</summary>
    Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Executes a command, streaming stdout/stderr chunks as they arrive. Used for live console attach
    /// and long-running operations.
    /// </summary>
    IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default);

    /// <summary>Returns whether a path exists on the target.</summary>
    Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>Returns file metadata for a path on the target.</summary>
    Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>
    /// Lists the immediate contents of a directory. Deliberately non-recursive, so traversal depth is
    /// always bounded by the caller rather than by the transport.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>Opens a read-only stream over a file on the target.</summary>
    Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default);

    /// <summary>
    /// Writes a file. By default the write is atomic: content is written to a temporary sibling file and
    /// then renamed into place. Returns a receipt including the SHA-256 of the pre-image (or null if the
    /// file did not previously exist). If <paramref name="options"/> specifies an
    /// <c>ExpectedPreImageHash</c> that does not match the file's current content, the write is refused and
    /// <see cref="TargetDriftException"/> is thrown before any I/O occurs.
    /// </summary>
    /// <remarks>
    /// <see cref="FileWriteOptions.Strategy"/> and <see cref="FileWriteOptions.Mode"/> are requests an
    /// implementation must either honour or refuse with <see cref="NotSupportedException"/> — see
    /// <see cref="FileWriteOptions.ThrowIfBeyondPlainAtomicRename"/>. Ignoring either of them would return a
    /// success receipt for a write that did something other than what was asked, which is worse than a
    /// refusal because nothing downstream can tell the difference.
    /// </remarks>
    Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default);

    /// <summary>Deletes a file on the target.</summary>
    Task DeleteAsync(TargetPath path, CancellationToken ct = default);
}
