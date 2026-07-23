using Servyx.Domain.Transport;

namespace Servyx.Domain.Connectors;

/// <summary>
/// A specific, user-configured, persisted connector instance — endpoint, credentials, host-key trust, and
/// pooled sessions. See <c>docs/connectors.md</c>, "<c>ITransport</c> vs <c>IConnector</c>", for how this
/// differs from the stateless, singleton <see cref="ITransport"/> it is typically built on.
/// </summary>
public interface IConnector
{
    /// <summary>The persisted configuration this connector instance was built from.</summary>
    ConnectorDescriptor Descriptor { get; }

    /// <summary>
    /// The channels actually observed working as of the last <see cref="CheckAsync"/>. Always a subset of
    /// <see cref="ConnectorDescriptor.DeclaredChannels"/> — a descriptor can declare
    /// <c>FileRead | FileWrite</c> because that's what the connector kind normally supports, while this
    /// specific instance's observed channels come back missing <c>FileWrite</c> because this particular
    /// host has the sftp subsystem disabled.
    /// </summary>
    ConnectorChannel AvailableChannels { get; }

    /// <summary>
    /// Checks whether this connector is reachable and reports its health, including any degraded channels.
    /// Implementations should keep this side-effect free wherever possible (passive probing) — see
    /// <c>docs/control-plane.md</c>, "Probing", for the passive/active distinction this mirrors.
    /// </summary>
    Task<ConnectorHealth> CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a session against this connector. Implementations MUST verify the remote host key (where
    /// applicable) before this method returns a session capable of executing anything, and MUST refuse to
    /// open a session at all on a non-<see cref="HostKeyVerdict.Trusted"/> verdict.
    /// </summary>
    Task<IConnectorSession> OpenAsync(CancellationToken ct = default);

    /// <summary>Resolves a stable identity string for whoever/whatever this connector authenticates as (e.g. a remote username).</summary>
    Task<string> ResolveIdentityAsync(CancellationToken ct = default);
}

/// <summary>
/// An open session against an <see cref="IConnector"/>, exposing the <see cref="IExecutionTarget"/> that
/// operations are actually performed through.
/// </summary>
public interface IConnectorSession : IAsyncDisposable
{
    /// <summary>The channels available on this specific, already-opened session.</summary>
    ConnectorChannel AvailableChannels { get; }

    /// <summary>The execution target operations are performed through for the lifetime of this session.</summary>
    IExecutionTarget ExecutionTarget { get; }
}
