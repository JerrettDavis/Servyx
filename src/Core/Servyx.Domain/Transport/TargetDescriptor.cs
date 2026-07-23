namespace Servyx.Domain.Transport;

/// <summary>
/// Identifies a specific target reachable through a transport. Immutable; two descriptors with equal
/// values are considered the same target.
/// </summary>
/// <param name="TransportId">"local" | "docker" | "ssh" | "ssh+docker".</param>
/// <param name="Endpoint">
/// Transport-specific endpoint address, e.g. "npipe://./pipe/dockerDesktopLinuxEngine" for local Docker
/// Desktop, or "ssh://host:22" for a remote host.
/// </param>
/// <param name="CredentialUrn">URN identifying credentials in the secret store, if any. Never a literal credential.</param>
/// <param name="DockerContext">Named Docker context to use, when applicable (e.g. "desktop-linux").</param>
/// <param name="Options">Additional transport-specific key/value options.</param>
public sealed record TargetDescriptor(
    string TransportId,
    string Endpoint,
    string? CredentialUrn,
    string? DockerContext,
    IReadOnlyDictionary<string, string> Options);

/// <summary>Result of a reachability probe.</summary>
/// <param name="Reachable">Whether the target responded.</param>
/// <param name="Latency">Round-trip time of the probe, if reachable.</param>
/// <param name="Detail">Human-readable detail, especially on failure.</param>
public sealed record TargetHealth(bool Reachable, TimeSpan? Latency, string? Detail);
