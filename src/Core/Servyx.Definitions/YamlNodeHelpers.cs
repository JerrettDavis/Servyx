using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// Small extensions over YamlDotNet's representation model used throughout the block parsers. Kept
/// separate from <c>GameDefinitionYamlParser.Support.cs</c> because these are pure node-shape helpers with
/// no <see cref="ParseIssues"/>/<see cref="ParseState"/> involvement, unlike everything in that file.
/// </summary>
internal static class YamlNodeHelpers
{
    /// <summary>Looks up a mapping key by its scalar value, without relying on YamlDotNet's key-node equality/hashing.</summary>
    public static bool TryGet(this YamlMappingNode map, string key, out YamlNode value)
    {
        foreach (var pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Every top-level scalar key name declared on this mapping, in source order.</summary>
    public static IEnumerable<string> KeyNames(this YamlMappingNode map) =>
        map.Children.Keys.OfType<YamlScalarNode>().Select(s => s.Value ?? string.Empty);

    /// <summary>The <see cref="YamlNode"/> of a mapping key, for reporting an issue against the key itself rather than its value.</summary>
    public static YamlNode? KeyNode(this YamlMappingNode map, string key) =>
        map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(s => string.Equals(s.Value, key, StringComparison.Ordinal));
}
