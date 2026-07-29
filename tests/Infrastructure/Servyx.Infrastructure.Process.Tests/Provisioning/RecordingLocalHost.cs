using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Tests.Provisioning;

/// <summary>
/// A local machine as the provisioner sees it: a real <see cref="LocalExecutionTarget"/> for every file
/// operation, with command execution intercepted and recorded instead of actually starting a program.
/// </summary>
/// <remarks>
/// <para>
/// This is the local counterpart of <c>SshHostDouble</c>, but it substitutes strictly less. The SSH double has
/// to model an entire remote filesystem in memory because there is no local equivalent of the host it talks to.
/// Here the filesystem <em>is</em> available — every test already owns a temp directory — so marker files are
/// genuinely written, read back, listed, and deleted through the production
/// <see cref="LocalExecutionTarget"/>. Only <see cref="IExecutionTarget.ExecuteAsync"/> is faked, because the
/// only program an install would start is <c>steamcmd</c>, which no test may require to be installed.
/// </para>
/// <para>
/// Every descriptor the provisioner connects with is recorded, and file writes and command executions are
/// recorded in one interleaved list, so orderings can be asserted directly.
/// </para>
/// </remarks>
internal sealed class RecordingLocalHost : ITransport
{
    /// <inheritdoc />
    public string TransportId => LocalProcessTransport.Id;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => new LocalProcessTransport().Capabilities;

    /// <summary>How each command answers. Defaults to success; replace it to exercise a failing step.</summary>
    internal Func<CommandSpec, CommandResult> ExecHandler { get; set; } =
        _ => new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);

    /// <summary>Every command the provisioner executed, in order, as argv arrays.</summary>
    internal List<CommandSpec> Commands { get; } = [];

    /// <summary>Exec, write, and delete operations interleaved, in order.</summary>
    internal List<string> Order { get; } = [];

    /// <summary>Every descriptor the provisioner connected with.</summary>
    internal List<TargetDescriptor> Connected { get; } = [];

    /// <summary>
    /// When set, every session this host hands back is wrapped in the production
    /// <see cref="WriteGuardedExecutionTarget"/> in that mode, exactly as a composition root registering the
    /// transport behind <c>WriteGuardedTransport</c> would produce.
    /// </summary>
    /// <remarks>
    /// Null by default, so every test written before this property existed still gets an unguarded session and
    /// behaves identically. It exists because the guard gates file writes and deliberately does <em>not</em>
    /// gate command execution, so "a read-only server refuses the whole update before running anything" is a
    /// claim only a real guard can pin.
    /// </remarks>
    internal WriteMode? GuardMode { get; set; }

    /// <inheritdoc />
    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
        new LocalProcessTransport().ProbeAsync(target, ct);

    /// <inheritdoc />
    public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        Connected.Add(target);
        var inner = new LocalExecutionTarget(LocalProcessTransport.ResolveRootPath(target));
        IExecutionTarget session = new RecordingExecutionTarget(this, inner);

        if (GuardMode is { } mode)
        {
            session = new WriteGuardedExecutionTarget(session, mode, target.Endpoint);
        }

        return Task.FromResult(session);
    }

    /// <summary>Forgets everything recorded so far, so a test can assert on a single phase in isolation.</summary>
    internal void ClearRecordings()
    {
        Commands.Clear();
        Order.Clear();
        Connected.Clear();
    }

    private sealed class RecordingExecutionTarget(RecordingLocalHost owner, LocalExecutionTarget inner) : IExecutionTarget
    {
        public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
        {
            owner.Commands.Add(spec);
            owner.Order.Add($"exec:{spec.Executable}");
            return Task.FromResult(owner.ExecHandler(spec));
        }

        public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
            throw new NotSupportedException("The provisioner never streams; nothing should reach this method.");

        public Task<bool> ExistsAsync(TargetPath path, CancellationToken ct = default) => inner.ExistsAsync(path, ct);

        public Task<FileStat> StatAsync(TargetPath path, CancellationToken ct = default) => inner.StatAsync(path, ct);

        public Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(TargetPath path, CancellationToken ct = default) =>
            inner.ListDirectoryAsync(path, ct);

        public Task<Stream> OpenReadAsync(TargetPath path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);

        public async Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default)
        {
            owner.Order.Add($"write:{Absolute(path)}");
            return await inner.WriteFileAsync(path, content, options, ct).ConfigureAwait(false);
        }

        public async Task DeleteAsync(TargetPath path, CancellationToken ct = default)
        {
            owner.Order.Add($"delete:{Absolute(path)}");
            await inner.DeleteAsync(path, ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private string Absolute(TargetPath path) => path.Value.Length == 0
            ? inner.RootPath
            : Path.Combine(inner.RootPath, path.Value.Replace('/', Path.DirectorySeparatorChar));
    }
}
