namespace Servyx.Domain.Lifecycle;

/// <summary>Observed lifecycle state of a server.</summary>
public enum ServerState
{
    /// <summary>State has not yet been determined.</summary>
    Unknown,

    /// <summary>The workload is not running.</summary>
    Stopped,

    /// <summary>The workload has been asked to start and is not yet ready.</summary>
    Starting,

    /// <summary>The workload is running and ready.</summary>
    Running,

    /// <summary>The workload has been asked to stop and has not yet exited.</summary>
    Stopping,

    /// <summary>The workload exited unexpectedly.</summary>
    Crashed,
}

/// <summary>Current observed status of a server.</summary>
/// <param name="State">The server's current lifecycle state.</param>
/// <param name="StartedAt">When the workload started, if currently running or starting.</param>
/// <param name="Uptime">How long the workload has been running, if currently running.</param>
public sealed record ServerStatus(ServerState State, DateTimeOffset? StartedAt, TimeSpan? Uptime);
