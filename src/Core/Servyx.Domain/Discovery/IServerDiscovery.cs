namespace Servyx.Domain.Discovery;

/// <summary>A published network port, as reported by the workload's execution environment.</summary>
/// <param name="HostPort">The host port it is published on, or <see langword="null"/> if not published to the host.</param>
/// <param name="ContainerPort">The port as exposed inside the container/process.</param>
/// <param name="Protocol">The transport protocol, e.g. <c>"tcp"</c> or <c>"udp"</c>.</param>
public sealed record DiscoveredPort(int? HostPort, int ContainerPort, string Protocol);

/// <summary>A single bind mount or volume attached to a discovered workload.</summary>
/// <param name="Source">The mount's source path (or volume name) on the host.</param>
/// <param name="Destination">The mount's destination path inside the container.</param>
/// <param name="ReadWrite">Whether the mount is writable from inside the container.</param>
public sealed record DiscoveredMount(string Source, string Destination, bool ReadWrite);

/// <summary>
/// A workload discovered as a candidate adoption match for a game definition's deployment profile.
/// </summary>
/// <param name="ServerId">
/// The transport-native identifier for this workload (e.g. a Docker container id). Not yet a stable
/// Servyx <c>Server</c> entity id — that mapping is introduced when adoption is persisted.
/// </param>
/// <param name="EnvironmentVariables">
/// The workload's environment variables exactly as observed on the live target, keyed by variable name.
/// This commonly includes secret-carrying keys (e.g. <c>ADMIN_PASSWORD</c>, <c>SERVER_PASSWORD</c> for
/// the Palworld deployment). <strong>Callers must never surface a value from this dictionary — or this
/// dictionary itself — to a view model, the DOM, a log message, or an exception message without first
/// passing it through a secret redactor.</strong> Consumers should read only the specific keys they need
/// and mask any key known to carry a secret before it leaves their mapping step.
/// </param>
/// <param name="HostKey">
/// The name of the host this workload was discovered on, when discovery fans out across more than one host
/// (see <c>CompositeServerDiscovery</c> in <c>Servyx.Infrastructure.Ssh.Docker</c>). <see langword="null"/>
/// for a single-host <see cref="IServerDiscovery"/> implementation (e.g. <c>DockerServerDiscovery</c>,
/// <c>SshDockerServerDiscovery</c> used directly) that has no notion of "which host" — trailing and optional
/// so every existing construction site stays source-compatible.
/// </param>
public sealed record DiscoveredServer(
    string ServerId,
    string Name,
    string Image,
    string? ImageDigest,
    string State,
    string HealthStatus,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? StartedAt,
    IReadOnlyList<DiscoveredPort> Ports,
    IReadOnlyList<DiscoveredMount> Mounts,
    string? NetworkName,
    string? ContainerIp,
    long? MemoryLimitBytes,
    double? CpuLimit,
    string? RestartPolicy,
    IReadOnlyDictionary<string, string> ComposeLabels,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? HostKey = null);

/// <summary>
/// Discovers existing workloads that match a game definition's deployment profile, so they can be
/// adopted into Servyx rather than requiring a fresh container/process to be created. Implementations
/// (e.g. <c>DockerServerDiscovery</c> in <c>Servyx.Infrastructure.Docker</c>) are purely a read: they
/// list and inspect workloads, never create, start, or modify one. Declared in <c>Servyx.Domain</c> so
/// <c>Servyx.Application</c> can consume discovery without referencing any specific transport's
/// infrastructure project.
/// </summary>
public interface IServerDiscovery
{
    /// <summary>
    /// Finds all workloads whose image repository matches <paramref name="imageRepository"/> (ignoring
    /// tag and digest) and which have a mount whose container-side destination equals
    /// <paramref name="requiredMountContainerPath"/>.
    /// </summary>
    Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        string imageRepository,
        string requiredMountContainerPath,
        CancellationToken ct = default);
}
