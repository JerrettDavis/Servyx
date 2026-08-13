using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    /// <summary>
    /// How long a failed connect is replayed to subsequent callers before another is attempted. Long enough
    /// that a page whose render fans out over an unreachable host pays the connect timeout once rather than
    /// once per call; short enough that a host coming back becomes usable without a restart.
    /// </summary>
    public static readonly TimeSpan DefaultFailureCooldown = TimeSpan.FromSeconds(45);

    private readonly Func<CancellationToken, Task<IExecutionTarget>> _connect;
    private readonly TimeSpan _failureCooldown;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IExecutionTarget? _inner;
    private Exception? _failure;
    private DateTimeOffset _retryNotBefore;

    /// <summary>Creates a target that opens its inner session via <paramref name="connect"/> on first use.</summary>
    /// <param name="failureCooldown">Overrides <see cref="DefaultFailureCooldown"/>.</param>
    /// <param name="timeProvider">Clock used for the cooldown. Defaults to <see cref="TimeProvider.System"/>.</param>
    public LazyConnectingExecutionTarget(
        Func<CancellationToken, Task<IExecutionTarget>> connect,
        TimeSpan? failureCooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        if (failureCooldown is { } cooldown && cooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCooldown), cooldown, "The failure cooldown cannot be negative.");
        }

        _connect = connect;
        _failureCooldown = failureCooldown ?? DefaultFailureCooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    /// <summary>
    /// Opens the inner session once, and — the reason this is not a plain <c>??=</c> — remembers a connect
    /// that threw for <see cref="_failureCooldown"/> so the next caller is refused immediately instead of
    /// paying another full connect timeout. Without that, an unreachable host costs every single call the
    /// transport's connect timeout, which is what turns one bad host into a page that reads as hung.
    /// </summary>
    private async Task<IExecutionTarget> ResolveAsync(CancellationToken ct)
    {
        if (_inner is not null)
        {
            return _inner;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null)
            {
                return _inner;
            }

            if (_failure is not null && _timeProvider.GetUtcNow() < _retryNotBefore)
            {
                ExceptionDispatchInfo.Capture(_failure).Throw();
            }

            try
            {
                _inner = await _connect(ct).ConfigureAwait(false);
                _failure = null;
                return _inner;
            }
            catch (OperationCanceledException)
            {
                // The caller gave up, which says nothing about the host — cooling down on it would punish a
                // reachable host for one abandoned request.
                throw;
            }
            catch (Exception ex)
            {
                _failure = ex;
                _retryNotBefore = _timeProvider.GetUtcNow() + _failureCooldown;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
