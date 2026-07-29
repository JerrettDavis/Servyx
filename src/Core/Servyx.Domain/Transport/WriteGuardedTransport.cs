namespace Servyx.Domain.Transport;

/// <summary>
/// A decorator over <see cref="ITransport"/> that guarantees every session it hands out is wrapped in a
/// <see cref="WriteGuardedExecutionTarget"/> carrying the target's own <see cref="WriteMode"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the write guard structural rather than conventional. A caller cannot obtain an
/// unguarded <see cref="IExecutionTarget"/> from a transport registered through a Servyx DI extension,
/// because the only <see cref="ITransport"/> in the container is this one and its
/// <see cref="ConnectAsync"/> has no code path that returns the inner session directly. An architecture
/// test asserts the registration side of that claim.
/// </para>
/// <para>
/// <see cref="Capabilities"/> delegates unchanged. It deliberately does not add
/// <see cref="TransportCapabilities.FileWrite"/> when a grant exists, because <see cref="ITransport"/>
/// declares capabilities per transport <em>kind</em> while write mode is held per <em>server</em>; a
/// property that varied with which target you were about to connect to would be lying to every caller that
/// reads it without one in hand. Under-advertising is the safe direction: a caller that consults
/// capabilities before offering a write simply does not offer it.
/// </para>
/// </remarks>
public sealed class WriteGuardedTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly IWriteModeResolver _writeModes;

    /// <summary>Creates a guard over <paramref name="inner"/>.</summary>
    /// <param name="inner">The transport that actually connects.</param>
    /// <param name="writeModes">Resolves each target's write posture; null means every target is read-only.</param>
    public WriteGuardedTransport(ITransport inner, IWriteModeResolver? writeModes = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _writeModes = writeModes ?? ReadOnlyWriteModeResolver.Instance;
    }

    /// <summary>The transport this guard wraps. Exposed for diagnostics and architecture tests only.</summary>
    public ITransport Inner => _inner;

    /// <inheritdoc />
    public string TransportId => _inner.TransportId;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
        _inner.ProbeAsync(target, ct);

    /// <inheritdoc />
    public async Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var mode = _writeModes.Resolve(target);
        var session = await _inner.ConnectAsync(target, ct).ConfigureAwait(false);
        return new WriteGuardedExecutionTarget(session, mode, DescribeTarget(target));
    }

    /// <summary>
    /// A short, human-readable name for a target, used only in refusal messages. Prefers whichever
    /// container option the descriptor carries, falling back to the endpoint.
    /// </summary>
    private static string DescribeTarget(TargetDescriptor target)
    {
        foreach (var key in (string[])["containerName", "containerId", "container"])
        {
            if (target.Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return target.Endpoint;
    }
}
