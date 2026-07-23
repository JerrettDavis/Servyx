namespace Servyx.Domain.Connectors;

/// <summary>
/// Timeout and concurrency policy for sessions opened against a connector.
/// </summary>
/// <param name="Connect">How long to wait for the initial connection/handshake to complete.</param>
/// <param name="Command">How long to wait for a single command execution to complete.</param>
/// <param name="FileTransfer">How long to wait for a single file read or write to complete.</param>
/// <param name="IdleEviction">
/// How long a pooled connection may sit unused before <see cref="IConnectorPool"/> evicts it. Long-lived
/// consumers (log streaming, a metrics poll loop) hold a lease for their entire lifetime and are exempt
/// from this — see <c>docs/connectors.md</c>, "Pooling".
/// </param>
/// <param name="MaxConcurrentSessions">
/// The maximum number of logical sessions (exec, sftp, port-forward) the pool multiplexes over one
/// pooled connection for a given <see cref="ConnectorKey"/>.
/// </param>
public sealed record TimeoutPolicy(
    TimeSpan Connect,
    TimeSpan Command,
    TimeSpan FileTransfer,
    TimeSpan IdleEviction,
    int MaxConcurrentSessions)
{
    /// <summary>
    /// The documented defaults: 10s connect, 30s command, 10m file transfer, 5m idle eviction, 4 max
    /// concurrent sessions.
    /// </summary>
    public static TimeoutPolicy Default { get; } = new(
        Connect: TimeSpan.FromSeconds(10),
        Command: TimeSpan.FromSeconds(30),
        FileTransfer: TimeSpan.FromMinutes(10),
        IdleEviction: TimeSpan.FromMinutes(5),
        MaxConcurrentSessions: 4);
}
