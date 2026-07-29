namespace Servyx.Domain.Transport;

/// <summary>
/// A decorator over <see cref="IExecutionTarget"/> whose mutating members throw
/// <see cref="WritesDisabledException"/> before any I/O occurs unless the owning server's
/// <see cref="WriteMode"/> is <see cref="WriteMode.Enabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// Individual services are never trusted to check the write mode themselves — the guard is structural,
/// not a convention. <see cref="WriteGuardedTransport"/> is what makes it structural in practice: every
/// <see cref="IExecutionTarget"/> handed out by a transport registered through a Servyx DI extension
/// comes out of this decorator, and an architecture test asserts exactly that.
/// </para>
/// <para>
/// <b>Which members are mutating.</b> <see cref="WriteFileAsync"/> and <see cref="DeleteAsync"/> always are.
/// <see cref="ExecuteAsync"/>/<see cref="ExecuteStreamingAsync"/> are gated too, but by the spec's declared
/// <see cref="CommandSpec.Intent"/> rather than by verb — this type never parses argv and never guesses.
/// <c>docker exec</c> is the same API call whether it runs <c>Info</c> or <c>Shutdown</c>, so classification
/// has to come from whoever built the argv, exactly as <c>WriteGuardedRconSession</c> takes it from the
/// definition's <c>readOnly</c> flag. A command declared <see cref="CommandIntent.ReadOnly"/> passes in every
/// mode, which is what keeps M2's read-only control and readiness probes working on a
/// <see cref="WriteMode.ReadOnly"/> server; anything else — including anything that simply did not say — is
/// refused unless the mode is <see cref="WriteMode.Enabled"/>.
/// </para>
/// <para>
/// <b>Why intent lives on the spec rather than in a second method.</b> A <c>ExecuteMutatingAsync</c>
/// alongside an ungated <see cref="ExecuteAsync"/> would leave the ungated one in the interface, so the gap
/// would survive for anyone who called it — the guard has to be the only door. Putting the declaration on
/// <see cref="CommandSpec"/> with <see cref="CommandIntent.Mutating"/> as the default means an adapter that
/// forgets to think about intent is refused on a read-only server instead of silently permitted, so the
/// failure mode of forgetting is a refusal rather than a mutation.
/// </para>
/// <para>
/// <b><see cref="WriteMode.PreviewOnly"/> at this seam.</b> It refuses exactly what
/// <see cref="WriteMode.ReadOnly"/> refuses, and permits exactly what it permits. That is not an oversight:
/// previewing a change means reading the current bytes, hashing them, and rendering a diff — all reads. The
/// distinction between the two modes lives one layer up, where the plan engine may compute and render a
/// plan under <see cref="WriteMode.PreviewOnly"/> but must refuse to under <see cref="WriteMode.ReadOnly"/>.
/// A preview that needed to write in order to preview would not be a preview, so at the transport seam the
/// two modes are identical by construction.
/// </para>
/// <para>
/// Disposal delegates to the inner target: the guard owns no resources of its own, and swallowing the
/// inner disposal would leak whatever the transport opened.
/// </para>
/// </remarks>
public sealed class WriteGuardedExecutionTarget : IExecutionTarget
{
    private readonly IExecutionTarget _inner;

    /// <summary>Creates a guard over <paramref name="inner"/> for a server in <paramref name="mode"/>.</summary>
    /// <param name="inner">The target every permitted call delegates to.</param>
    /// <param name="mode">The owning server's write posture.</param>
    /// <param name="targetDescription">
    /// A human-readable identifier for the guarded target (a container name, an endpoint) used only in
    /// refusal messages, so an operator reading a <see cref="WritesDisabledException"/> can tell which
    /// server refused. Optional; never used for any decision.
    /// </param>
    public WriteGuardedExecutionTarget(IExecutionTarget inner, WriteMode mode, string? targetDescription = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        Mode = mode;
        TargetDescription = targetDescription;
    }

    /// <summary>The write posture this guard enforces.</summary>
    public WriteMode Mode { get; }

    /// <summary>A human-readable identifier for the guarded target, used only in refusal messages.</summary>
    public string? TargetDescription { get; }

    /// <summary>Whether <see cref="Mode"/> permits mutating calls to reach the inner target.</summary>
    public bool WritesPermitted => Mode == WriteMode.Enabled;

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// <paramref name="spec"/> is not declared <see cref="CommandIntent.ReadOnly"/> and <see cref="Mode"/> is
    /// not <see cref="WriteMode.Enabled"/>. Thrown synchronously, before the inner target is touched at all.
    /// </exception>
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        ThrowIfMutatingCommandIsDisabled(spec);
        return _inner.ExecuteAsync(spec, ct);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// <paramref name="spec"/> is not declared <see cref="CommandIntent.ReadOnly"/> and <see cref="Mode"/> is
    /// not <see cref="WriteMode.Enabled"/>. Thrown synchronously by the call itself rather than on first
    /// enumeration, so a caller that builds the sequence and never iterates it still cannot start the process.
    /// </exception>
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default)
    {
        ThrowIfMutatingCommandIsDisabled(spec);
        return _inner.ExecuteStreamingAsync(spec, ct);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.ExistsAsync(path, ct);

    /// <inheritdoc />
    public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.StatAsync(path, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.ListDirectoryAsync(path, ct);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.OpenReadAsync(path, ct);

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// <see cref="Mode"/> is not <see cref="WriteMode.Enabled"/>. Thrown synchronously, before
    /// <paramref name="content"/> is read and before the inner target is touched at all.
    /// </exception>
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
    {
        ThrowIfWritesDisabled("write file", path);
        return _inner.WriteFileAsync(path, content, options, ct);
    }

    /// <inheritdoc />
    /// <exception cref="WritesDisabledException">
    /// <see cref="Mode"/> is not <see cref="WriteMode.Enabled"/>. Thrown synchronously, before the inner
    /// target is touched at all.
    /// </exception>
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default)
    {
        ThrowIfWritesDisabled("delete", path);
        return _inner.DeleteAsync(path, ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void ThrowIfMutatingCommandIsDisabled(CommandSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Intent == CommandIntent.ReadOnly || WritesPermitted)
        {
            return;
        }

        var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
        throw new WritesDisabledException(
            $"Refusing to run command '{spec.Executable}'{where}: it is declared {nameof(CommandIntent)}." +
            $"{nameof(CommandIntent.Mutating)} — the default for a command that declares nothing — and the " +
            $"server's write mode is {Mode}. Mutating commands require {nameof(WriteMode)}." +
            $"{nameof(WriteMode.Enabled)}, set per server and never globally. A command the caller declares " +
            $"{nameof(CommandIntent)}.{nameof(CommandIntent.ReadOnly)} runs in every mode.");
    }

    private void ThrowIfWritesDisabled(string operation, TargetPath path)
    {
        if (WritesPermitted)
        {
            return;
        }

        var where = TargetDescription is null ? string.Empty : $" on '{TargetDescription}'";
        throw new WritesDisabledException(
            $"Refusing to {operation} '{path.Value}'{where}: the server's write mode is {Mode}. " +
            $"Writes require {nameof(WriteMode)}.{nameof(WriteMode.Enabled)}, set per server and never globally.");
    }
}
