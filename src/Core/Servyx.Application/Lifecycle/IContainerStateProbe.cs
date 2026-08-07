namespace Servyx.Application.Lifecycle;

/// <summary>
/// A point-in-time, read-only snapshot of whether a container has exited.
/// </summary>
/// <param name="Exited">Whether the container is no longer running.</param>
/// <param name="State">The transport-reported run state (e.g. <c>"running"</c>, <c>"exited"</c>), if available.</param>
/// <param name="ExitCode">The container's exit code, if it has exited and the transport reports one.</param>
public sealed record ContainerStateSnapshot(bool Exited, string? State = null, int? ExitCode = null);

/// <summary>
/// Reads whether a container has exited, without performing any mutating operation against it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as its own port instead of reusing <c>IContainerLifecycle</c>.</strong>
/// <c>IContainerLifecycle</c> deliberately has no read-only member — see its remarks — because every one
/// of its verbs (Start/Stop/Restart/Kill) changes state, and its own write-guard treats every request as
/// mutating on principle. <see cref="ServerLifecycleService"/>'s stop ladder, though, needs to poll
/// "has the container exited yet?" between escalation stages without that poll itself being gate-able as
/// a write — sending a signal is one action; observing whether it worked is a different, side-effect-free
/// one. This port is that observation, kept structurally separate from the guarded mutating path exactly
/// as <c>ITransport.ProbeAsync</c> is kept separate from <c>IExecutionTarget</c>'s mutating operations.
/// </para>
/// <para>
/// Implementations live outside <c>Servyx.Domain</c>/<c>Servyx.Application</c> (e.g. a Docker inspect or
/// an SSH <c>docker inspect</c> call) and are supplied by the composition root.
/// </para>
/// </remarks>
public interface IContainerStateProbe
{
    /// <summary>Returns whether <paramref name="containerRef"/> has exited.</summary>
    Task<ContainerStateSnapshot> GetStateAsync(string containerRef, CancellationToken ct = default);
}
