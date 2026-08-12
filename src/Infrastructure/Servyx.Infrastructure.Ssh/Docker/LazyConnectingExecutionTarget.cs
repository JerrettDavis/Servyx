using System.Runtime.CompilerServices;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// An <see cref="IExecutionTarget"/> that connects on first real use instead of at construction, so a
/// dependency-injection singleton factory — or a registry that hands out one target per configured/registered
/// host — can produce a working target without blocking the calling thread on <see cref="ITransport.ConnectAsync"/>.
/// </summary>
/// <remarks>
/// Originally private to <see cref="SshDockerServiceCollectionExtensions"/>; promoted to its own public type so
/// <see cref="HostConnectionRegistry"/> can build one lazy, memoized session per host exactly the same way
/// <c>AddServyxSshDocker</c> already does for its single configured host — see that method's remarks for the
/// full rationale (blocking-vs-async-factory, concurrent first-caller memoization, disposal).
/// </remarks>
public sealed class LazyConnectingExecutionTarget : IExecutionTarget
{
    private readonly Func<CancellationToken, Task<IExecutionTarget>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IExecutionTarget? _inner;

    /// <summary>Creates a target that opens its inner session via <paramref name="connect"/> on first use.</summary>
    public LazyConnectingExecutionTarget(Func<CancellationToken, Task<IExecutionTarget>> connect)
    {
        ArgumentNullException.ThrowIfNull(connect);
        _connect = connect;
    }

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).ExecuteAsync(spec, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(
        CommandSpec spec, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var target = await ResolveAsync(ct).ConfigureAwait(false);
        await foreach (var chunk in target.ExecuteStreamingAsync(spec, ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).ExistsAsync(path, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).StatAsync(path, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).ListDirectoryAsync(path, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).OpenReadAsync(path, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<FileWriteReceipt> WriteFileAsync(
        TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).WriteFileAsync(path, content, options, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        await (await ResolveAsync(ct).ConfigureAwait(false)).DeleteAsync(path, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IExecutionTarget> ResolveAsync(CancellationToken ct)
    {
        if (_inner is not null)
        {
            return _inner;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _inner ??= await _connect(ct).ConfigureAwait(false);
            return _inner;
        }
        finally
        {
            _gate.Release();
        }
    }
}
