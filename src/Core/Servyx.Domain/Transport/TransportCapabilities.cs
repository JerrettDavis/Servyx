namespace Servyx.Domain.Transport;

/// <summary>
/// Capabilities a transport may support. A transport advertises the subset it actually implements;
/// callers must check <see cref="ITransport.Capabilities"/> before invoking an operation that may not
/// be present.
/// </summary>
[Flags]
public enum TransportCapabilities
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>The transport can execute a command to completion.</summary>
    ExecuteCommand = 1 << 0,

    /// <summary>The transport can stream command output as it is produced.</summary>
    StreamOutput = 1 << 1,

    /// <summary>The transport can stream input to a running command's stdin.</summary>
    StreamStdin = 1 << 2,

    /// <summary>The transport can read files on the target.</summary>
    FileRead = 1 << 3,

    /// <summary>The transport can write files on the target.</summary>
    FileWrite = 1 << 4,

    /// <summary>The transport can list directory contents on the target.</summary>
    DirectoryList = 1 << 5,

    /// <summary>The transport exposes a container-oriented API (e.g. Docker Engine API).</summary>
    ContainerApi = 1 << 6,

    /// <summary>The transport exposes a process-oriented API (e.g. OS process control).</summary>
    ProcessApi = 1 << 7,

    /// <summary>The transport can forward ports between the panel and the target.</summary>
    PortForward = 1 << 8,
}
