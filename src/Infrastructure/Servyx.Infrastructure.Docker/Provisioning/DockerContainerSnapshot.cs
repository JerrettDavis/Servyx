using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet.Models;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// The durable, self-describing record of what a container <em>was</em>, and the only thing a rollback is
/// allowed to restore from.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this type exists at all.</strong> Nothing in Servyx recorded a container's specification
/// before this. The provisioning ledger stores a <c>ResourceHandle</c> — provisioner id, container id,
/// region, and the container's labels — so it knows the image and the root path and nothing whatsoever about
/// ports, environment variables or mounts. The container itself knows all of them, and a recreate deletes
/// the container. A rollback built on those records could restore an image and would have had to invent
/// everything else, which is worse than no rollback: it looks like recovery and is not. So the update path
/// captures one of these first, and the rollback path reads it back.
/// </para>
/// <para>
/// <strong>It never invents.</strong> <see cref="Capture"/> answers <see langword="null"/> for any container
/// it cannot describe completely and truthfully — one that is not Servyx-managed, one missing an identity
/// label, one with no image or name. <see cref="TryDecode"/> answers <see langword="false"/> for anything it
/// cannot parse. Neither substitutes a default for a value it did not read.
/// </para>
/// <para>
/// <strong>Encoded as JSON into a container label.</strong> The record has to survive the removal of the
/// container it describes, a Servyx restart, and a database that may not exist yet, which rules out process
/// memory and (for now) a new ledger table. A label on the <em>replacement</em> container is durable in the
/// Docker Engine itself, travels with the resource, and is visible to <c>docker inspect</c> — so an operator
/// can read the recorded prior state without Servyx.
/// </para>
/// </remarks>
internal sealed class DockerContainerSnapshot
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>The image reference the container was created from.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>The container's name, without the engine's leading slash.</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>The Servyx instance the container backed.</summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>The provisioning job that asked for the container.</summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>The connector the container was reachable through.</summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>The in-container root path recorded on the container.</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>The Docker restart policy name, or <see langword="null"/> if the container carried none.</summary>
    public string? RestartPolicy { get; set; }

    /// <summary>Every port the container exposed, with the host port it was published on if any.</summary>
    public List<SnapshotPort> Ports { get; set; } = [];

    /// <summary>Every mount the container had, as the engine reported it.</summary>
    public List<SnapshotVolume> Volumes { get; set; } = [];

    /// <summary>Every environment variable baked into the container at create time.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The container's non-canonical, non-bookkeeping labels — i.e. the extras a caller supplied, with the
    /// identity keys (rebuilt from <see cref="InstanceId"/> and friends), the descriptive keys (rebuilt from
    /// <see cref="Image"/> and <see cref="RootPath"/>) and this adapter's own bookkeeping keys removed.
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>One exposed port, and the host port it was published on if it was published at all.</summary>
    public sealed class SnapshotPort
    {
        /// <summary>The port as exposed inside the container.</summary>
        public int ContainerPort { get; set; }

        /// <summary>The transport protocol, lower-cased.</summary>
        public string Protocol { get; set; } = "tcp";

        /// <summary>The host port it was published on, or <see langword="null"/> if it was exposed only.</summary>
        public int? HostPort { get; set; }
    }

    /// <summary>One bind mount.</summary>
    public sealed class SnapshotVolume
    {
        /// <summary>The path on the host.</summary>
        public string HostPath { get; set; } = string.Empty;

        /// <summary>The path inside the container.</summary>
        public string ContainerPath { get; set; } = string.Empty;

        /// <summary>Whether the mount was writable.</summary>
        public bool ReadWrite { get; set; }
    }

    /// <summary>
    /// Reads a live container into a snapshot, or answers <see langword="null"/> when it cannot be described
    /// completely.
    /// </summary>
    /// <remarks>
    /// The refusals are the point. A container with no Servyx identity labels is not one this adapter created,
    /// so there is no honest spec to write down for it; a container the engine reports with no image or no
    /// name cannot be recreated from what was read. In every such case the caller gets nothing rather than a
    /// partially-defaulted spec that would later be restored as if it had been observed.
    /// </remarks>
    internal static DockerContainerSnapshot? Capture(ContainerInspectResponse? inspect)
    {
        if (inspect is null)
        {
            return null;
        }

        var labels = inspect.Config?.Labels;
        var tags = ServyxResourceTags.FromLabels(
            labels is null ? null : new Dictionary<string, string>(labels, StringComparer.Ordinal));

        if (tags is null)
        {
            return null;
        }

        var image = inspect.Config?.Image;
        var name = inspect.Name?.TrimStart('/');

        if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var snapshot = new DockerContainerSnapshot
        {
            Image = image,
            ContainerName = name,
            InstanceId = tags.InstanceId,
            JobId = tags.JobId,
            ConnectorId = tags.ConnectorId,
            RootPath = ReadRootPath(labels),
            RestartPolicy = ReadRestartPolicy(inspect.HostConfig?.RestartPolicy?.Name),
            Ports = [.. ReadPorts(inspect)],
            Volumes = [.. ReadVolumes(inspect)],
            Environment = ReadEnvironment(inspect.Config?.Env),
            Labels = ReadExtraLabels(labels),
        };

        return snapshot;
    }

    /// <summary>Turns a snapshot back into the spec that recreates the container it describes.</summary>
    /// <remarks>
    /// Every value comes from the snapshot. The only defaults involved are the ones a container genuinely has
    /// none of — an absent restart policy stays absent rather than becoming <c>"no"</c>.
    /// </remarks>
    internal DockerContainerSpec ToSpec() =>
        new(Image, ContainerName, ServyxResourceTags.For(InstanceId, JobId, ConnectorId))
        {
            RootPath = RootPath,
            RestartPolicy = RestartPolicy,
            Ports = [.. Ports.Select(p => new DockerPortBinding(p.ContainerPort, p.Protocol, p.HostPort))],
            Volumes = [.. Volumes.Select(v => new DockerVolumeMount(v.HostPath, v.ContainerPath, v.ReadWrite))],
            Environment = new Dictionary<string, string>(Environment, StringComparer.Ordinal),
            AdditionalLabels = new Dictionary<string, string>(Labels, StringComparer.Ordinal),
        };

    /// <summary>Encodes a snapshot for storage in a container label.</summary>
    internal string Encode() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Decodes a snapshot previously written by <see cref="Encode"/>, answering <see langword="false"/> for
    /// anything that is absent, unparseable, or missing a value the spec cannot be built without.
    /// </summary>
    /// <remarks>
    /// A malformed record is treated exactly like an absent one, and for the same reason: a rollback that
    /// cannot read what it is restoring must refuse, not fill the gaps.
    /// </remarks>
    internal static bool TryDecode(string? encoded, out DockerContainerSnapshot snapshot)
    {
        snapshot = null!;

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        DockerContainerSnapshot? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<DockerContainerSnapshot>(encoded, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (decoded is null
            || string.IsNullOrWhiteSpace(decoded.Image)
            || string.IsNullOrWhiteSpace(decoded.ContainerName)
            || string.IsNullOrWhiteSpace(decoded.InstanceId)
            || string.IsNullOrWhiteSpace(decoded.JobId)
            || string.IsNullOrWhiteSpace(decoded.ConnectorId))
        {
            return false;
        }

        decoded.Ports ??= [];
        decoded.Volumes ??= [];
        decoded.Environment = new Dictionary<string, string>(decoded.Environment ?? [], StringComparer.Ordinal);
        decoded.Labels = new Dictionary<string, string>(decoded.Labels ?? [], StringComparer.Ordinal);
        decoded.RootPath = string.IsNullOrWhiteSpace(decoded.RootPath) ? "/" : decoded.RootPath;

        snapshot = decoded;
        return true;
    }

    private static string ReadRootPath(IDictionary<string, string>? labels) =>
        labels is not null
        && labels.TryGetValue(ServyxResourceTags.RootPathLabel, out var rootPath)
        && !string.IsNullOrWhiteSpace(rootPath)
            ? rootPath
            : "/";

    /// <summary>
    /// Maps the engine's restart-policy enum back onto the name the provisioning parameter uses, so a
    /// snapshot round-trips through <c>DockerContainerProvisioner.BuildSpec</c>'s vocabulary.
    /// </summary>
    private static string? ReadRestartPolicy(RestartPolicyKind? kind) => kind switch
    {
        RestartPolicyKind.No => "no",
        RestartPolicyKind.Always => "always",
        RestartPolicyKind.OnFailure => "on-failure",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        _ => null,
    };

    private static IEnumerable<SnapshotPort> ReadPorts(ContainerInspectResponse inspect)
    {
        var bindings = inspect.HostConfig?.PortBindings;
        var keys = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in inspect.Config?.ExposedPorts?.Keys ?? [])
        {
            keys.Add(key);
        }

        foreach (var key in bindings?.Keys ?? [])
        {
            keys.Add(key);
        }

        foreach (var key in keys)
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            var portText = slash < 0 ? key : key[..slash];
            var protocol = slash < 0 ? "tcp" : key[(slash + 1)..];

            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var containerPort))
            {
                // An exposed-port key the engine reports in a shape this adapter cannot parse is skipped
                // rather than guessed at; the plan-hash check on the rollback path then fails loudly if the
                // resulting spec no longer describes the container.
                continue;
            }

            int? hostPort = null;
            if (bindings is not null
                && bindings.TryGetValue(key, out var bound)
                && bound is { Count: > 0 }
                && int.TryParse(bound[0]?.HostPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                hostPort = parsed;
            }

            yield return new SnapshotPort
            {
                ContainerPort = containerPort,
                Protocol = protocol.ToLowerInvariant(),
                HostPort = hostPort,
            };
        }
    }

    private static IEnumerable<SnapshotVolume> ReadVolumes(ContainerInspectResponse inspect)
    {
        foreach (var mount in inspect.Mounts ?? [])
        {
            if (mount is null || string.IsNullOrWhiteSpace(mount.Destination))
            {
                continue;
            }

            yield return new SnapshotVolume
            {
                HostPath = mount.Source ?? mount.Name ?? string.Empty,
                ContainerPath = mount.Destination,
                ReadWrite = mount.RW,
            };
        }
    }

    private static Dictionary<string, string> ReadEnvironment(IList<string>? env)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in env ?? [])
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            result[entry[..separator]] = entry[(separator + 1)..];
        }

        return result;
    }

    /// <summary>
    /// The labels that belong in a spec's <see cref="DockerContainerSpec.AdditionalLabels"/>: everything the
    /// container carries except the keys <c>LabelsFor</c> rebuilds for itself and the bookkeeping keys this
    /// adapter owns.
    /// </summary>
    /// <remarks>
    /// Excluding the bookkeeping keys is load-bearing rather than tidiness. If a captured spec carried
    /// <see cref="ServyxResourceTags.PreviousSpecLabel"/>, restoring it would restore a prior-state pointer
    /// too, and a rollback would chain backwards forever instead of stopping at the state it restored.
    /// </remarks>
    private static Dictionary<string, string> ReadExtraLabels(IDictionary<string, string>? labels)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels is null)
        {
            return result;
        }

        foreach (var pair in labels)
        {
            if (IsRebuilt(pair.Key) || ServyxResourceTags.Bookkeeping.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static bool IsRebuilt(string key) =>
        ServyxTagKeys.Canonical.Contains(key, StringComparer.Ordinal)
        || string.Equals(key, ServyxResourceTags.RootPathLabel, StringComparison.Ordinal)
        || string.Equals(key, ServyxResourceTags.ImageLabel, StringComparison.Ordinal);
}
