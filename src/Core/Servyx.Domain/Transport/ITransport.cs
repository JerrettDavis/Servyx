namespace Servyx.Domain.Transport;

/// <summary>
/// A transport is a way of reaching a host or workload. It does not itself represent a connection —
/// call <see cref="ConnectAsync"/> to obtain an <see cref="IExecutionTarget"/> session. Implementations
/// include local process execution, local Docker, SSH, and Docker-CLI-over-SSH; they are four
/// implementations of one contract, deliberately below the container-vs-process distinction.
/// </summary>
public interface ITransport
{
    /// <summary>Stable identifier for this transport implementation.</summary>
    string TransportId { get; }

    /// <summary>Capabilities this transport implementation supports.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Checks whether the given target is reachable and reports its health. MUST be side-effect free:
    /// no state on the target may change as a result of calling this method.
    /// </summary>
    Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default);

    /// <summary>
    /// Establishes a session against the given target. The returned session is pooled and
    /// reference-counted by callers; this method itself does not pool.
    /// </summary>
    Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default);
}
