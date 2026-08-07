using System.Text.RegularExpressions;
using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> DeploymentKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "kind", "detect", "image", "dataDir", "stopTimeout", "stopGracePeriodSeconds", "config", "install", "executable", "files",
        };

    private static readonly IReadOnlySet<string> DeployedFileKeys =
        new HashSet<string>(StringComparer.Ordinal) { "path", "mode", "createOnly", "contentFrom", "content" };

    private static readonly IReadOnlySet<string> DetectKeys = new HashSet<string>(StringComparer.Ordinal) { "imageRepo", "requiredMounts" };
    private static readonly IReadOnlySet<string> RequiredMountKeys = new HashSet<string>(StringComparer.Ordinal) { "containerPath" };
    private static readonly IReadOnlySet<string> ImageKeys = new HashSet<string>(StringComparer.Ordinal) { "default" };
    private static readonly IReadOnlySet<string> ExecutableKeys = new HashSet<string>(StringComparer.Ordinal) { "linux", "windows" };
    private static readonly IReadOnlySet<string> ConfigKeys = new HashSet<string>(StringComparer.Ordinal) { "surfaces", "ignored" };
    private static readonly IReadOnlySet<string> IgnoredKeys = new HashSet<string>(StringComparer.Ordinal) { "path", "reason" };
    private static readonly IReadOnlySet<string> SurfaceKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "role", "format", "codec", "codecPath", "locator", "managedSubtree", "mergePolicy", "derivedFrom", "regeneration",
    };
    private static readonly IReadOnlySet<string> LocatorKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "path", "channel", "query" };
    private static readonly IReadOnlySet<string> RegenerationKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "description" };
    private static readonly IReadOnlySet<string> InstallStepKeys = new HashSet<string>(StringComparer.Ordinal) { "verb", "appId", "validate", "path" };

    private static IReadOnlyList<DeploymentProfile>? ParseDeployments(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var sequence = RequireSequence(root, "deployments", issues, "The definition");
        if (sequence is null)
        {
            return null;
        }

        if (sequence.Children.Count == 0)
        {
            issues.Error("'deployments' declares no entries; at least one deployment profile is required.", sequence);
            return null;
        }

        var result = new List<DeploymentProfile>();
        foreach (var entryNode in sequence.Children)
        {
            var profile = ParseDeploymentProfile(entryNode, issues, state);
            if (profile is not null)
            {
                result.Add(profile);
            }
        }

        return result;
    }

    private static DeploymentProfile? ParseDeploymentProfile(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'deployments'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, DeploymentKeys, issues, "A 'deployments' entry");

        var id = RequireString(map, "id", issues, "A 'deployments' entry");
        var kind = ParseDeploymentKind(map, issues);

        DetectRule? detect = null;
        if (map.TryGet("detect", out var detectNode))
        {
            var detectMap = AsMapping(detectNode, issues, "A 'deployments' entry's 'detect'");
            if (detectMap is not null)
            {
                RejectUnknownKeys(detectMap, DetectKeys, issues, "A 'deployments' entry's 'detect'");
                var imageRepo = OptionalString(detectMap, "imageRepo", issues, "A 'deployments' entry's 'detect'");
                var mounts = new List<RequiredMount>();
                foreach (var mountNode in OptionalSequence(detectMap, "requiredMounts", issues, "A 'deployments' entry's 'detect'"))
                {
                    var mountMap = AsMapping(mountNode, issues, "An entry of 'detect.requiredMounts'");
                    if (mountMap is null)
                    {
                        continue;
                    }

                    RejectUnknownKeys(mountMap, RequiredMountKeys, issues, "A 'detect.requiredMounts' entry");
                    var containerPath = RequireString(mountMap, "containerPath", issues, "A 'detect.requiredMounts' entry");
                    if (containerPath is not null)
                    {
                        mounts.Add(new RequiredMount(containerPath));
                    }
                }

                detect = new DetectRule(imageRepo, mounts);
            }
        }

        ImageSpec? image = null;
        if (map.TryGet("image", out var imageNode))
        {
            var imageMap = AsMapping(imageNode, issues, "A 'deployments' entry's 'image'");
            if (imageMap is not null)
            {
                RejectUnknownKeys(imageMap, ImageKeys, issues, "A 'deployments' entry's 'image'");
                var defaultImage = RequireString(imageMap, "default", issues, "A 'deployments' entry's 'image'");
                image = defaultImage is null ? null : new ImageSpec(defaultImage);
            }
        }

        var dataDir = OptionalString(map, "dataDir", issues, "A 'deployments' entry");
        var stopTimeout = OptionalDuration(map, "stopTimeout", issues, "A 'deployments' entry");
        var stopGracePeriod = ParseStopGracePeriod(map, issues);

        ExecutableSpec? executable = null;
        if (map.TryGet("executable", out var execNode))
        {
            var execMap = AsMapping(execNode, issues, "A 'deployments' entry's 'executable'");
            if (execMap is not null)
            {
                RejectUnknownKeys(execMap, ExecutableKeys, issues, "A 'deployments' entry's 'executable'");
                var linux = OptionalString(execMap, "linux", issues, "A 'deployments' entry's 'executable'");
                var windows = OptionalString(execMap, "windows", issues, "A 'deployments' entry's 'executable'");
                executable = new ExecutableSpec(linux, windows);
            }
        }

        var install = new List<InstallStep>();
        foreach (var stepNode in OptionalSequence(map, "install", issues, "A 'deployments' entry"))
        {
            var step = ParseInstallStep(stepNode, issues, state);
            if (step is not null)
            {
                install.Add(step);
            }
        }

        var files = new List<DeployedFile>();
        foreach (var fileNode in OptionalSequence(map, "files", issues, "A 'deployments' entry"))
        {
            var file = ParseDeployedFile(fileNode, issues, state);
            if (file is not null)
            {
                files.Add(file);
            }
        }

        var surfaces = new List<DeclaredConfigSurface>();
        var ignored = new List<IgnoredPath>();
        if (map.TryGet("config", out var configNode))
        {
            var configMap = AsMapping(configNode, issues, "A 'deployments' entry's 'config'");
            if (configMap is not null)
            {
                RejectUnknownKeys(configMap, ConfigKeys, issues, "A 'deployments' entry's 'config'");

                foreach (var surfaceNode in OptionalSequence(configMap, "surfaces", issues, "A 'deployments' entry's 'config'"))
                {
                    var surface = ParseSurface(surfaceNode, issues, state);
                    if (surface is not null)
                    {
                        surfaces.Add(surface);
                    }
                }

                foreach (var ignoredNode in OptionalSequence(configMap, "ignored", issues, "A 'deployments' entry's 'config'"))
                {
                    var ignoredMap = AsMapping(ignoredNode, issues, "An entry of 'config.ignored'");
                    if (ignoredMap is null)
                    {
                        continue;
                    }

                    RejectUnknownKeys(ignoredMap, IgnoredKeys, issues, "A 'config.ignored' entry");
                    var path = RequireString(ignoredMap, "path", issues, "A 'config.ignored' entry");
                    var reason = RequireString(ignoredMap, "reason", issues, "A 'config.ignored' entry");

                    if (path is not null)
                    {
                        ignoredMap.TryGet("path", out var pathNode);
                        ValidateContainedPath(path, pathNode, issues, "A 'config.ignored' entry's 'path'");
                        QueueTemplateTokens(path, pathNode, state);
                    }

                    if (path is not null && reason is not null)
                    {
                        ignored.Add(new IgnoredPath(path, reason));
                    }
                }
            }
        }

        if (id is null || kind is null)
        {
            return null;
        }

        // Kind-specific required fields (docs/schema.md: "a deployment missing its kind-specific required
        // fields fails validation").
        if (kind == DeploymentKind.Docker && image is null)
        {
            issues.Error($"Deployment '{id}' has kind 'docker' but declares no 'image.default'.", map);
        }

        if (kind == DeploymentKind.Process && executable is { Linux: null, Windows: null })
        {
            issues.Error($"Deployment '{id}' has kind 'process' but declares no 'executable.linux' or 'executable.windows'.", map);
        }

        if (kind == DeploymentKind.Process && executable is null)
        {
            issues.Error($"Deployment '{id}' has kind 'process' but declares no 'executable'.", map);
        }

        foreach (var surface in surfaces)
        {
            if (!state.SurfacesById.TryGetValue(surface.Id, out var list))
            {
                list = [];
                state.SurfacesById[surface.Id] = list;
            }

            list.Add((id, surface.Format));
        }

        // Queued rather than checked here: the value it must be measured against is the total of the
        // 'lifecycle.stop' ladder's stage timeouts, and 'lifecycle' is parsed after 'deployments'.
        if (stopGracePeriod is { } grace)
        {
            map.TryGet("stopGracePeriodSeconds", out var graceNode);
            state.PendingStopGracePeriods.Add((id, grace, graceNode ?? map));
        }

        return new DeploymentProfile(id, kind.Value, detect, image, dataDir, stopTimeout, stopGracePeriod, surfaces, ignored, install, executable, files);
    }

    /// <summary>The only accepted shape of a <c>files[].mode</c> value: a leading zero and three octal digits.</summary>
    private static readonly Regex DeployedFileModePattern = new(@"^0[0-7]{3}$", RegexOptions.Compiled);

    /// <summary>
    /// The mode a seeded file is created with when the definition declares none. Owner-read/write and
    /// nothing else, because the only reason this feature exists is to place a credential where an image
    /// expects to find one — see the remarks on <see cref="DeployedFile"/> — and a permissive default would
    /// hand that credential to every other process sharing the container's filesystem.
    /// </summary>
    private const string DefaultDeployedFileMode = "0600";

    /// <summary>
    /// Parses one entry of a deployment profile's optional <c>files</c> list. Every rule here is an Error
    /// rather than a Warning: a seeded file is a write into the deployment's own storage performed before
    /// the workload has ever run, so a definition that gets one wrong is not degraded, it is dangerous.
    /// </summary>
    private static DeployedFile? ParseDeployedFile(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'files'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, DeployedFileKeys, issues, "A 'files' entry");

        // RequireString already reports a missing or blank 'path' in the wording every other required
        // scalar in this parser uses, and hands back null — so the non-empty rule needs no restatement here.
        var path = RequireString(map, "path", issues, "A 'files' entry");
        if (path is not null)
        {
            map.TryGet("path", out var pathNode);
            ValidateSeededFilePath(path, pathNode ?? map, issues, "A 'files' entry's 'path'");

            // The same deferred closed-set check every other path-like field in this parser goes through
            // (see ResolveDeferredChecks): '${DATA_DIR}'/'${COMPOSE_DIR}'/'${INSTANCE_ID}' and any declared
            // settings key resolve, anything else is reported once 'settings' has been walked.
            QueueTemplateTokens(path, pathNode ?? map, state);
        }

        var mode = ParseDeployedFileMode(map, issues);
        var createOnly = OptionalBool(map, "createOnly", issues, "A 'files' entry") ?? true;
        var (contentFrom, content) = ParseDeployedFileContent(map, issues, state);

        return path is null ? null : new DeployedFile(path, mode, createOnly, contentFrom, content);
    }

    /// <summary>
    /// Applies the containment rule to a <c>files[].path</c>. This is the one path-like field in the schema
    /// that must be <em>rooted</em> at a declared root variable rather than merely "not escaping" one: every
    /// other path-like field describes something the definition only reads or lists, while this one names a
    /// destination Servyx will write bytes to, so "somewhere relative, we'll see where it lands" is not an
    /// acceptable answer.
    /// </summary>
    /// <remarks>
    /// The shared <see cref="ValidateContainedPath"/> runs first, so a plain <c>..</c> segment and an
    /// OS-absolute path are reported by exactly the same rule (and the same wording) that every other
    /// path-like field uses. Two checks are then layered on top, both specific to this field:
    /// <list type="number">
    /// <item>
    /// <strong>Encoded traversal.</strong> <see cref="ValidateContainedPath"/> splits on <c>/</c> and
    /// <c>\</c> and compares segments, so it sees through <c>foo/../bar</c> and <c>..\bar</c> but not
    /// <c>..%2fbar</c> or <c>%2e%2e/bar</c> — neither of those produces a segment that <em>equals</em>
    /// <c>..</c>. A definition is untrusted content and this field is a write destination, so any <c>..</c>
    /// surviving a single percent-decode is refused outright rather than reasoned about.
    /// </item>
    /// <item>
    /// <strong>Rooting.</strong> The path must begin with <c>${DATA_DIR}</c> or <c>${COMPOSE_DIR}</c>
    /// specifically — not <c>${INSTANCE_ID}</c>, not a settings key, and not a bare relative path.
    /// </item>
    /// </list>
    /// </remarks>
    private static void ValidateSeededFilePath(string path, YamlNode node, ParseIssues issues, string context)
    {
        ValidateContainedPath(path, node, issues, context);

        var hasPlainTraversalSegment = path
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");

        if (!hasPlainTraversalSegment && ContainsEncodedTraversal(path))
        {
            issues.Error(
                $"{context} value '{path}' contains an encoded '..' path-traversal segment, which is never allowed.",
                node);
        }

        if (!IsRootedAtDeploymentRootVariable(path))
        {
            issues.Error(
                $"{context} value '{path}' is not rooted at '${{DATA_DIR}}' or '${{COMPOSE_DIR}}'. A seeded file is "
                + "written into the deployment's own storage, so it must name a destination inside one of those two "
                + "roots rather than an absolute or otherwise-rooted path.",
                node);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> hides a <c>..</c> behind percent-encoding (<c>..%2f</c>,
    /// <c>%2e%2e/</c>, <c>%2e%2e%2f</c>). One decode pass is enough: a value needing two is, by that fact
    /// alone, not a path any definition legitimately declares, and the raw-form check below still catches
    /// the literal <c>..</c> its outer layer contains.
    /// </summary>
    private static bool ContainsEncodedTraversal(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return true;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            // Malformed percent-encoding in a write destination: treat as hostile rather than as harmless.
            return true;
        }

        return decoded.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="path"/> begins with a <c>${DATA_DIR}</c> or <c>${COMPOSE_DIR}</c> token.
    /// Case-insensitive for the same reason <see cref="ValidateContainedPath"/> is — see its remarks.
    /// </summary>
    private static bool IsRootedAtDeploymentRootVariable(string path) =>
        TemplateTokenPattern.Match(path) is { Success: true, Index: 0 } match
        && (string.Equals(match.Groups[1].Value, "DATA_DIR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(match.Groups[1].Value, "COMPOSE_DIR", StringComparison.OrdinalIgnoreCase));

    private static string ParseDeployedFileMode(YamlMappingNode map, ParseIssues issues)
    {
        var mode = OptionalString(map, "mode", issues, "A 'files' entry");
        if (mode is null)
        {
            return DefaultDeployedFileMode;
        }

        if (DeployedFileModePattern.IsMatch(mode))
        {
            return mode;
        }

        map.TryGet("mode", out var modeNode);
        issues.Error(
            $"A 'files' entry declares 'mode: {mode}'; it must be a four-character octal string such as "
            + $"'{DefaultDeployedFileMode}' (a leading '0' followed by three digits in the range 0-7). Omit the "
            + $"field to accept the default of '{DefaultDeployedFileMode}'.",
            modeNode ?? map);

        return DefaultDeployedFileMode;
    }

    /// <summary>
    /// Reads a <c>files</c> entry's mutually-exclusive <c>contentFrom</c>/<c>content</c> pair. The
    /// <c>contentFrom</c> branch delegates to <see cref="ParseSecretRefValue"/> — the same method a control
    /// channel's <c>passwordRef</c> and <c>auth.passwordRef</c> go through — so the accepted scheme set and
    /// the "must be shaped 'scheme:key'" wording are literally the same rule, not a second one that agrees
    /// with it today. Whether the referenced key is declared is deferred, because <c>settings</c> is parsed
    /// after <c>deployments</c>.
    /// </summary>
    private static (string? ContentFrom, string? Content) ParseDeployedFileContent(
        YamlMappingNode map, ParseIssues issues, ParseState state)
    {
        var hasContentFrom = map.TryGet("contentFrom", out var contentFromNode);
        var hasContent = map.TryGet("content", out var contentNode);

        if (hasContentFrom && hasContent)
        {
            issues.Error(
                "A 'files' entry declares both 'content' and 'contentFrom'; exactly one is required. Inline "
                + "'content' is checked-in definition text, while 'contentFrom' resolves from the secret store at "
                + "the point of use — declaring both leaves which one wins to the reader's guess.",
                contentFromNode ?? map);
        }
        else if (!hasContentFrom && !hasContent)
        {
            issues.Error(
                "A 'files' entry declares neither 'content' nor 'contentFrom'; exactly one is required. A seeded "
                + "file with no declared content would place an empty file where the workload expects real content.",
                map);
        }

        string? contentFrom = null;
        if (hasContentFrom && contentFromNode is not null)
        {
            var secretRef = ParseSecretRefValue(contentFromNode, issues, "A 'files' entry's 'contentFrom'");
            if (secretRef is not null)
            {
                state.PendingSecretKeyRefs.Add((secretRef.Key, contentFromNode, "A 'files' entry's 'contentFrom'"));
                contentFrom = $"{secretRef.Scheme}:{secretRef.Key}";
            }
        }

        var content = hasContent && contentNode is not null
            ? AsString(contentNode, issues, "A 'files' entry's 'content'")
            : null;

        return (contentFrom, content);
    }

    /// <summary>
    /// Parses the optional <c>stopGracePeriodSeconds</c> field: whole seconds, not a duration string — see
    /// the remarks on <see cref="DeploymentProfile.StopGracePeriod"/>. Zero or negative is rejected outright:
    /// a grace period of zero is indistinguishable from an immediate kill, which is what the field exists to
    /// prevent, and is far more likely a typo than an intent.
    /// </summary>
    private static TimeSpan? ParseStopGracePeriod(YamlMappingNode map, ParseIssues issues)
    {
        var seconds = OptionalInt(map, "stopGracePeriodSeconds", issues, "A 'deployments' entry");
        if (seconds is not { } value)
        {
            return null;
        }

        if (value <= 0)
        {
            map.TryGet("stopGracePeriodSeconds", out var node);
            issues.Error(
                $"A 'deployments' entry declares 'stopGracePeriodSeconds: {value}'; it must be a positive whole "
                + "number of seconds. Omit the field entirely to accept the container runtime's own default.",
                node ?? map);
            return null;
        }

        return TimeSpan.FromSeconds(value);
    }

    private static DeploymentKind? ParseDeploymentKind(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "kind", issues, "A 'deployments' entry");
        if (raw is null)
        {
            return null;
        }

        return raw switch
        {
            "docker" => DeploymentKind.Docker,
            "process" => DeploymentKind.Process,
            _ => Fail(map, issues, raw),
        };

        static DeploymentKind? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("kind", out var node);
            issues.Error($"A 'deployments' entry declares 'kind: {raw}'; only 'docker' and 'process' are recognized.", node);
            return null;
        }
    }

    private static InstallStep? ParseInstallStep(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'install'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, InstallStepKeys, issues, "An 'install' entry");
        var verb = RequireString(map, "verb", issues, "An 'install' entry");
        if (verb is null)
        {
            return null;
        }

        switch (verb)
        {
            case "steamcmd":
                var appId = OptionalInt(map, "appId", issues, "An 'install' steamcmd entry") ?? 0;
                if (!map.TryGet("appId", out _))
                {
                    issues.Error("An 'install' steamcmd entry declares no 'appId'.", map);
                }

                var validate = OptionalBool(map, "validate", issues, "An 'install' steamcmd entry") ?? false;
                return new InstallStep.SteamCmd(appId, validate);

            case "ensure-dir":
                var path = RequireString(map, "path", issues, "An 'install' ensure-dir entry");
                if (path is not null)
                {
                    map.TryGet("path", out var pathNode);
                    ValidateContainedPath(path, pathNode, issues, "An 'install' ensure-dir entry's 'path'");
                    QueueTemplateTokens(path, pathNode, state);
                }

                return path is null ? null : new InstallStep.EnsureDir(path);

            default:
                map.TryGet("verb", out var verbNode);
                issues.Error($"An 'install' entry declares 'verb: {verb}'; only 'steamcmd' and 'ensure-dir' are recognized.", verbNode);
                return null;
        }
    }

    private static DeclaredConfigSurface? ParseSurface(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'config.surfaces'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, SurfaceKeys, issues, "A 'config.surfaces' entry");

        var id = RequireString(map, "id", issues, "A 'config.surfaces' entry");
        var role = ParseSurfaceRole(map, issues);
        var format = ParseSurfaceFormat(map, issues);
        var codec = OptionalString(map, "codec", issues, "A 'config.surfaces' entry");
        var codecPath = OptionalString(map, "codecPath", issues, "A 'config.surfaces' entry");
        var managedSubtree = OptionalString(map, "managedSubtree", issues, "A 'config.surfaces' entry");
        var mergePolicy = ParseMergePolicy(map, issues);
        var derivedFrom = OptionalStringList(map, "derivedFrom", issues, "A 'config.surfaces' entry");

        SurfaceLocator? locator = null;
        if (map.TryGet("locator", out var locatorNode))
        {
            var locatorMap = AsMapping(locatorNode, issues, "A 'config.surfaces' entry's 'locator'");
            if (locatorMap is not null)
            {
                RejectUnknownKeys(locatorMap, LocatorKeys, issues, "A 'config.surfaces' entry's 'locator'");
                var locatorKind = RequireString(locatorMap, "kind", issues, "A 'config.surfaces' entry's 'locator'");
                locator = locatorKind switch
                {
                    "host-file" => ParseHostFileLocator(locatorMap, issues, state),
                    "control-channel" => ParseControlChannelLocator(locatorMap, issues, state),
                    null => null,
                    _ => FailLocatorKind(locatorMap, issues, locatorKind),
                };
            }
        }

        RegenerationTrigger? regeneration = null;
        if (map.TryGet("regeneration", out var regenNode))
        {
            var regenMap = AsMapping(regenNode, issues, "A 'config.surfaces' entry's 'regeneration'");
            if (regenMap is not null)
            {
                RejectUnknownKeys(regenMap, RegenerationKeys, issues, "A 'config.surfaces' entry's 'regeneration'");
                var regenKind = ParseRegenerationKind(regenMap, issues);
                var description = RequireString(regenMap, "description", issues, "A 'config.surfaces' entry's 'regeneration'");
                regeneration = regenKind is not null && description is not null
                    ? new RegenerationTrigger(regenKind.Value, description)
                    : null;
            }
        }

        if (id is null || role is null || format is null || locator is null)
        {
            return null;
        }

        return new DeclaredConfigSurface(id, role.Value, format.Value, codec, codecPath, locator, managedSubtree, mergePolicy, derivedFrom, regeneration);
    }

    private static SurfaceLocator? ParseHostFileLocator(YamlMappingNode locatorMap, ParseIssues issues, ParseState state)
    {
        var path = RequireString(locatorMap, "path", issues, "A 'host-file' locator");
        if (path is null)
        {
            return null;
        }

        locatorMap.TryGet("path", out var pathNode);
        ValidateContainedPath(path, pathNode, issues, "A 'host-file' locator's 'path'");
        QueueTemplateTokens(path, pathNode, state);
        return new SurfaceLocator.HostFile(path);
    }

    private static SurfaceLocator? ParseControlChannelLocator(YamlMappingNode locatorMap, ParseIssues issues, ParseState state)
    {
        var channel = RequireString(locatorMap, "channel", issues, "A 'control-channel' locator");
        var query = RequireString(locatorMap, "query", issues, "A 'control-channel' locator");

        // 'query' is deliberately NOT cross-validated here: for the one real usage in
        // definitions/palworld-docker.yaml it is a raw REST path ("/v1/api/settings"), not an id that could
        // be looked up in a channel's 'commands'/'endpoints' catalogue the way backup.quiesce's 'command' or
        // control.players.preferred's dotted operation can be — see this phase's final report.
        if (channel is not null)
        {
            locatorMap.TryGet("channel", out var channelNode);
            state.PendingSurfaceChannelRefs.Add((channel, channelNode));
        }

        return channel is not null && query is not null ? new SurfaceLocator.ControlChannel(channel, query) : null;
    }

    private static SurfaceLocator? FailLocatorKind(YamlMappingNode locatorMap, ParseIssues issues, string kind)
    {
        locatorMap.TryGet("kind", out var node);
        issues.Error($"A 'config.surfaces' entry's locator declares 'kind: {kind}'; only 'host-file' and 'control-channel' are recognized.", node);
        return null;
    }

    private static SurfaceRole? ParseSurfaceRole(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "role", issues, "A 'config.surfaces' entry");
        return raw switch
        {
            "authoritative" => SurfaceRole.Authoritative,
            "derived" => SurfaceRole.Derived,
            "runtime" => SurfaceRole.Runtime,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static SurfaceRole? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("role", out var node);
            issues.Error($"A 'config.surfaces' entry declares 'role: {raw}'; only 'authoritative', 'derived', and 'runtime' are recognized.", node);
            return null;
        }
    }

    private static SurfaceFormat? ParseSurfaceFormat(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "format", issues, "A 'config.surfaces' entry");
        return raw switch
        {
            "dotenv" => SurfaceFormat.Dotenv,
            "yaml" => SurfaceFormat.Yaml,
            "ini" => SurfaceFormat.Ini,
            "json" => SurfaceFormat.Json,
            "properties" => SurfaceFormat.Properties,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static SurfaceFormat? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("format", out var node);
            issues.Error($"A 'config.surfaces' entry declares 'format: {raw}'; only 'dotenv', 'yaml', 'ini', 'json', and 'properties' are recognized.", node);
            return null;
        }
    }

    private static MergePolicy ParseMergePolicy(YamlMappingNode map, ParseIssues issues)
    {
        if (!map.TryGet("mergePolicy", out var node))
        {
            return MergePolicy.PreserveUnknown;
        }

        var raw = AsString(node, issues, "A 'config.surfaces' entry's 'mergePolicy'");
        return raw switch
        {
            "preserve-unknown" => MergePolicy.PreserveUnknown,
            "managed-block" => MergePolicy.ManagedBlock,
            _ => Fail(node, issues, raw),
        };

        static MergePolicy Fail(YamlNode node, ParseIssues issues, string? raw)
        {
            issues.Error($"A 'config.surfaces' entry declares 'mergePolicy: {raw}'; only 'preserve-unknown' and 'managed-block' are recognized.", node);
            return MergePolicy.PreserveUnknown;
        }
    }

    private static RegenerationKind? ParseRegenerationKind(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "kind", issues, "A 'regeneration' block");
        return raw switch
        {
            "container-restart" => RegenerationKind.ContainerRestart,
            "process-restart" => RegenerationKind.ProcessRestart,
            "manual" => RegenerationKind.Manual,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static RegenerationKind? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("kind", out var node);
            issues.Error($"A 'regeneration' block declares 'kind: {raw}'; only 'container-restart', 'process-restart', and 'manual' are recognized.", node);
            return null;
        }
    }
}
