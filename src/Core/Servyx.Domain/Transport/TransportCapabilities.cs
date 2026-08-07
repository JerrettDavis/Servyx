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

    /// <summary>
    /// File and directory operations on the sessions this transport hands out are rooted <em>inside</em>
    /// the container named by the <see cref="TargetDescriptor"/>, honouring its <c>rootPath</c> option as a
    /// path in the container's own filesystem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is not implied by <see cref="ContainerApi"/>, and the two must never be conflated.</strong>
    /// <see cref="ContainerApi"/> says the transport can address a container's <em>control plane</em> — start
    /// it, stop it, inspect it. This flag says something strictly narrower and entirely independent: that
    /// <see cref="IExecutionTarget"/>'s <em>file</em> members see the container's filesystem. A transport can
    /// hold the first and not the second — ssh+docker runs <c>docker &lt;verb&gt; &lt;container&gt;</c> over
    /// an SSH exec channel (container-correct) while its file members are SFTP against the SSH host's own
    /// root (host-scoped) — and such a transport must not declare this flag.
    /// </para>
    /// <para>
    /// <strong>Opt-in, because the failure mode is silent.</strong> A host-scoped file channel handed a
    /// container-rooted path does not error; it succeeds against the wrong filesystem, reading nothing and
    /// writing somewhere real. Callers that need container-rooted files therefore treat an <em>absent</em>
    /// flag as a refusal — see <see cref="ContainerScopedFilesNotSupportedException"/> — so a transport
    /// cannot acquire the misrouting by saying nothing.
    /// </para>
    /// </remarks>
    ContainerScopedFiles = 1 << 9,
}
