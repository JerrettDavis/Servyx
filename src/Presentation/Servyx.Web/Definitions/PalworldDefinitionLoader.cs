using Microsoft.Extensions.Logging;
using Servyx.Infrastructure.Rcon;
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

    /// <summary>
    /// Attempts to load the <c>rcon</c> control channel's <c>commands</c> catalogue from the bundled
    /// definition. Returns <see langword="null"/> — logging a warning rather than throwing — if the file is
    /// missing or the block is absent or malformed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="TryLoad"/> on purpose. Adoption criteria must survive a definition that has
    /// no control block at all, and a control channel must not be composed from a definition whose command
    /// catalogue did not parse — folding both into one result would force one of those two to be wrong.
    /// </para>
    /// <para>
    /// There is deliberately no hardcoded fallback catalogue. Every command id that reaches the wire has to
    /// carry the definition's own <c>readOnly</c> classification, and a fallback invented in C# would be a
    /// second, unreviewed source of truth for exactly the flag the write guard gates on. A definition that
    /// does not parse yields no RCON channel, which is a visible absence rather than a silent substitution.
    /// </para>
    /// </remarks>
    /// <param name="baseDirectory">Where the bundled definition lives, typically <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="logger">Optional logger for the degraded path.</param>
    public static IReadOnlyList<RconCommand>? TryLoadRconCommands(string baseDirectory, ILogger? logger = null)
    {
        var path = Path.Combine(baseDirectory, RelativePath);

        try
        {
            if (!File.Exists(path))
            {
                logger?.LogWarning(
                    "Bundled game definition not found at '{Path}'; no RCON control-command catalogue is available.",
                    path);
                return null;
            }

            return ParseRconCommands(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Failed to parse the 'control.channels[rcon].commands' block of the bundled game definition at "
                + "'{Path}'; no RCON control-command catalogue is available.",
                path);
            return null;
        }
    }

    /// <summary>
    /// Parses <c>control.channels[]</c>, selects the entry whose <c>id</c> is <c>rcon</c>, and reads its
    /// <c>commands</c> map. Exposed internally for direct unit testing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No control channel has <c>id: rcon</c>, or it declares no commands. Selected by id rather than by
    /// list position for the same reason the docker deployment profile is: a reordered definition must fail
    /// with a named, diagnosable exception rather than silently bind the REST channel's endpoints as if
    /// they were RCON commands.
    /// </exception>
    internal static IReadOnlyList<RconCommand> ParseRconCommands(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var root = deserializer.Deserialize<Dictionary<object, object>>(yaml);

        var control = AsMap(root["control"]);
        var channels = (List<object>)control["channels"];

        var rcon = channels
            .Select(AsMap)
            .FirstOrDefault(c => c.TryGetValue("id", out var id) && string.Equals(id as string, "rcon", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "No entry in the bundled game definition's 'control.channels' list has 'id: rcon'; cannot resolve "
                + "the RCON control-command catalogue.");

        if (!rcon.TryGetValue("commands", out var rawCommands) || rawCommands is not Dictionary<object, object> commands || commands.Count == 0)
        {
            throw new InvalidOperationException(
                "The bundled game definition's 'rcon' control channel declares no 'commands'; a catalogue with no "
                + "commands would refuse every invocation, including the backup quiesce step.");
        }

        var parsed = new List<RconCommand>(commands.Count);

        foreach (var (rawId, rawCommand) in commands)
        {
            var id = rawId as string
                ?? throw new InvalidOperationException("An RCON control command id is not a string.");
            var command = AsMap(rawCommand);

            var template = command.TryGetValue("template", out var rawTemplate) ? rawTemplate as string : null;
            if (string.IsNullOrWhiteSpace(template))
            {
                throw new InvalidOperationException($"RCON control command '{id}' declares no 'template'.");
            }

            // Absent means NOT read-only. The safe reading of a missing classification is the one that makes
            // the write guard refuse, never the one that lets an unclassified command through on a
            // read-only server.
            var readOnly = command.TryGetValue("readOnly", out var rawReadOnly)
                && bool.TryParse(rawReadOnly as string, out var flag)
                && flag;

            parsed.Add(new RconCommand(id, template, readOnly));
        }

        return parsed;
    }

    /// <summary>
    /// Parses the <c>metadata</c> block and the <c>docker</c>-kind entry of <c>deployments</c> for its
    /// <c>detect</c>/<c>image</c> blocks. Exposed internally for direct unit testing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No entry in <c>deployments</c> has <c>kind: docker</c>. Selecting by <c>kind</c> rather than by
    /// list position means a reordered or docker-profile-less definition fails with this named,
    /// diagnosable exception — which <see cref="TryLoad"/> logs at Warning before falling back to
    /// <c>AdoptionCriteria.PalworldDefault</c> — instead of an opaque <see cref="InvalidCastException"/>
    /// or <see cref="KeyNotFoundException"/> from indexing the wrong profile.
    /// </exception>
    internal static PalworldDefinitionInfo Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var root = deserializer.Deserialize<Dictionary<object, object>>(yaml);

        var metadata = AsMap(root["metadata"]);
        var deployments = (List<object>)root["deployments"];
        var dockerDeployment = deployments
            .Select(AsMap)
            .FirstOrDefault(d => d.TryGetValue("kind", out var kind) && string.Equals(kind as string, "docker", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "No entry in the bundled game definition's 'deployments' list has 'kind: docker'; cannot resolve adoption criteria.");
        var detect = AsMap(dockerDeployment["detect"]);
        var image = AsMap(dockerDeployment["image"]);
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
