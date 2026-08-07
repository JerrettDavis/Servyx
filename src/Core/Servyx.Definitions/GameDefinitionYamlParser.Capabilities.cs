using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> CapabilitiesKeys =
        new HashSet<string>(StringComparer.Ordinal) { "network", "filesystem", "egress", "shell", "privileged", "hostNetwork" };

    private static readonly IReadOnlySet<string> NetworkPortKeys =
        new HashSet<string>(StringComparer.Ordinal) { "port", "protocol", "purpose", "var", "published" };

    private static readonly IReadOnlySet<string> FilesystemKeys =
        new HashSet<string>(StringComparer.Ordinal) { "path", "access", "purpose" };

    private static readonly IReadOnlySet<string> EgressKeys =
        new HashSet<string>(StringComparer.Ordinal) { "destination", "port", "purpose" };

    private static Capabilities? ParseCapabilities(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var map = RequireMapping(root, "capabilities", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, CapabilitiesKeys, issues, "'capabilities'");

        var network = new List<NetworkPortCapability>();
        var purposesSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entryNode in OptionalSequence(map, "network", issues, "'capabilities'"))
        {
            var entryMap = AsMapping(entryNode, issues, "An entry of 'capabilities.network'");
            if (entryMap is null)
            {
                continue;
            }

            RejectUnknownKeys(entryMap, NetworkPortKeys, issues, "A 'capabilities.network' entry");

            var port = ParsePortRef(entryMap, "port", issues, state, allowHostVariable: false, "A 'capabilities.network' entry");
            var protocol = ParseNetworkProtocol(entryMap, issues);
            var purpose = RequireString(entryMap, "purpose", issues, "A 'capabilities.network' entry");
            var var = OptionalString(entryMap, "var", issues, "A 'capabilities.network' entry");
            var published = RequireBool(entryMap, "published", issues, "A 'capabilities.network' entry");

            if (var is not null)
            {
                entryMap.TryGet("var", out var varNode);
                state.PendingVariableRefs.Add((var, varNode, AllowHostVariable: false));
            }

            if (purpose is not null && !purposesSeen.Add(purpose))
            {
                entryMap.TryGet("purpose", out var purposeNode);
                issues.Error($"'capabilities.network' declares 'purpose: {purpose}' more than once; purpose values must be unique.", purposeNode);
            }

            if (port is not null && protocol is not null && purpose is not null)
            {
                network.Add(new NetworkPortCapability(port, protocol.Value, purpose, var, published));
            }
        }

        var filesystem = new List<FilesystemCapability>();
        foreach (var entryNode in OptionalSequence(map, "filesystem", issues, "'capabilities'"))
        {
            var entryMap = AsMapping(entryNode, issues, "An entry of 'capabilities.filesystem'");
            if (entryMap is null)
            {
                continue;
            }

            RejectUnknownKeys(entryMap, FilesystemKeys, issues, "A 'capabilities.filesystem' entry");

            var path = RequireString(entryMap, "path", issues, "A 'capabilities.filesystem' entry");
            var access = ParseFilesystemAccess(entryMap, issues);
            var purpose = RequireString(entryMap, "purpose", issues, "A 'capabilities.filesystem' entry");

            if (path is not null)
            {
                entryMap.TryGet("path", out var pathNode);
                ValidateContainedPath(path, pathNode, issues, "A 'capabilities.filesystem' entry's 'path'");
                QueueTemplateTokens(path, pathNode, state);
            }

            if (path is not null && access is not null && purpose is not null)
            {
                filesystem.Add(new FilesystemCapability(path, access.Value, purpose));
            }
        }

        var egress = new List<EgressRule>();
        foreach (var entryNode in OptionalSequence(map, "egress", issues, "'capabilities'"))
        {
            var entryMap = AsMapping(entryNode, issues, "An entry of 'capabilities.egress'");
            if (entryMap is null)
            {
                continue;
            }

            RejectUnknownKeys(entryMap, EgressKeys, issues, "A 'capabilities.egress' entry");
            var destination = RequireString(entryMap, "destination", issues, "A 'capabilities.egress' entry");
            var port = OptionalInt(entryMap, "port", issues, "A 'capabilities.egress' entry");
            var purpose = OptionalString(entryMap, "purpose", issues, "A 'capabilities.egress' entry");

            if (destination is not null)
            {
                egress.Add(new EgressRule(destination, port, purpose));
            }
        }

        var shell = RequireBool(map, "shell", issues, "'capabilities'");
        var privileged = RequireBool(map, "privileged", issues, "'capabilities'");
        var hostNetwork = RequireBool(map, "hostNetwork", issues, "'capabilities'");

        return new Capabilities(network, filesystem, egress, shell, privileged, hostNetwork);
    }

    private static NetworkProtocol? ParseNetworkProtocol(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "protocol", issues, "A 'capabilities.network' entry");
        if (raw is null)
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "tcp" => NetworkProtocol.Tcp,
            "udp" => NetworkProtocol.Udp,
            _ => Fail(map, issues, raw),
        };

        static NetworkProtocol? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("protocol", out var node);
            issues.Error($"A 'capabilities.network' entry declares 'protocol: {raw}'; only 'tcp' and 'udp' are recognized.", node);
            return null;
        }
    }

    private static FilesystemAccess? ParseFilesystemAccess(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "access", issues, "A 'capabilities.filesystem' entry");
        if (raw is null)
        {
            return null;
        }

        return raw.ToLowerInvariant() switch
        {
            "rw" => FilesystemAccess.ReadWrite,
            "ro" => FilesystemAccess.ReadOnly,
            _ => Fail(map, issues, raw),
        };

        static FilesystemAccess? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("access", out var node);
            issues.Error($"A 'capabilities.filesystem' entry declares 'access: {raw}'; only 'rw' and 'ro' are recognized.", node);
            return null;
        }
    }
}
