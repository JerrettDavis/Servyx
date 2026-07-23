namespace Servyx.Domain.Connectors;

/// <summary>
/// Maintains one pooled connection per <see cref="ConnectorKey"/>, multiplexing multiple logical channels
/// (exec, sftp, port-forward) over it rather than opening a new handshake per operation. See
/// <c>docs/connectors.md</c>, "Pooling".
/// </summary>
public interface IConnectorPool
{
    /// <summary>
    /// Leases the pooled connector for <paramref name="key"/>, creating and connecting it if this is the
    /// first lease for that key. The caller must dispose the returned <see cref="IConnectorLease"/> as soon
    /// as it is done with the connector.
    /// </summary>
    Task<IConnectorLease> LeaseAsync(ConnectorKey key, CancellationToken ct = default);
}

/// <summary>
/// A held reference to a pooled <see cref="IConnector"/>. While at least one lease is outstanding for a
/// given <see cref="ConnectorKey"/>, <see cref="IConnectorPool"/> will not evict that connection for
/// idleness. Long-lived consumers (log streaming, a metrics poll loop) should hold their lease for their
/// entire lifetime rather than re-leasing per operation.
/// </summary>
public interface IConnectorLease : IAsyncDisposable
{
    /// <summary>The leased connector.</summary>
    IConnector Connector { get; }
}
