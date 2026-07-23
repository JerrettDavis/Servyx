using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>
/// Routes exec operations to one <see cref="IExecutionTarget"/> and file/directory operations to another,
/// which may or may not be the same underlying connection. This is the concrete realization of
/// <c>docs/connectors.md</c>'s "SSH and SFTP are independent" composition: an SFTP-only server passes
/// <c>execTarget: null</c>, an exec-only server passes <c>fileTarget: null</c> (or a
/// <see cref="ShellFileChannel"/> synthesizing files over the same exec connection), and the common case
/// passes both.
/// </summary>
public sealed class CompositeExecutionTarget : ICompositeExecutionTarget
{
    /// <inheritdoc />
    public IExecutionTarget? ExecTarget { get; }

    /// <inheritdoc />
    public IExecutionTarget? FileTarget { get; }

    /// <summary>
    /// The channels this composite reports as available, computed purely from which of
    /// <see cref="ExecTarget"/>/<see cref="FileTarget"/> are present — an exec-only composite
    /// (<c>fileTarget: null</c>) never reports <see cref="ConnectorChannel.FileWrite"/>, and an SFTP-only
    /// composite (<c>execTarget: null</c>) never reports <see cref="ConnectorChannel.Exec"/>.
    /// </summary>
    public ConnectorChannel AvailableChannels { get; }

    /// <summary>Creates a composite target. At least one of <paramref name="execTarget"/> or <paramref name="fileTarget"/> must be non-null.</summary>
    /// <exception cref="ArgumentException">Both <paramref name="execTarget"/> and <paramref name="fileTarget"/> are <see langword="null"/>.</exception>
    public CompositeExecutionTarget(IExecutionTarget? execTarget, IExecutionTarget? fileTarget)
    {
        if (execTarget is null && fileTarget is null)
        {
            throw new ArgumentException("At least one of execTarget or fileTarget must be provided.");
        }

        ExecTarget = execTarget;
        FileTarget = fileTarget;

        var channels = ConnectorChannel.None;
        if (execTarget is not null)
        {
            channels |= ConnectorChannel.Exec | ConnectorChannel.Stdin;
        }

        if (fileTarget is not null)
        {
            channels |= ConnectorChannel.FileRead | ConnectorChannel.FileWrite | ConnectorChannel.DirectoryList;
        }

        AvailableChannels = channels;
    }

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        RequireExecTarget().ExecuteAsync(spec, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        RequireExecTarget().ExecuteStreamingAsync(spec, ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
        RequireFileTarget().ExistsAsync(path, ct);

    /// <inheritdoc />
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
        RequireFileTarget().StatAsync(path, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        RequireFileTarget().ListDirectoryAsync(path, ct);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
        RequireFileTarget().OpenReadAsync(path, ct);

    /// <inheritdoc />
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        RequireFileTarget().WriteFileAsync(path, content, options, ct);

    /// <inheritdoc />
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        RequireFileTarget().DeleteAsync(path, ct);

    private IExecutionTarget RequireExecTarget() =>
        ExecTarget ?? throw new NotSupportedException("This connector has no exec channel available.");

    private IExecutionTarget RequireFileTarget() =>
        FileTarget ?? throw new NotSupportedException("This connector has no file channel available.");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (ExecTarget is not null)
        {
            await ExecTarget.DisposeAsync().ConfigureAwait(false);
        }

        if (FileTarget is not null && !ReferenceEquals(FileTarget, ExecTarget))
        {
            await FileTarget.DisposeAsync().ConfigureAwait(false);
        }
    }
}
