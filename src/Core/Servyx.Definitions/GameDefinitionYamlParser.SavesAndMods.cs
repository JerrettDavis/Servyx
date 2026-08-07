using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> SavesKeys =
        new HashSet<string>(StringComparer.Ordinal) { "worldRoot", "worldIdPattern", "levelFile", "metaFile", "playerDir" };
    private static readonly IReadOnlySet<string> ModsKeys = new HashSet<string>(StringComparer.Ordinal) { "supported" };

    /// <summary>The <c>saves</c> block is optional — see the remarks on <see cref="SavesLayout"/> — so a definition that omits it entirely is not an error.</summary>
    private static SavesLayout? ParseSaves(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        if (!root.TryGet("saves", out var node))
        {
            return null;
        }

        var map = AsMapping(node, issues, "'saves'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, SavesKeys, issues, "'saves'");

        var worldRoot = RequireString(map, "worldRoot", issues, "'saves'");
        if (worldRoot is not null)
        {
            map.TryGet("worldRoot", out var worldRootNode);
            ValidateContainedPath(worldRoot, worldRootNode, issues, "'saves.worldRoot'");
            QueueTemplateTokens(worldRoot, worldRootNode, state);
        }

        var worldIdPattern = OptionalString(map, "worldIdPattern", issues, "'saves'");
        if (worldIdPattern is not null)
        {
            map.TryGet("worldIdPattern", out var patternNode);
            ValidateSafeRegex(worldIdPattern, patternNode, issues, "'saves.worldIdPattern'");
        }

        var levelFile = RequireString(map, "levelFile", issues, "'saves'");
        var metaFile = RequireString(map, "metaFile", issues, "'saves'");
        var playerDir = OptionalString(map, "playerDir", issues, "'saves'");

        return worldRoot is null || levelFile is null || metaFile is null
            ? null
            : new SavesLayout(worldRoot, worldIdPattern, levelFile, metaFile, playerDir);
    }

    private static ModsPolicy? ParseMods(YamlMappingNode root, ParseIssues issues)
    {
        var map = RequireMapping(root, "mods", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, ModsKeys, issues, "'mods'");
        var supported = RequireBool(map, "supported", issues, "'mods'");
        return new ModsPolicy(supported);
    }
}
