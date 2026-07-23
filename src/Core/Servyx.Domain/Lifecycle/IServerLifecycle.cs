namespace Servyx.Domain.Lifecycle;

/// <summary>
/// Controls the lifecycle of a single server. Mutating members are subject to the write guard exactly as
/// file writes are.
/// </summary>
public interface IServerLifecycle
{
    /// <summary>Returns the server's current observed status.</summary>
    Task<ServerStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Starts the server.</summary>
    Task<StartOutcome> StartAsync(CancellationToken ct = default);

    /// <summary>Stops the server, escalating through <paramref name="plan"/>'s stages.</summary>
    Task<StopOutcome> StopAsync(StopPlan plan, CancellationToken ct = default);

    /// <summary>Stops and then restarts the server.</summary>
    Task<StopOutcome> RestartAsync(StopPlan plan, CancellationToken ct = default);

    /// <summary>
    /// Recreates the underlying container. Requires an already-approved <c>ConfigChangePlan</c> id whose
    /// consequences include <c>RecreateRequired</c> — this operation is never callable ad hoc, only as
    /// the applied consequence of a previewed plan.
    /// </summary>
    Task RecreateAsync(string approvedChangePlanId, CancellationToken ct = default);

    /// <summary>Streams status changes as they occur.</summary>
    IAsyncEnumerable<ServerStatus> WatchAsync(CancellationToken ct = default);
}
