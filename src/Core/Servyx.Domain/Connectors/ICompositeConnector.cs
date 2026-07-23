using Servyx.Domain.Transport;

namespace Servyx.Domain.Connectors;

/// <summary>
/// A connector that routes exec operations to one underlying connector and file operations to another,
/// which may or may not be the same physical connection. See <c>docs/connectors.md</c>, "SSH and SFTP are
/// independent", for the four real-world deployment shapes this composition makes representable without
/// special-casing any of them.
/// </summary>
public interface ICompositeConnector : IConnector
{
    /// <summary>The connector that exec operations (<see cref="ConnectorChannel.Exec"/>, etc.) are routed to.</summary>
    IConnector ExecTarget { get; }

    /// <summary>
    /// The connector that file operations (<see cref="ConnectorChannel.FileRead"/>,
    /// <see cref="ConnectorChannel.FileWrite"/>, <see cref="ConnectorChannel.DirectoryList"/>) are routed to.
    /// </summary>
    IConnector FileTarget { get; }
}

/// <summary>
/// An <see cref="IExecutionTarget"/> that routes exec operations to one underlying execution target and
/// file operations to another. The <see cref="IExecutionTarget"/> methods themselves
/// (<see cref="IExecutionTarget.ExecuteAsync"/> versus <see cref="IExecutionTarget.StatAsync"/>, etc.) are
/// unchanged; this interface only names the two targets a composite implementation routes between, for
/// diagnostics and testing.
/// </summary>
public interface ICompositeExecutionTarget : IExecutionTarget
{
    /// <summary>The target that <see cref="IExecutionTarget.ExecuteAsync"/> and <see cref="IExecutionTarget.ExecuteStreamingAsync"/> are routed to, or <see langword="null"/> if exec is not available.</summary>
    IExecutionTarget? ExecTarget { get; }

    /// <summary>The target that file and directory operations are routed to, or <see langword="null"/> if file access is not available.</summary>
    IExecutionTarget? FileTarget { get; }
}
