namespace Servyx.Domain.Connectors;

/// <summary>
/// The observed health of a specific <see cref="IConnector"/> instance, as returned by
/// <see cref="IConnector.CheckAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Degraded"/> is the partial-availability answer, and it is the field that makes this type
/// worth having instead of a bool. Consider an SSH connector where exec works fine but the sftp subsystem
/// is disabled on the remote <c>sshd</c>: <see cref="Working"/> would be <c>Exec | ProcessApi</c> and
/// <see cref="Degraded"/> would be <c>FileRead | FileWrite</c>, with an <see cref="Issues"/> entry naming
/// <c>sshd_config</c> as the place to look.
/// </para>
/// <para>
/// This must be sharply distinguished from a <c>ControlCapability</c> denial (see
/// <c>docs/control-plane.md</c>): "can I reach the host and talk to it at all" and "may I write this
/// specific file" are different questions, fail for different reasons, and are fixed by different actors.
/// </para>
/// </remarks>
/// <param name="Reachable">Whether the connector's endpoint responded at all.</param>
/// <param name="Working">
/// The channels observed to be genuinely working right now. Always a subset of
/// <see cref="ConnectorDescriptor.DeclaredChannels"/>.
/// </param>
/// <param name="Degraded">
/// Channels the connector's descriptor declares but which were observed <em>not</em> to work, alongside
/// an explanation in <see cref="Issues"/>. Disjoint from <see cref="Working"/>.
/// </param>
/// <param name="Issues">Human-readable explanations for anything in <see cref="Degraded"/>, or connection problems.</param>
/// <param name="Latency">Round-trip time of the health check, if the connector was reachable.</param>
/// <param name="CheckedAt">When this health snapshot was produced.</param>
public sealed record ConnectorHealth(
    bool Reachable,
    ConnectorChannel Working,
    ConnectorChannel Degraded,
    IReadOnlyList<string> Issues,
    TimeSpan Latency,
    DateTimeOffset CheckedAt);
