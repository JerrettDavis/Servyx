using System.Globalization;
using System.Text.Json;
using Servyx.Domain.Discovery;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>A single line of <c>docker container ls --all --no-trunc --format '{{json .}}'</c> output.</summary>
public sealed record DockerContainerListEntry(
    string Id,
    string Name,
    string Image,
    string State,
    string Status,
    string HealthStatus,
    string CreatedAt,
    string Ports,
    string Mounts,
    string Networks,
    string Command,
    string Labels);

/// <summary>The subset of <c>docker version --format '{{json .}}'</c> Servyx needs.</summary>
/// <param name="ServerVersion">The Docker Engine (daemon) version — what Servyx needs to know it can talk to.</param>
/// <param name="ClientVersion">The docker CLI version, if reported.</param>
/// <param name="ApiVersion">The daemon's negotiated API version, if reported.</param>
public sealed record DockerVersionInfo(string ServerVersion, string? ClientVersion, string? ApiVersion);

/// <summary>A single non-streaming snapshot from <c>docker stats --no-stream --format '{{json .}}'</c>.</summary>
/// <param name="MemoryUsage">The raw <c>"used / limit"</c> string as reported (e.g. <c>"2.141GiB / 8GiB"</c>).</param>
public sealed record DockerContainerStats(
    string Container,
    double? CpuPercent,
    string? MemoryUsage,
    long? MemoryUsageBytes,
    long? MemoryLimitBytes,
    double? MemoryPercent);

/// <summary>
/// Parses <c>docker</c> CLI JSON output (from <c>container inspect</c>, <c>container ls</c>,
/// <c>version</c>, and <c>stats</c>) into Servyx's discovery model, for the ssh+docker transport that
/// manages a remote game server by running docker CLI commands over an SSH exec channel rather than
/// talking to the Docker Engine API directly (as <c>Servyx.Infrastructure.Docker</c> does via
/// Docker.DotNet).
/// </summary>
/// <remarks>
/// This class intentionally does not reference <c>Servyx.Infrastructure.Docker</c> — doing so would drag
/// the Docker.DotNet dependency into this SSH-only assembly, which never talks to a Docker Engine API
/// endpoint at all (everything here is parsed from CLI stdout). Every field access below is defensive:
/// docker's JSON shape varies across engine versions and container configurations (most notably, a
/// container with no healthcheck omits <c>State.Health</c> entirely), so every optional field absent
/// must yield a sane default rather than a <see cref="NullReferenceException"/> or parse exception.
/// </remarks>
public static class DockerInspectJson
{
    /// <summary>
    /// Parses the output of <c>docker container inspect &lt;id&gt;</c> — a JSON array with one element
    /// per requested container — into the transport-agnostic <see cref="DiscoveredServer"/> shape for
    /// element <c>[0]</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The array is empty (docker returns this when the container id/name did not resolve), or the
    /// element is missing its <c>Id</c> field.
    /// </exception>
    public static DiscoveredServer ParseInspect(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "'docker container inspect' returned an empty array; expected exactly one container.");
        }

        return MapContainer(root[0]);
    }

    /// <summary>
    /// Parses the output of <c>docker container ls --all --no-trunc --format '{{json .}}'</c> — one JSON
    /// object per line — tolerating blank/whitespace lines (including a trailing newline).
    /// </summary>
    public static IReadOnlyList<DockerContainerListEntry> ParseContainerList(string jsonLines)
    {
        ArgumentNullException.ThrowIfNull(jsonLines);

        var result = new List<DockerContainerListEntry>();
        foreach (var rawLine in jsonLines.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            result.Add(new DockerContainerListEntry(
                Id: GetString(root, "ID") ?? "",
                Name: (GetString(root, "Names") ?? "").TrimStart('/'),
                Image: GetString(root, "Image") ?? "",
                State: GetString(root, "State") ?? "",
                Status: GetString(root, "Status") ?? "",
                HealthStatus: GetString(root, "HealthStatus") ?? "",
                CreatedAt: GetString(root, "CreatedAt") ?? "",
                Ports: GetString(root, "Ports") ?? "",
                Mounts: GetString(root, "Mounts") ?? "",
                Networks: GetString(root, "Networks") ?? "",
                Command: GetString(root, "Command") ?? "",
                Labels: GetString(root, "Labels") ?? ""));
        }

        return result;
    }

    /// <summary>Parses the output of <c>docker version --format '{{json .}}'</c>.</summary>
    /// <exception cref="InvalidOperationException">The <c>Server.Version</c> field is missing.</exception>
    public static DockerVersionInfo ParseVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var server = GetObject(root, "Server");
        var client = GetObject(root, "Client");

        var serverVersion = GetString(server, "Version")
            ?? throw new InvalidOperationException("'docker version' JSON is missing 'Server.Version'.");

        return new DockerVersionInfo(
            ServerVersion: serverVersion,
            ClientVersion: GetString(client, "Version"),
            ApiVersion: GetString(server, "ApiVersion"));
    }

    /// <summary>
    /// Parses a single snapshot from <c>docker stats --no-stream --format '{{json .}}' &lt;container&gt;</c>.
    /// </summary>
    public static DockerContainerStats ParseStats(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var memoryUsageText = GetString(root, "MemUsage");
        var (usageBytes, limitBytes) = ParseMemUsage(memoryUsageText);

        return new DockerContainerStats(
            Container: GetString(root, "Container") ?? GetString(root, "Name") ?? "",
            CpuPercent: ParsePercent(GetString(root, "CPUPerc")),
            MemoryUsage: memoryUsageText,
            MemoryUsageBytes: usageBytes,
            MemoryLimitBytes: limitBytes,
            MemoryPercent: ParsePercent(GetString(root, "MemPerc")));
    }

    private static DiscoveredServer MapContainer(JsonElement root)
    {
        var id = GetString(root, "Id")
            ?? throw new InvalidOperationException("'docker container inspect' element is missing 'Id'.");

        var rawName = GetString(root, "Name") ?? id;
        var name = rawName.StartsWith('/') ? rawName[1..] : rawName;

        var config = GetObject(root, "Config");
        var image = GetString(config, "Image") ?? "unknown";

        // The top-level "Image" field on an inspect result is the resolved sha256 image id (the same
        // value Docker.DotNet's ContainerListResponse.ImageID reports), distinct from Config.Image
        // (the human-readable "repo:tag" reference) — mirrors DockerServerDiscovery's ImageDigest mapping.
        var resolvedImageId = GetString(root, "Image");
        var imageDigest = resolvedImageId is not null && resolvedImageId.StartsWith("sha256:", StringComparison.Ordinal)
            ? resolvedImageId
            : null;

        var state = GetObject(root, "State");
        var status = GetString(state, "Status") ?? "unknown";

        // A container whose image declares no HEALTHCHECK omits "State.Health" entirely — this must not
        // throw or NRE, it must simply report "no health information available".
        var health = GetObject(state, "Health");
        var healthStatus = GetString(health, "Status") ?? "none";

        var createdAt = TryGetDateTimeOffset(root, "Created");

        // Docker reports the zero-value sentinel "0001-01-01T00:00:00Z" for StartedAt on a container
        // that has never run; treat that (and anything before the Unix epoch) as "never started" rather
        // than a real timestamp, matching DockerServerDiscovery's Docker.DotNet-based mapping.
        var startedAt = TryGetDateTimeOffset(state, "StartedAt");
        if (startedAt is { } started && started <= DateTimeOffset.UnixEpoch)
        {
            startedAt = null;
        }

        var networkSettings = GetObject(root, "NetworkSettings");
        var ports = ParsePorts(networkSettings);
        var mounts = ParseMounts(root);

        var (networkName, containerIp) = ParsePrimaryNetwork(networkSettings);

        var hostConfig = GetObject(root, "HostConfig");
        var memoryLimit = TryGetInt64(hostConfig, "Memory");
        if (memoryLimit is <= 0)
        {
            memoryLimit = null;
        }

        var cpuLimit = ResolveCpuLimit(hostConfig);

        var restartPolicyObject = GetObject(hostConfig, "RestartPolicy");
        var restartPolicy = GetString(restartPolicyObject, "Name");
        if (string.IsNullOrEmpty(restartPolicy))
        {
            restartPolicy = null;
        }

        return new DiscoveredServer(
            ServerId: id,
            Name: name,
            Image: image,
            ImageDigest: imageDigest,
            State: status,
            HealthStatus: healthStatus,
            CreatedAt: createdAt,
            StartedAt: startedAt,
            Ports: ports,
            Mounts: mounts,
            NetworkName: networkName,
            ContainerIp: containerIp,
            MemoryLimitBytes: memoryLimit,
            CpuLimit: cpuLimit,
            RestartPolicy: restartPolicy,
            ComposeLabels: ParseComposeLabels(config),
            EnvironmentVariables: ParseEnv(config));
    }

    /// <summary>
    /// Maps <c>NetworkSettings.Ports</c>, keyed <c>"&lt;containerPort&gt;/&lt;protocol&gt;"</c>.
    /// </summary>
    /// <remarks>
    /// The single most important mapping in this file: a port whose value is JSON <c>null</c> means
    /// "exposed inside the container but not published to any host port" — represented here as
    /// <see cref="DiscoveredPort.HostPort"/> = <see langword="null"/>, which is exactly how a published
    /// port with no assigned host binding would otherwise be indistinguishable, EXCEPT that a published
    /// port always carries at least one binding object in its array. A remote deployment's RCON port
    /// (25575/tcp in the Palworld fixture) is deliberately exposed-not-published — callers must be able
    /// to tell that apart from "published on some port" so they know to reach it only via <c>docker
    /// exec</c>, never a direct host socket.
    /// </remarks>
    private static IReadOnlyList<DiscoveredPort> ParsePorts(JsonElement networkSettings)
    {
        var portsElement = GetObject(networkSettings, "Ports");
        if (portsElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<DiscoveredPort>();
        foreach (var portEntry in portsElement.EnumerateObject())
        {
            var (containerPort, protocol) = SplitPortKey(portEntry.Name);
            if (containerPort is null)
            {
                continue;
            }

            var bindings = portEntry.Value;
            if (bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() == 0)
            {
                result.Add(new DiscoveredPort(null, containerPort.Value, protocol));
                continue;
            }

            // A single container port can carry MULTIPLE host bindings — typically one for the IPv4
            // wildcard (0.0.0.0) and one for the IPv6 wildcard ([::]). Servyx.Domain's DiscoveredPort has
            // no HostIp field, so (unlike DockerServerDiscovery's Docker.DotNet-based mapping, which
            // de-duplicates ListContainersAsync's one-entry-per-host-IP results) every binding here is
            // surfaced as its own entry rather than collapsed, so no binding is silently dropped.
            foreach (var binding in bindings.EnumerateArray())
            {
                var hostPortText = GetString(binding, "HostPort");
                var hostPort = int.TryParse(hostPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHostPort)
                    ? (int?)parsedHostPort
                    : null;

                result.Add(new DiscoveredPort(hostPort, containerPort.Value, protocol));
            }
        }

        return result;
    }

    private static (int? Port, string Protocol) SplitPortKey(string key)
    {
        var slash = key.IndexOf('/');
        if (slash < 0)
        {
            return (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare) ? bare : null, "tcp");
        }

        var portText = key[..slash];
        var protocol = key[(slash + 1)..];
        var port = int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (int?)null;
        return (port, string.IsNullOrEmpty(protocol) ? "tcp" : protocol);
    }

    private static IReadOnlyList<DiscoveredMount> ParseMounts(JsonElement root)
    {
        if (!root.TryGetProperty("Mounts", out var mountsElement) || mountsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<DiscoveredMount>();
        foreach (var mount in mountsElement.EnumerateArray())
        {
            var source = GetString(mount, "Source");
            var destination = GetString(mount, "Destination");
            if (source is null || destination is null)
            {
                continue;
            }

            var readWrite = false;
            if (mount.TryGetProperty("RW", out var rwElement)
                && (rwElement.ValueKind == JsonValueKind.True || rwElement.ValueKind == JsonValueKind.False))
            {
                readWrite = rwElement.GetBoolean();
            }

            result.Add(new DiscoveredMount(source, destination, readWrite));
        }

        return result;
    }

    private static (string? NetworkName, string? ContainerIp) ParsePrimaryNetwork(JsonElement networkSettings)
    {
        var networks = GetObject(networkSettings, "Networks");
        if (networks.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        foreach (var network in networks.EnumerateObject())
        {
            var ip = GetString(network.Value, "IPAddress");
            return (network.Name, string.IsNullOrEmpty(ip) ? null : ip);
        }

        return (null, null);
    }

    private static IReadOnlyDictionary<string, string> ParseComposeLabels(JsonElement config)
    {
        var labels = GetObject(config, "Labels");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var label in labels.EnumerateObject())
        {
            if (label.Value.ValueKind == JsonValueKind.String
                && label.Name.StartsWith("com.docker.compose.", StringComparison.Ordinal))
            {
                result[label.Name] = label.Value.GetString() ?? "";
            }
        }

        return result;
    }

    /// <summary>
    /// Parses <c>Config.Env</c> (each entry shaped <c>KEY=VALUE</c>) into a dictionary. Entries with no
    /// <c>=</c> are skipped rather than throwing, since a malformed entry here is a workload quirk, not a
    /// Servyx bug. <strong>Contains secret-carrying keys such as <c>ADMIN_PASSWORD</c> /
    /// <c>SERVER_PASSWORD</c></strong> — see <see cref="DiscoveredServer.EnvironmentVariables"/> for the
    /// handling contract callers must honor.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseEnv(JsonElement config)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!config.TryGetProperty("Env", out var envElement) || envElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var entry in envElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = entry.GetString();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var separatorIndex = text.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            result[text[..separatorIndex]] = text[(separatorIndex + 1)..];
        }

        return result;
    }

    private static double? ResolveCpuLimit(JsonElement hostConfig)
    {
        var nanoCpus = TryGetInt64(hostConfig, "NanoCpus");
        if (nanoCpus is > 0)
        {
            return nanoCpus.Value / 1_000_000_000.0;
        }

        var quota = TryGetInt64(hostConfig, "CpuQuota");
        var period = TryGetInt64(hostConfig, "CpuPeriod");
        if (quota is > 0 && period is > 0)
        {
            return (double)quota.Value / period.Value;
        }

        return null;
    }

    private static double? ParsePercent(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var trimmed = text.TrimEnd('%');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Parses a <c>"used / limit"</c> pair as reported by <c>docker stats</c> (e.g. <c>"2.141GiB / 8GiB"</c>).</summary>
    private static (long? Usage, long? Limit) ParseMemUsage(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        var parts = text.Split('/', 2);
        var usage = ParseByteSize(parts.Length > 0 ? parts[0] : null);
        var limit = parts.Length > 1 ? ParseByteSize(parts[1]) : null;
        return (usage, limit);
    }

    /// <summary>Parses a Docker-formatted byte size such as <c>"2.141GiB"</c>, <c>"512MiB"</c>, or <c>"0B"</c>.</summary>
    private static long? ParseByteSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        var unitStart = 0;
        while (unitStart < text.Length && (char.IsAsciiDigit(text[unitStart]) || text[unitStart] == '.'))
        {
            unitStart++;
        }

        if (unitStart == 0)
        {
            return null;
        }

        if (!double.TryParse(text[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = text[unitStart..].Trim().ToUpperInvariant();
        double multiplier = unit switch
        {
            "" or "B" => 1,
            "KB" => 1_000,
            "KIB" => 1024,
            "MB" => 1_000_000,
            "MIB" => 1024.0 * 1024,
            "GB" => 1_000_000_000,
            "GIB" => 1024.0 * 1024 * 1024,
            "TB" => 1_000_000_000_000,
            "TIB" => 1024.0 * 1024 * 1024 * 1024,
            _ => 0,
        };

        return multiplier == 0 ? null : (long)(number * multiplier);
    }

    private static JsonElement GetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? TryGetInt64(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var result)
            ? result
            : null;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.TryGetDateTimeOffset(out var result))
        {
            return result;
        }

        return null;
    }

    // --- Duplicated (not shared) from Servyx.Infrastructure.Docker.DockerServerDiscovery ---
    //
    // These two helpers are intentionally copied rather than referenced: Servyx.Infrastructure.Ssh must
    // not take a project reference on Servyx.Infrastructure.Docker, because that project's whole purpose
    // is talking to the Docker Engine API via Docker.DotNet, and pulling that dependency into this
    // SSH-only assembly (which only ever shells out to the docker CLI over an exec channel, never touches
    // the Engine API) would be a real, unwanted dependency, not just an unused one. Keep both copies in
    // sync by hand if the matching rule ever changes.

    /// <summary>
    /// Whether an image reference (e.g. <c>"repo:tag"</c>, <c>"repo"</c>, <c>"repo@sha256:..."</c>, or
    /// <c>"registry:port/repo:tag"</c>) refers to the given bare repository name, ignoring any tag
    /// and/or digest suffix.
    /// </summary>
    public static bool ImageRepositoryMatches(string? imageReference, string expectedRepository)
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
    public static string StripTagAndDigest(string imageReference)
    {
        var atIndex = imageReference.IndexOf('@', StringComparison.Ordinal);
        var withoutDigest = atIndex >= 0 ? imageReference[..atIndex] : imageReference;

        var lastSlash = withoutDigest.LastIndexOf('/');
        var lastColon = withoutDigest.LastIndexOf(':');

        return lastColon > lastSlash ? withoutDigest[..lastColon] : withoutDigest;
    }
}
