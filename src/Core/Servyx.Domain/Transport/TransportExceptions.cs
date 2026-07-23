namespace Servyx.Domain.Transport;

/// <summary>
/// Thrown when a write is refused because the target's current content no longer matches the caller's
/// expected pre-image hash — the file has drifted since it was last observed.
/// </summary>
public sealed class TargetDriftException : Exception
{
    /// <summary>Creates a <see cref="TargetDriftException"/> with a default message.</summary>
    public TargetDriftException()
        : base("The target has drifted since its content was last observed.")
    {
    }

    /// <summary>Creates a <see cref="TargetDriftException"/> with the given message.</summary>
    public TargetDriftException(string message) : base(message) { }

    /// <summary>Creates a <see cref="TargetDriftException"/> with the given message and inner exception.</summary>
    public TargetDriftException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="TargetDriftException"/> carrying the path and hash mismatch that caused it.</summary>
    public TargetDriftException(string message, TargetPath path, string? expectedHash, string? actualHash) : base(message)
    {
        Path = path;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }

    /// <summary>The path whose content drifted, if known.</summary>
    public TargetPath? Path { get; }

    /// <summary>The pre-image hash the caller expected, if known.</summary>
    public string? ExpectedHash { get; }

    /// <summary>The pre-image hash actually observed, if known.</summary>
    public string? ActualHash { get; }
}

/// <summary>
/// Thrown by the write-guard decorator over <see cref="IExecutionTarget"/> when a mutating call is
/// attempted under a non-permitting <see cref="WriteMode"/>. Individual services are never trusted to
/// check the write-mode flag themselves — the guard is structural.
/// </summary>
public sealed class WritesDisabledException : Exception
{
    /// <summary>Creates a <see cref="WritesDisabledException"/> with a default message.</summary>
    public WritesDisabledException()
        : base("Writes are disabled for this server's current write mode.")
    {
    }

    /// <summary>Creates a <see cref="WritesDisabledException"/> with the given message.</summary>
    public WritesDisabledException(string message) : base(message) { }

    /// <summary>Creates a <see cref="WritesDisabledException"/> with the given message and inner exception.</summary>
    public WritesDisabledException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown by <see cref="SandboxedPathResolver"/> when a requested path would escape the configured
/// sandbox root, whether via <c>..</c> traversal, an absolute path outside the root, or a UNC/device path.
/// </summary>
public sealed class PathEscapesSandboxException : Exception
{
    /// <summary>Creates a <see cref="PathEscapesSandboxException"/> with a default message.</summary>
    public PathEscapesSandboxException()
        : base("The requested path escapes the sandbox root.")
    {
    }

    /// <summary>Creates a <see cref="PathEscapesSandboxException"/> with the given message.</summary>
    public PathEscapesSandboxException(string message) : base(message) { }

    /// <summary>Creates a <see cref="PathEscapesSandboxException"/> with the given message and inner exception.</summary>
    public PathEscapesSandboxException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="PathEscapesSandboxException"/> carrying the offending input.</summary>
    public PathEscapesSandboxException(string message, string attemptedPath) : base(message)
    {
        AttemptedPath = attemptedPath;
    }

    /// <summary>The raw path string that was rejected, if known.</summary>
    public string? AttemptedPath { get; }
}

/// <summary>
/// Thrown when an <see cref="ITransport"/> cannot establish or maintain a connection to a target
/// (distinct from <see cref="TargetHealth.Reachable"/> being false during a side-effect-free probe).
/// </summary>
public sealed class TransportUnavailableException : Exception
{
    /// <summary>Creates a <see cref="TransportUnavailableException"/> with a default message.</summary>
    public TransportUnavailableException()
        : base("The transport is unavailable.")
    {
    }

    /// <summary>Creates a <see cref="TransportUnavailableException"/> with the given message.</summary>
    public TransportUnavailableException(string message) : base(message) { }

    /// <summary>Creates a <see cref="TransportUnavailableException"/> with the given message and inner exception.</summary>
    public TransportUnavailableException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a <see cref="TransportUnavailableException"/> carrying the offending transport id.</summary>
    public TransportUnavailableException(string message, string transportId) : base(message)
    {
        TransportId = transportId;
    }

    /// <summary>The transport identifier that was unavailable, if known.</summary>
    public string? TransportId { get; }
}
