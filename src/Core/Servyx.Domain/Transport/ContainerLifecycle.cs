namespace Servyx.Domain.Transport;

/// <summary>
/// A lifecycle operation to perform against a container. Deliberately has no read-only member — see
/// <see cref="IContainerLifecycle"/> for why that omission is the point.
/// </summary>
public enum ContainerLifecycleVerb
{
    /// <summary>Starts a stopped container.</summary>
    Start,

    /// <summary>Stops a running container, giving it a chance to shut down cleanly.</summary>
    Stop,

    /// <summary>Stops and starts a container.</summary>
    Restart,

    /// <summary>Terminates a container immediately, without a graceful shutdown.</summary>
    Kill,
}

/// <summary>
/// A request to change a container's run state.
/// </summary>
/// <remarks>
/// <see cref="AsGuardedSpec"/> is what lets <see cref="WriteGuardedExecutionTarget"/> gate this request
/// through the exact same policy it applies to <see cref="CommandSpec"/>-shaped calls, without this type
/// needing to know anything about <see cref="WriteMode"/> itself.
/// </remarks>
/// <param name="Verb">The lifecycle operation to perform.</param>
/// <param name="ContainerRef">The container's name or id. Must not be null, empty, or whitespace.</param>
/// <param name="GracePeriod">
/// How long to wait for a graceful shutdown before the underlying transport may escalate, where the verb
/// and transport support one (e.g. <see cref="ContainerLifecycleVerb.Stop"/>). Ignored by transports or
/// verbs that have no such concept.
/// </param>
/// <param name="Signal">
/// An optional OS signal to send, where the verb and transport support one (e.g.
/// <see cref="ContainerLifecycleVerb.Kill"/>). Ignored by transports or verbs that have no such concept.
/// </param>
public sealed record ContainerLifecycleRequest(
    ContainerLifecycleVerb Verb,
    string ContainerRef,
    TimeSpan? GracePeriod = null,
    string? Signal = null)
{
    private readonly string _containerRef = Validated(ContainerRef);

    /// <summary>The container's name or id.</summary>
    /// <exception cref="ArgumentException">The value is null, empty, or whitespace.</exception>
    public string ContainerRef
    {
        get => _containerRef;
        init => _containerRef = Validated(value);
    }

    private static string Validated(string containerRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRef);
        return containerRef;
    }

    /// <summary>
    /// The <see cref="CommandSpec"/> this request is gated as. <see cref="CommandSpec.Intent"/> is never
    /// set, so it resolves to <see cref="CommandIntent.Mutating"/> by the same default every other
    /// undeclared command gets — there is no argument on this record a caller can pass to claim the
    /// operation is read-only. <see cref="ContainerLifecycleVerb"/> has no read-only member for the same
    /// reason.
    /// </summary>
    public CommandSpec AsGuardedSpec() =>
        new("docker", [Verb.ToString().ToLowerInvariant(), ContainerRef]);
}

/// <summary>The outcome of a completed <see cref="IContainerLifecycle.InvokeAsync"/> call.</summary>
/// <param name="Success">Whether the requested transition completed successfully.</param>
/// <param name="Detail">
/// A human-readable description of the outcome (e.g. the resulting container state, or the reason the
/// operation did not succeed), for operator-facing logs and diagnostics.
/// </param>
/// <param name="ExitCode">
/// The container's exit code after the operation, if the underlying transport reports one and the verb
/// produces a terminated state (e.g. <see cref="ContainerLifecycleVerb.Stop"/>,
/// <see cref="ContainerLifecycleVerb.Kill"/>). Null when not applicable or not reported.
/// </param>
/// <param name="State">
/// The container's resulting run state as reported by the underlying transport (e.g. <c>"running"</c>,
/// <c>"exited"</c>), if available. Null when the transport does not report one.
/// </param>
public sealed record ContainerLifecycleResult(bool Success, string Detail, int? ExitCode = null, string? State = null);

/// <summary>
/// Invokes lifecycle operations (start/stop/restart/kill) against a container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as a separate shape from <see cref="IExecutionTarget.ExecuteAsync"/>.</b> Container
/// lifecycle on the local Docker path goes through Docker.DotNet's container APIs — <c>StartContainer</c>,
/// <c>StopContainer</c>, and so on — which are not commands and carry no <see cref="CommandSpec"/> to
/// classify. <c>docker start</c> in particular cannot be represented as an exec at all: you cannot exec
/// into a container that is not running. If that path bypassed <see cref="WriteGuardedExecutionTarget"/>,
/// the read-only guarantee would have a hole exactly where the destructive operations live.
/// </para>
/// <para>
/// <b>This is a second shape gated by the same policy, not a second policy.</b> An implementation of this
/// interface on <see cref="WriteGuardedExecutionTarget"/> converts every request to a
/// <see cref="CommandSpec"/> via <see cref="ContainerLifecycleRequest.AsGuardedSpec"/> and reuses the exact
/// same private guard the command path uses — the refusal message, the synchronous-before-I/O timing, and
/// the treatment of <see cref="WriteMode.PreviewOnly"/> as equivalent to <see cref="WriteMode.ReadOnly"/>
/// are all identical, because it is literally the same check.
/// </para>
/// <para>
/// <b>The absence of a read-only verb is the point.</b> Unlike <see cref="CommandSpec.Intent"/>, which lets
/// a caller declare a command read-only when it genuinely is, <see cref="ContainerLifecycleVerb"/> has no
/// member that means "this does not change state" — because none of Start, Stop, Restart, or Kill ever
/// doesn't. There is deliberately no argument a caller can pass to this API to opt out of the guard.
/// </para>
/// </remarks>
public interface IContainerLifecycle
{
    /// <summary>
    /// Performs the requested lifecycle transition against <see cref="ContainerLifecycleRequest.ContainerRef"/>.
    /// </summary>
    /// <param name="request">The lifecycle operation to perform.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<ContainerLifecycleResult> InvokeAsync(ContainerLifecycleRequest request, CancellationToken ct = default);
}
