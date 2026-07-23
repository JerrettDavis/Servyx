using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace Servyx.Web.Definitions;

/// <summary>Game metadata and adoption criteria parsed from the bundled <c>palworld-docker.yaml</c> definition.</summary>
public sealed record PalworldDefinitionInfo(
    string GameId,
    string GameName,
    string Version,
    IReadOnlyList<string> Tags,
    string ImageRepository,
    string RequiredMountContainerPath,
    string DefaultImage);

/// <summary>
/// Loads <c>definitions/palworld-docker.yaml</c> for its <c>metadata</c> and first <c>deployments</c>
/// entry's <c>detect</c>/<c>image</c> blocks only.
/// </summary>
/// <remarks>
/// This is deliberately not a full <c>IGameDefinitionProvider</c>/schema-validated parse — there is no
/// line/column validation, no signature or trust-tier evaluation, and the <c>settings</c>, <c>control</c>,
/// <c>backup</c>, and <c>saves</c> blocks are not parsed at all (M1's <c>ServerQueryService</c> reads a
/// small hardcoded allowlist of setting keys directly instead — see its <c>KnownSettings</c> table).
/// Wiring the full definition schema described in <c>docs/abstractions.md</c>
/// (<c>IGameDefinitionProvider</c>, <c>IDefinitionTrustEvaluator</c>) is out of scope for this milestone
/// and is called out here rather than approximated.
/// </remarks>
public static class PalworldDefinitionLoader
{
    private const string RelativePath = "definitions/palworld-docker.yaml";

    /// <summary>
    /// Attempts to load and parse the bundled definition from <paramref name="baseDirectory"/> (typically
    /// <see cref="AppContext.BaseDirectory"/>). Returns <see langword="null"/> — logging a warning rather
    /// than throwing — if the file is missing or does not parse into the expected shape, so a malformed
    /// or absent bundled definition degrades to the hardcoded <c>AdoptionCriteria.PalworldDefault</c>
    /// rather than crashing application startup.
    /// </summary>
    public static PalworldDefinitionInfo? TryLoad(string baseDirectory, ILogger? logger = null)
    {
        var path = Path.Combine(baseDirectory, RelativePath);

        try
        {
            if (!File.Exists(path))
            {
                logger?.LogWarning("Bundled game definition not found at '{Path}'; falling back to built-in adoption criteria.", path);
                return null;
            }

            var yaml = File.ReadAllText(path);
            return Parse(yaml);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse bundled game definition at '{Path}'; falling back to built-in adoption criteria.", path);
            return null;
        }
    }

    /// <summary>Parses the <c>metadata</c> and first <c>deployments</c> entry's <c>detect</c>/<c>image</c> blocks. Exposed internally for direct unit testing.</summary>
    internal static PalworldDefinitionInfo Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var root = deserializer.Deserialize<Dictionary<object, object>>(yaml);

        var metadata = AsMap(root["metadata"]);
        var deployments = (List<object>)root["deployments"];
        var firstDeployment = AsMap(deployments[0]);
        var detect = AsMap(firstDeployment["detect"]);
        var image = AsMap(firstDeployment["image"]);
        var requiredMounts = (List<object>)detect["requiredMounts"];
        var firstMount = AsMap(requiredMounts[0]);

        var tags = metadata.TryGetValue("tags", out var rawTags) && rawTags is List<object> tagList
            ? tagList.Select(t => t.ToString() ?? string.Empty).ToList()
            : [];

        return new PalworldDefinitionInfo(
            GameId: (string)metadata["id"],
            GameName: (string)metadata["name"],
            Version: metadata["version"].ToString() ?? "0.0.0",
            Tags: tags,
            ImageRepository: (string)detect["imageRepo"],
            RequiredMountContainerPath: (string)firstMount["containerPath"],
            DefaultImage: (string)image["default"]);
    }

    private static Dictionary<object, object> AsMap(object value) => (Dictionary<object, object>)value;
}
