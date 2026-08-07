namespace Servyx.Domain.Definitions.Model;

/// <summary>
/// The parsed shape of a definition's <c>capabilities</c> block: the declared blast radius of the
/// deployment, checked against trust tier restrictions before anything runs.
/// </summary>
/// <param name="Network">Ports the workload uses.</param>
/// <param name="Filesystem">Paths the workload touches.</param>
/// <param name="Egress">
/// Outbound network destinations the workload is allowed to reach. <c>docs/provisioning.md</c> flags an
/// empty list here as ambiguous between "no egress needed" and "unspecified" pending a future tri-state
/// fix — this type reproduces whatever the definition declares, unmodified.
/// </param>
/// <param name="Shell">Whether any step in this definition requires shell execution.</param>
/// <param name="Privileged">Whether the workload requires privileged mode.</param>
/// <param name="HostNetwork">Whether the workload requires host networking.</param>
public sealed record Capabilities(
    IReadOnlyList<NetworkPortCapability> Network,
    IReadOnlyList<FilesystemCapability> Filesystem,
    IReadOnlyList<EgressRule> Egress,
    bool Shell,
    bool Privileged,
    bool HostNetwork);

/// <summary>The wire protocol a declared network port is used over.</summary>
public enum NetworkProtocol
{
    /// <summary>TCP.</summary>
    Tcp,

    /// <summary>UDP.</summary>
    Udp,
}

/// <summary>One entry of <see cref="Capabilities.Network"/>: a single port the workload uses.</summary>
/// <param name="Port">The port, either a literal or a reference to the setting that determines it.</param>
/// <param name="Protocol">The wire protocol.</param>
/// <param name="Purpose">
/// A short identifier for what the port is for, e.g. <c>game</c>, <c>rcon</c>. Must be unique within a
/// deployment — see the "Port <c>purpose</c> values must be unique" rule in <c>docs/schema.md</c>.
/// </param>
/// <param name="Var">The <c>.env</c> variable this port is sourced from, if any.</param>
/// <param name="Published">Whether this port is published to the host by default.</param>
public sealed record NetworkPortCapability(PortRef Port, NetworkProtocol Protocol, string Purpose, string? Var, bool Published);

/// <summary>The access a workload has to a declared filesystem path.</summary>
public enum FilesystemAccess
{
    /// <summary>Read-only access.</summary>
    ReadOnly,

    /// <summary>Read-write access.</summary>
    ReadWrite,
}

/// <summary>One entry of <see cref="Capabilities.Filesystem"/>: a path the workload touches.</summary>
/// <param name="Path">The path, e.g. <c>${DATA_DIR}</c>.</param>
/// <param name="Access">Whether the workload only reads or also writes this path.</param>
/// <param name="Purpose">A human-readable description of what lives at this path.</param>
public sealed record FilesystemCapability(string Path, FilesystemAccess Access, string Purpose);

/// <summary>
/// One entry of <see cref="Capabilities.Egress"/>: an outbound network destination the workload is allowed
/// to reach.
/// </summary>
/// <remarks>
/// <c>definitions/palworld-docker.yaml</c> declares an empty <c>egress</c> list and the repository has no
/// worked example of a populated one, so this shape is a judgment call pending a real example to validate
/// it against.
/// </remarks>
/// <param name="Destination">The allowed destination, e.g. a hostname or CIDR.</param>
/// <param name="Port">The destination port, if the rule is restricted to one.</param>
/// <param name="Purpose">A human-readable description of why the workload needs this destination.</param>
public sealed record EgressRule(string Destination, int? Port, string? Purpose);
