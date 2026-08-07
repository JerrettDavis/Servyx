using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// A thin decorator over the ssh+docker session's inner <see cref="IExecutionTarget"/> that adds a
/// <see cref="IContainerLifecycle"/> channel, translating each verb into the matching <see cref="DockerCli"/>
/// factory and running it through the inner target's own <see cref="IExecutionTarget.ExecuteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every <see cref="IExecutionTarget"/> member is forwarded verbatim.</strong> None of them are
/// intercepted, none rewrite intent, none inspect the spec — that is the entire reason
/// <see cref="SshDockerTransport.ConnectAsync"/> was previously able to return the inner SSH session
/// unchanged, and this type preserves that property even though it now sits in front of it. The only new
/// surface is <see cref="InvokeAsync"/>.
/// </para>
/// <para>
/// <strong><see cref="InvokeAsync"/> never bypasses the write guard.</strong> This type is not itself a
/// guard — it has no concept of <see cref="WriteMode"/> and does not attempt to enforce one. It is
/// constructed only inside <see cref="ITransport.ConnectAsync"/>, which is only ever reached through
/// <see cref="WriteGuardedTransport"/> in every Servyx composition root
/// (<c>TransportWriteGuardArchitectureTests</c> asserts that structurally). The
/// <see cref="CommandSpec"/> each verb maps to is always <see cref="CommandIntent.Mutating"/> — none of the
/// <see cref="DockerCli"/> factories used here declare otherwise — so even a caller that somehow obtained
/// this session directly, un-guarded, and called <see cref="IExecutionTarget.ExecuteAsync"/> straight
/// through would still be refused if the inner target itself happens to be write-guarded. Two independent
/// refusal points, not one policy relied on twice.
/// </para>
/// </remarks>
public sealed class SshDockerLifecycleSession : IExecutionTarget, IContainerLifecycle
{
    private const int StderrTruncateLength = 200;
    private const int DefaultStopTimeoutSeconds = 10;

    private readonly IExecutionTarget _inner;

    /// <summary>Creates a lifecycle-capable session decorating <paramref name="inner"/>.</summary>
    /// <param name="inner">The session every <see cref="IExecutionTarget"/> call and lifecycle verb delegates to.</param>
    public SshDockerLifecycleSession(IExecutionTarget inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(CommandSpec spec, CancellationToken ct = default) =>
        _inner.ExecuteAsync(spec, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<OutputChunk> ExecuteStreamingAsync(CommandSpec spec, CancellationToken ct = default) =>
        _inner.ExecuteStreamingAsync(spec, ct);

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
    public Task<FileWriteReceipt> WriteFileAsync(TargetPath path, Stream content, FileWriteOptions options, CancellationToken ct = default) =>
        _inner.WriteFileAsync(path, content, options, ct);

    /// <inheritdoc />
    public Task DeleteAsync(TargetPath path, CancellationToken ct = default) =>
        _inner.DeleteAsync(path, ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <inheritdoc />
    /// <remarks>
    /// Maps <paramref name="request"/> to the matching <see cref="DockerCli"/> factory — <see cref="DockerCli.Start"/>,
    /// <see cref="DockerCli.Stop"/> (with <see cref="ContainerLifecycleRequest.GracePeriod"/> as docker's
    /// <c>--time</c>), <see cref="DockerCli.Restart"/>, or <see cref="DockerCli.Kill"/> — then executes it
    /// through <see cref="ExecuteAsync"/> exactly like any other command. Exit code 0 is reported as
    /// success; any other exit code is a failure whose <see cref="ContainerLifecycleResult.Detail"/> carries
    /// a truncated (~200 character) excerpt of stderr, so a lifecycle failure can never leak unbounded
    /// remote output into an operator-facing result.
    /// </remarks>
    public async Task<ContainerLifecycleResult> InvokeAsync(ContainerLifecycleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var spec = BuildSpec(request);
        var result = await _inner.ExecuteAsync(spec, ct).ConfigureAwait(false);

        return result.ExitCode == 0
            ? new ContainerLifecycleResult(true, $"docker {spec.Arguments[0]} succeeded.", result.ExitCode)
            : new ContainerLifecycleResult(false, Truncate(result.StandardError), result.ExitCode);
    }

    private static CommandSpec BuildSpec(ContainerLifecycleRequest request) => request.Verb switch
    {
        ContainerLifecycleVerb.Start => DockerCli.Start(request.ContainerRef),
        ContainerLifecycleVerb.Stop => DockerCli.Stop(request.ContainerRef, GracePeriodSeconds(request.GracePeriod)),
        ContainerLifecycleVerb.Restart => DockerCli.Restart(request.ContainerRef),
        ContainerLifecycleVerb.Kill => DockerCli.Kill(request.ContainerRef, request.Signal),
        _ => throw new ArgumentOutOfRangeException(
            nameof(request), request.Verb, $"Unrecognized {nameof(ContainerLifecycleVerb)} value."),
    };

    private static int GracePeriodSeconds(TimeSpan? gracePeriod) =>
        gracePeriod is null
            ? DefaultStopTimeoutSeconds
            : (int)Math.Max(0, Math.Round(gracePeriod.Value.TotalSeconds));

    /// <summary>Truncates text to a bounded length so a failed lifecycle call can never leak unbounded remote output.</summary>
    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var trimmed = text.Trim();
        return trimmed.Length <= StderrTruncateLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, StderrTruncateLength), "...");
    }
}
