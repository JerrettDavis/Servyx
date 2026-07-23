using Docker.DotNet;
using Docker.DotNet.Models;

namespace Servyx.Infrastructure.Docker;

/// <summary>A published container port, as reported by the Docker Engine.</summary>
/// <param name="HostPort">The host port it is published on, or <see langword="null"/> if not published to the host.</param>
/// <param name="ContainerPort">The port as exposed inside the container.</param>
/// <param name="Protocol">The transport protocol, e.g. <c>"tcp"</c> or <c>"udp"</c>.</param>
public sealed record DiscoveredPort(int? HostPort, int ContainerPort, string Protocol);

/// <summary>A single bind mount or volume attached to a discovered container.</summary>
/// <param name="Source">The mount's source path (or volume name) on the host.</param>
/// <param name="Destination">The mount's destination path inside the container.</param>
/// <param name="ReadWrite">Whether the mount is writable from inside the container.</param>
public sealed record DiscoveredMount(string Source, string Destination, bool ReadWrite);

/// <summary>
/// A container discovered by <see cref="DockerServerDiscovery"/> as a candidate adoption match for a
/// game definition's docker deployment profile.
/// </summary>
public sealed record DiscoveredContainer(
    string ContainerId,
    string ContainerName,
    string Image,
    string? ImageDigest,
    string State,
    string HealthStatus,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? StartedAt,
    IReadOnlyList<DiscoveredPort> PublishedPorts,
    IReadOnlyList<DiscoveredMount> Mounts,
    string? NetworkName,
    string? ContainerIp,
    long? MemoryLimitBytes,
    double? CpuLimit,
    string? RestartPolicy,
    IReadOnlyDictionary<string, string> ComposeLabels);

/// <summary>
/// Discovers existing Docker containers that match a game definition's docker deployment profile, so
/// they can be adopted into Servyx rather than requiring a fresh container to be created. Matching is
/// by image repository (ignoring tag/digest) plus a required container mount path — both must match.
/// </summary>
public sealed class DockerServerDiscovery
{
    private static readonly string[] ComposeLabelKeys =
    [
        "com.docker.compose.project",
        "com.docker.compose.project.config_files",
        "com.docker.compose.project.working_dir",
        "com.docker.compose.service",
    ];

    private readonly IDockerClient _client;

    /// <summary>Creates a discovery service operating against the given Docker client.</summary>
    public DockerServerDiscovery(IDockerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>
    /// Finds all containers whose image repository matches <paramref name="imageRepository"/> (ignoring
    /// tag and digest) and which have a mount whose container-side destination equals
    /// <paramref name="requiredMountContainerPath"/>. Both conditions must hold for a container to be
    /// returned. Purely a read: lists and inspects containers, never creates, starts, or modifies one.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredContainer>> DiscoverAsync(
        string imageRepository,
        string requiredMountContainerPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredMountContainerPath);

        var containers = await _client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true },
            ct).ConfigureAwait(false);

        var results = new List<DiscoveredContainer>();
        foreach (var container in containers)
        {
            if (!Matches(container, imageRepository, requiredMountContainerPath))
            {
                continue;
            }

            var inspect = await _client.Containers.InspectContainerAsync(container.ID, ct).ConfigureAwait(false);
            results.Add(Map(container, inspect));
        }

        return results;
    }

    /// <summary>
    /// Whether a listed container matches both the image-repository and required-mount adoption
    /// criteria. Exposed internally so the matching rule can be unit tested directly against
    /// hand-built <see cref="ContainerListResponse"/> instances, without a Docker daemon.
    /// </summary>
    internal static bool Matches(ContainerListResponse container, string imageRepository, string requiredMountContainerPath)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (!ImageRepositoryMatches(container.Image, imageRepository))
        {
            return false;
        }

        return container.Mounts is not null
            && container.Mounts.Any(m => string.Equals(m.Destination, requiredMountContainerPath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether an image reference (e.g. <c>"repo:tag"</c>, <c>"repo"</c>, <c>"repo@sha256:..."</c>, or
    /// <c>"registry:port/repo:tag"</c>) refers to the given bare repository name, ignoring any tag
    /// and/or digest suffix.
    /// </summary>
    internal static bool ImageRepositoryMatches(string? imageReference, string expectedRepository)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return false;
        }

        return string.Equals(StripTagAndDigest(imageReference), expectedRepository, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips any trailing <c>@digest</c> and/or <c>:tag</c> suffix from an image reference, correctly
    /// distinguishing a tag separator from the <c>:</c> in a <c>host:port</c> registry prefix (the tag
    /// separator, if present, is always the last colon and always occurs after the last slash).
    /// </summary>
    internal static string StripTagAndDigest(string imageReference)
    {
        var atIndex = imageReference.IndexOf('@', StringComparison.Ordinal);
        var withoutDigest = atIndex >= 0 ? imageReference[..atIndex] : imageReference;

        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');

        return lastColon > lastSlash ? withoutDigest[..lastColon] : withoutDigest;
    }

    private static DiscoveredContainer Map(ContainerListResponse container, ContainerInspectResponse inspect)
    {
        var name = container.Names?.FirstOrDefault()?.TrimStart('/') ?? inspect.Name?.TrimStart('/') ?? container.ID;

        var ports = (container.Ports ?? [])
            .Select(p => new DiscoveredPort(p.PublicPort == 0 ? null : p.PublicPort, p.PrivatePort, p.Type ?? "tcp"))
            .ToList();

        var mounts = (container.Mounts ?? [])
            .Select(m => new DiscoveredMount(m.Source, m.Destination, m.RW))
            .ToList();

        var networks = inspect.NetworkSettings?.Networks;
        var firstNetwork = networks?.FirstOrDefault();
        var networkName = firstNetwork?.Key;
        var containerIp = firstNetwork?.Value?.IPAddress;
        if (string.IsNullOrEmpty(containerIp))
        {
            containerIp = null;
        }

        var hostConfig = inspect.HostConfig;
        long? memoryLimit = hostConfig?.Memory is > 0 ? hostConfig.Memory : null;
        double? cpuLimit = ResolveCpuLimit(hostConfig);
        var restartPolicy = hostConfig?.RestartPolicy is null ? null : MapRestartPolicy(hostConfig.RestartPolicy.Name);

        var labels = container.Labels ?? new Dictionary<string, string>();
        var composeLabels = ComposeLabelKeys
            .Where(labels.ContainsKey)
            .ToDictionary(key => key, key => labels[key], StringComparer.Ordinal);

        DateTimeOffset? startedAt = null;
        if (DateTimeOffset.TryParse(inspect.State?.StartedAt, out var parsedStartedAt) && parsedStartedAt > DateTimeOffset.UnixEpoch)
        {
            startedAt = parsedStartedAt;
        }

        return new DiscoveredContainer(
            ContainerId: container.ID,
            ContainerName: name,
            Image: container.Image,
            ImageDigest: string.IsNullOrEmpty(container.ImageID) ? null : container.ImageID,
            State: container.State ?? inspect.State?.Status ?? "unknown",
            HealthStatus: inspect.State?.Health?.Status ?? "none",
            CreatedAt: container.Created == default ? null : new DateTimeOffset(container.Created.ToUniversalTime()),
            StartedAt: startedAt,
            PublishedPorts: ports,
            Mounts: mounts,
            NetworkName: networkName,
            ContainerIp: containerIp,
            MemoryLimitBytes: memoryLimit,
            CpuLimit: cpuLimit,
            RestartPolicy: restartPolicy,
            ComposeLabels: composeLabels);
    }

    private static double? ResolveCpuLimit(HostConfig? hostConfig)
    {
        if (hostConfig is null)
        {
            return null;
        }

        if (hostConfig.NanoCPUs > 0)
        {
            return hostConfig.NanoCPUs / 1_000_000_000.0;
        }

        if (hostConfig.CPUQuota > 0 && hostConfig.CPUPeriod > 0)
        {
            return (double)hostConfig.CPUQuota / hostConfig.CPUPeriod;
        }

        return null;
    }

    private static string MapRestartPolicy(RestartPolicyKind kind) => kind switch
    {
        RestartPolicyKind.No => "no",
        RestartPolicyKind.Always => "always",
        RestartPolicyKind.OnFailure => "on-failure",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        _ => "no",
    };
}
