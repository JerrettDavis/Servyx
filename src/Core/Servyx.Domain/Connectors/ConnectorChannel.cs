namespace Servyx.Domain.Connectors;

/// <summary>
/// The individual capabilities a connector may expose. Unlike <see cref="Transport.TransportCapabilities"/>,
/// which is a static property of a transport <em>kind</em>, a set of <see cref="ConnectorChannel"/> flags is
/// always an <em>observation</em> about one specific, credentialed connector instance — see
/// <see cref="IConnector.AvailableChannels"/> and <see cref="ConnectorHealth"/>.
/// </summary>
[Flags]
public enum ConnectorChannel
{
    /// <summary>No channels.</summary>
    None = 0,

    /// <summary>The connector can execute a command to completion (and, typically, stream its output).</summary>
    Exec = 1 << 0,

    /// <summary>The connector can read file contents.</summary>
    FileRead = 1 << 1,

    /// <summary>The connector can write file contents.</summary>
    FileWrite = 1 << 2,

    /// <summary>The connector can list directory contents.</summary>
    DirectoryList = 1 << 3,

    /// <summary>The connector exposes a container-oriented API (e.g. the Docker Engine API).</summary>
    DockerApi = 1 << 4,

    /// <summary>The connector exposes a process-oriented API (e.g. OS process control, signals).</summary>
    ProcessApi = 1 << 5,

    /// <summary>The connector can forward ports between the panel and the target.</summary>
    PortForward = 1 << 6,

    /// <summary>The connector can stream input to a running command's stdin.</summary>
    Stdin = 1 << 7,
}
