using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> MetadataKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "name", "version", "license", "tags", "summary", "description", "vendor", "documentationUrl", "icon", "accentColor",
    };

    private static readonly IReadOnlySet<string> VendorKeys = new HashSet<string>(StringComparer.Ordinal) { "name", "url" };

    /// <summary>
    /// Parses the <c>metadata</c> block. <c>id</c>, <c>name</c>, and <c>version</c> are mandatory per
    /// <c>docs/schema.md</c>'s "Required fields" rule — missing or blank is an Error. Every other field is
    /// modeled ahead of the YAML actually gaining it (see the remarks on <see cref="GameMetadata"/>) and so
    /// is optional here too.
    /// </summary>
    private static GameMetadata? ParseMetadata(YamlMappingNode root, ParseIssues issues)
    {
        var map = RequireMapping(root, "metadata", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, MetadataKeys, issues, "'metadata'");

        var id = RequireString(map, "id", issues, "'metadata'");
        var name = RequireString(map, "name", issues, "'metadata'");
        var version = RequireString(map, "version", issues, "'metadata'");
        var license = OptionalString(map, "license", issues, "'metadata'");
        var tags = OptionalStringList(map, "tags", issues, "'metadata'");
        var summary = OptionalString(map, "summary", issues, "'metadata'");
        var description = OptionalString(map, "description", issues, "'metadata'");
        var accentColor = OptionalString(map, "accentColor", issues, "'metadata'");

        VendorRef? vendor = null;
        if (map.TryGet("vendor", out var vendorNode))
        {
            var vendorMap = AsMapping(vendorNode, issues, "'metadata.vendor'");
            if (vendorMap is not null)
            {
                RejectUnknownKeys(vendorMap, VendorKeys, issues, "'metadata.vendor'");
                var vendorName = RequireString(vendorMap, "name", issues, "'metadata.vendor'");
                var vendorUrl = ParseUri(vendorMap, "url", issues, "'metadata.vendor'");
                vendor = vendorName is null ? null : new VendorRef(vendorName, vendorUrl);
            }
        }

        var documentationUrl = map.TryGet("documentationUrl", out var docNode)
            ? ParseUriValue(docNode, issues, "'metadata.documentationUrl'")
            : null;

        IconRef? icon = null;
        if (map.TryGet("icon", out var iconNode))
        {
            var iconMap = AsMapping(iconNode, issues, "'metadata.icon'");
            if (iconMap is not null)
            {
                if (iconMap.TryGet("bundleFile", out var bundleNode))
                {
                    var path = AsString(bundleNode, issues, "'metadata.icon.bundleFile'");
                    icon = path is null ? null : new IconRef.BundleFile(path);
                }
                else if (iconMap.TryGet("remote", out var remoteNode))
                {
                    var url = ParseUriValue(remoteNode, issues, "'metadata.icon.remote'");
                    icon = url is null ? null : new IconRef.Remote(url);
                }
                else
                {
                    issues.Error("'metadata.icon' must declare either 'bundleFile' or 'remote'.", iconMap);
                }
            }
        }

        if (id is null || name is null || version is null)
        {
            return null;
        }

        return new GameMetadata(id, name, version, license, tags, summary, description, vendor, documentationUrl, icon, accentColor);
    }

    private static Uri? ParseUri(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            return null;
        }

        return ParseUriValue(node, issues, $"{context}'s '{key}'");
    }

    private static Uri? ParseUriValue(YamlNode node, ParseIssues issues, string context)
    {
        var raw = AsString(node, issues, context);
        if (raw is null)
        {
            return null;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        issues.Error($"{context} value '{raw}' is not a valid absolute URL.", node);
        return null;
    }
}
