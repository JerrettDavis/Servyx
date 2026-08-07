using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> ControlKeys = new HashSet<string>(StringComparer.Ordinal) { "channels", "players" };
    private static readonly IReadOnlySet<string> ChannelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "protocol", "port", "passwordRef", "auth", "enabledWhen", "reachability", "commands", "endpoints",
    };
    private static readonly IReadOnlySet<string> AuthKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "user", "passwordRef" };
    private static readonly IReadOnlySet<string> DockerExecToolKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "tool", "argv" };
    private static readonly IReadOnlySet<string> ReachabilityKindOnlyKeys = new HashSet<string>(StringComparer.Ordinal) { "kind" };
    private static readonly IReadOnlySet<string> CommandKeys = new HashSet<string>(StringComparer.Ordinal) { "template", "readOnly" };
    private static readonly IReadOnlySet<string> EndpointKeys = new HashSet<string>(StringComparer.Ordinal) { "method", "path", "readOnly" };
    private static readonly IReadOnlySet<string> PlayersKeys = new HashSet<string>(StringComparer.Ordinal) { "preferred", "pollInterval", "parsers" };
    private static readonly IReadOnlySet<string> CsvParserKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "columns", "nameColumn", "idColumn" };
    private static readonly IReadOnlySet<string> SummaryLineParserKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "pattern", "nameSeparator" };
    private static readonly IReadOnlySet<string> LinesParserKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "headerPattern", "entryPattern", "ignorePatterns" };
    private static readonly IReadOnlySet<string> CountParserKeys = new HashSet<string>(StringComparer.Ordinal) { "kind", "pattern", "jsonPointer" };

    /// <summary>The recognized <c>control.players.parsers.*.kind</c> discriminators, in the order they are listed back to an author.</summary>
    private const string RecognizedParserKinds = "'csv-with-header', 'summary-line', 'lines', 'count'";

    private static ControlPlane? ParseControl(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var map = RequireMapping(root, "control", issues, "The definition");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, ControlKeys, issues, "'control'");

        var channels = new List<ControlChannelDefinition>();
        foreach (var node in OptionalSequence(map, "channels", issues, "'control'"))
        {
            var channel = ParseControlChannel(node, issues, state);
            if (channel is not null)
            {
                channels.Add(channel);
                state.ChannelsById[channel.Id] = channel;
            }
        }

        PlayersConfig? players = null;
        if (map.TryGet("players", out var playersNode))
        {
            var playersMap = AsMapping(playersNode, issues, "'control.players'");
            if (playersMap is not null)
            {
                players = ParsePlayersConfig(playersMap, issues, state);
            }
        }

        return new ControlPlane(channels, players);
    }

    private static ControlChannelDefinition? ParseControlChannel(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'control.channels'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, ChannelKeys, issues, "A 'control.channels' entry");

        var id = RequireString(map, "id", issues, "A 'control.channels' entry");
        var protocol = RequireString(map, "protocol", issues, "A 'control.channels' entry");
        var port = ParsePortRef(map, "port", issues, state, allowHostVariable: false, "A 'control.channels' entry");

        SecretRef? passwordRef = null;
        if (map.TryGet("passwordRef", out var passwordRefNode))
        {
            passwordRef = ParseSecretRefValue(passwordRefNode, issues, "A 'control.channels' entry's 'passwordRef'");
        }

        AuthSpec? auth = null;
        if (map.TryGet("auth", out var authNode))
        {
            var authMap = AsMapping(authNode, issues, "A 'control.channels' entry's 'auth'");
            if (authMap is not null)
            {
                RejectUnknownKeys(authMap, AuthKeys, issues, "A 'control.channels' entry's 'auth'");
                var authKind = RequireString(authMap, "kind", issues, "A 'control.channels' entry's 'auth'");
                if (authKind is not null && authKind != "basic")
                {
                    authMap.TryGet("kind", out var authKindNode);
                    issues.Error($"A 'control.channels' entry's 'auth' declares 'kind: {authKind}'; only 'basic' is recognized.", authKindNode);
                }

                var user = RequireString(authMap, "user", issues, "A 'control.channels' entry's 'auth'");
                SecretRef? authPasswordRef = null;
                if (authMap.TryGet("passwordRef", out var authPasswordRefNode))
                {
                    authPasswordRef = ParseSecretRefValue(authPasswordRefNode, issues, "A 'control.channels' entry's 'auth.passwordRef'");
                }
                else
                {
                    issues.Error("A 'control.channels' entry's 'auth' declares no 'passwordRef'.", authMap);
                }

                auth = authKind == "basic" && user is not null && authPasswordRef is not null
                    ? new AuthSpec.Basic(user, authPasswordRef)
                    : null;
            }
        }

        EnabledWhenPredicate? enabledWhen = null;
        if (map.TryGet("enabledWhen", out var enabledWhenNode))
        {
            enabledWhen = ParseEnabledWhenValue(enabledWhenNode, issues, "A 'control.channels' entry's 'enabledWhen'");
        }

        var reachability = new List<ReachabilityStrategy>();
        foreach (var reachNode in OptionalSequence(map, "reachability", issues, "A 'control.channels' entry"))
        {
            var strategy = ParseReachabilityStrategy(reachNode, issues);
            if (strategy is not null)
            {
                reachability.Add(strategy);
            }
        }

        var commands = new Dictionary<string, ControlCommand>(StringComparer.Ordinal);
        if (map.TryGet("commands", out var commandsNode))
        {
            var commandsMap = AsMapping(commandsNode, issues, "A 'control.channels' entry's 'commands'");
            if (commandsMap is not null)
            {
                foreach (var pair in commandsMap.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } commandId })
                    {
                        issues.Error("A 'control.channels' entry's 'commands' declares a non-text key.", pair.Key);
                        continue;
                    }

                    var commandMap = AsMapping(pair.Value, issues, $"'commands.{commandId}'");
                    if (commandMap is null)
                    {
                        continue;
                    }

                    RejectUnknownKeys(commandMap, CommandKeys, issues, $"'commands.{commandId}'");
                    var template = RequireString(commandMap, "template", issues, $"'commands.{commandId}'");
                    var readOnly = RequireBool(commandMap, "readOnly", issues, $"'commands.{commandId}'");
                    if (template is not null)
                    {
                        commands[commandId] = new ControlCommand(template, readOnly);
                    }
                }
            }
        }

        var endpoints = new Dictionary<string, ControlEndpoint>(StringComparer.Ordinal);
        if (map.TryGet("endpoints", out var endpointsNode))
        {
            var endpointsMap = AsMapping(endpointsNode, issues, "A 'control.channels' entry's 'endpoints'");
            if (endpointsMap is not null)
            {
                foreach (var pair in endpointsMap.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } endpointId })
                    {
                        issues.Error("A 'control.channels' entry's 'endpoints' declares a non-text key.", pair.Key);
                        continue;
                    }

                    var endpointMap = AsMapping(pair.Value, issues, $"'endpoints.{endpointId}'");
                    if (endpointMap is null)
                    {
                        continue;
                    }

                    RejectUnknownKeys(endpointMap, EndpointKeys, issues, $"'endpoints.{endpointId}'");
                    var method = RequireString(endpointMap, "method", issues, $"'endpoints.{endpointId}'");
                    var path = RequireString(endpointMap, "path", issues, $"'endpoints.{endpointId}'");
                    var readOnly = RequireBool(endpointMap, "readOnly", issues, $"'endpoints.{endpointId}'");
                    if (method is not null && path is not null)
                    {
                        endpoints[endpointId] = new ControlEndpoint(method, path, readOnly);
                    }
                }
            }
        }

        if (id is null || protocol is null || port is null)
        {
            return null;
        }

        return new ControlChannelDefinition(id, protocol, port, passwordRef, auth, enabledWhen, reachability, commands, endpoints);
    }

    private static ReachabilityStrategy? ParseReachabilityStrategy(YamlNode node, ParseIssues issues)
    {
        var map = AsMapping(node, issues, "An entry of 'reachability'");
        if (map is null)
        {
            return null;
        }

        var kind = RequireString(map, "kind", issues, "A 'reachability' entry");
        switch (kind)
        {
            case "direct-tcp":
                RejectUnknownKeys(map, ReachabilityKindOnlyKeys, issues, "A 'reachability' direct-tcp entry");
                return new ReachabilityStrategy.DirectTcp();

            case "docker-exec-tool":
                RejectUnknownKeys(map, DockerExecToolKeys, issues, "A 'reachability' docker-exec-tool entry");
                var tool = RequireString(map, "tool", issues, "A 'reachability' docker-exec-tool entry");
                var argv = OptionalStringList(map, "argv", issues, "A 'reachability' docker-exec-tool entry");
                return tool is not null ? new ReachabilityStrategy.DockerExecTool(tool, argv) : null;

            case "docker-exec-network":
                RejectUnknownKeys(map, ReachabilityKindOnlyKeys, issues, "A 'reachability' docker-exec-network entry");
                return new ReachabilityStrategy.DockerExecNetwork();

            case "ssh-tunnel":
                RejectUnknownKeys(map, ReachabilityKindOnlyKeys, issues, "A 'reachability' ssh-tunnel entry");
                return new ReachabilityStrategy.SshTunnel();

            case null:
                return null;

            default:
                map.TryGet("kind", out var kindNode);
                issues.Error(
                    $"A 'reachability' entry declares 'kind: {kind}'; only 'direct-tcp', 'docker-exec-tool', "
                    + "'docker-exec-network', and 'ssh-tunnel' are recognized.",
                    kindNode);
                return null;
        }
    }

    private static PlayersConfig? ParsePlayersConfig(YamlMappingNode map, ParseIssues issues, ParseState state)
    {
        RejectUnknownKeys(map, PlayersKeys, issues, "'control.players'");

        var preferredSequence = RequireSequence(map, "preferred", issues, "'control.players'");
        var preferred = new List<string>();
        if (preferredSequence is not null)
        {
            foreach (var node in preferredSequence.Children)
            {
                var value = AsString(node, issues, "An entry of 'control.players.preferred'");
                if (value is null)
                {
                    continue;
                }

                preferred.Add(value);
                ValidatePreferredEntry(value, node, issues, state);
            }
        }

        var pollInterval = ParseDuration(map, "pollInterval", issues, "'control.players'");

        var parsers = new Dictionary<string, PlayerParserSpec>(StringComparer.Ordinal);
        if (map.TryGet("parsers", out var parsersNode))
        {
            var parsersMap = AsMapping(parsersNode, issues, "'control.players.parsers'");
            if (parsersMap is not null)
            {
                foreach (var pair in parsersMap.Children)
                {
                    if (pair.Key is not YamlScalarNode { Value: { } parserId })
                    {
                        issues.Error("'control.players.parsers' declares a non-text key.", pair.Key);
                        continue;
                    }

                    var parserMap = AsMapping(pair.Value, issues, $"'control.players.parsers.{parserId}'");
                    if (parserMap is null)
                    {
                        continue;
                    }

                    var parser = ParsePlayerParser(parserMap, parserId, issues);
                    if (parser is not null)
                    {
                        parsers[parserId] = parser;
                    }
                }
            }
        }

        return pollInterval is null ? null : new PlayersConfig(preferred, pollInterval.Value, parsers);
    }

    /// <summary>
    /// Parses one entry of <c>control.players.parsers</c> into a closed <see cref="PlayerParserSpec"/>.
    /// </summary>
    /// <remarks>
    /// The discriminator stays <c>kind</c>, spelled in the same kebab-case every other closed shape in this
    /// parser uses (<c>direct-tcp</c>, <c>docker-exec-tool</c>, <c>host-file</c>, <c>log-regex</c>), and the
    /// block stays nested under <c>control.players.parsers.&lt;channel&gt;.&lt;operation&gt;</c>. Both are
    /// deliberate: the one shape that already ships keeps parsing with a zero-line diff, and the three new
    /// shapes read like their siblings instead of introducing a second spelling convention into the same
    /// file. Only the set of recognized <c>kind</c> values grows.
    /// </remarks>
    private static PlayerParserSpec? ParsePlayerParser(YamlMappingNode map, string parserId, ParseIssues issues)
    {
        var context = $"'control.players.parsers.{parserId}'";
        var kind = RequireString(map, "kind", issues, context);

        switch (kind)
        {
            case "csv-with-header":
                return ParseCsvWithHeaderParser(map, context, issues);

            case "summary-line":
                return ParseSummaryLineParser(map, context, issues);

            case "lines":
                return ParseLinesParser(map, context, issues);

            case "count":
                return ParseCountParser(map, context, issues);

            case null:
                return null;

            default:
                map.TryGet("kind", out var kindNode);
                issues.Error($"{context} declares 'kind: {kind}'; only {RecognizedParserKinds} are recognized.", kindNode);
                return null;
        }
    }

    private static PlayerParserSpec? ParseCsvWithHeaderParser(YamlMappingNode map, string context, ParseIssues issues)
    {
        RejectUnknownKeys(map, CsvParserKeys, issues, context);

        var columns = OptionalStringList(map, "columns", issues, context);
        if (columns.Count == 0)
        {
            issues.Error($"{context} declares no 'columns', so no field of a reply line could be identified.", map);
            return null;
        }

        // Absent 'nameColumn' means the first declared column, which is what the shape that already ships
        // relies on. Naming a column that was never declared is an error rather than a silent fallback.
        var nameColumn = columns[0];
        if (map.TryGet("nameColumn", out var nameColumnNode))
        {
            var declared = AsString(nameColumnNode, issues, $"{context}'s 'nameColumn'");
            if (declared is null)
            {
                return null;
            }

            if (!columns.Contains(declared, StringComparer.Ordinal))
            {
                issues.Error(
                    $"{context} declares 'nameColumn: {declared}', which is not one of its declared columns "
                    + $"({string.Join(", ", columns)}).",
                    nameColumnNode);
                return null;
            }

            nameColumn = declared;
        }

        string? idColumn = null;
        if (map.TryGet("idColumn", out var idColumnNode))
        {
            idColumn = AsString(idColumnNode, issues, $"{context}'s 'idColumn'");
            if (idColumn is null)
            {
                return null;
            }

            if (!columns.Contains(idColumn, StringComparer.Ordinal))
            {
                issues.Error(
                    $"{context} declares 'idColumn: {idColumn}', which is not one of its declared columns "
                    + $"({string.Join(", ", columns)}).",
                    idColumnNode);
                return null;
            }
        }

        return new PlayerParserSpec.CsvWithHeader(columns, nameColumn, idColumn);
    }

    private static PlayerParserSpec? ParseSummaryLineParser(YamlMappingNode map, string context, ParseIssues issues)
    {
        RejectUnknownKeys(map, SummaryLineParserKeys, issues, context);

        var pattern = RequirePattern(map, "pattern", context, issues);
        if (pattern is null)
        {
            return null;
        }

        if (!RequireGroup(pattern, PlayerParserGroups.Count, map, "pattern", context, issues))
        {
            return null;
        }

        var separator = PlayerParserSpec.SummaryLine.DefaultNameSeparator;
        if (map.TryGet("nameSeparator", out var separatorNode))
        {
            var declared = AsString(separatorNode, issues, $"{context}'s 'nameSeparator'");
            if (declared is null or "")
            {
                issues.Error($"{context}'s 'nameSeparator' must not be empty.", separatorNode);
                return null;
            }

            separator = declared;
        }

        return new PlayerParserSpec.SummaryLine(pattern, separator);
    }

    private static PlayerParserSpec? ParseLinesParser(YamlMappingNode map, string context, ParseIssues issues)
    {
        RejectUnknownKeys(map, LinesParserKeys, issues, context);

        var entryPattern = RequirePattern(map, "entryPattern", context, issues);
        if (entryPattern is null)
        {
            return null;
        }

        if (!RequireGroup(entryPattern, PlayerParserGroups.Name, map, "entryPattern", context, issues))
        {
            return null;
        }

        CompiledPattern? headerPattern = null;
        if (map.TryGet("headerPattern", out _))
        {
            headerPattern = RequirePattern(map, "headerPattern", context, issues);
            if (headerPattern is null)
            {
                return null;
            }
        }

        var ignorePatterns = new List<CompiledPattern>();
        var failed = false;
        foreach (var node in OptionalSequence(map, "ignorePatterns", issues, context))
        {
            var source = AsString(node, issues, $"An entry of {context}'s 'ignorePatterns'");
            if (source is null)
            {
                failed = true;
                continue;
            }

            var compiled = CompiledPattern.TryCompile(source, out var error);
            if (compiled is null)
            {
                issues.Error(
                    $"An entry of {context}'s 'ignorePatterns' is not a valid non-backtracking regex: {error}",
                    node);
                failed = true;
                continue;
            }

            ignorePatterns.Add(compiled);
        }

        return failed ? null : new PlayerParserSpec.Lines(headerPattern, entryPattern, ignorePatterns);
    }

    private static PlayerParserSpec? ParseCountParser(YamlMappingNode map, string context, ParseIssues issues)
    {
        RejectUnknownKeys(map, CountParserKeys, issues, context);

        var hasPattern = map.TryGet("pattern", out _);
        var hasPointer = map.TryGet("jsonPointer", out var pointerNode);

        if (hasPattern == hasPointer)
        {
            issues.Error(
                $"{context} must declare exactly one of 'pattern' or 'jsonPointer'; it declares "
                + (hasPattern ? "both." : "neither."),
                map);
            return null;
        }

        if (hasPattern)
        {
            var pattern = RequirePattern(map, "pattern", context, issues);
            if (pattern is null || !RequireGroup(pattern, PlayerParserGroups.Count, map, "pattern", context, issues))
            {
                return null;
            }

            return new PlayerParserSpec.Count(pattern, null);
        }

        var pointer = AsString(pointerNode, issues, $"{context}'s 'jsonPointer'");
        if (pointer is null)
        {
            return null;
        }

        if (!pointer.StartsWith('/'))
        {
            issues.Error(
                $"{context} declares 'jsonPointer: {pointer}', which is not an RFC 6901 pointer — one must "
                + "start with '/'.",
                pointerNode);
            return null;
        }

        return new PlayerParserSpec.Count(null, pointer);
    }

    /// <summary>
    /// Reads a required regex-valued key and compiles it here, at definition-load time, so a malformed or
    /// backtracking-only pattern is a validation error against the file rather than an exception thrown at
    /// whatever polled a control channel later.
    /// </summary>
    private static CompiledPattern? RequirePattern(YamlMappingNode map, string key, string context, ParseIssues issues)
    {
        var source = RequireString(map, key, issues, context);
        if (source is null)
        {
            return null;
        }

        map.TryGet(key, out var node);
        var compiled = CompiledPattern.TryCompile(source, out var error);
        if (compiled is null)
        {
            issues.Error($"{context}'s '{key}' is not a valid non-backtracking regex: {error}", node);
        }

        return compiled;
    }

    private static bool RequireGroup(
        CompiledPattern pattern,
        string group,
        YamlMappingNode map,
        string key,
        string context,
        ParseIssues issues)
    {
        if (pattern.HasGroup(group))
        {
            return true;
        }

        map.TryGet(key, out var node);
        issues.Error(
            $"{context}'s '{key}' declares no '(?<{group}>...)' named group, which this parser shape requires.",
            node);
        return false;
    }

    /// <summary>
    /// Validates a <c>control.players.preferred</c> entry against the channels already parsed earlier in
    /// this same <c>control</c> block — no deferral needed, since <c>channels</c> always precedes
    /// <c>players</c> within the block itself regardless of top-level document order.
    /// </summary>
    private static void ValidatePreferredEntry(string entry, YamlNode node, ParseIssues issues, ParseState state)
    {
        var dot = entry.IndexOf('.');
        var channelId = dot < 0 ? entry : entry[..dot];

        if (!state.ChannelsById.TryGetValue(channelId, out var channel))
        {
            issues.Error($"'control.players.preferred' entry '{entry}' references channel '{channelId}', which is not declared.", node);
            return;
        }

        if (dot < 0)
        {
            return;
        }

        var operation = entry[(dot + 1)..];
        if (!channel.Commands.ContainsKey(operation) && !channel.Endpoints.ContainsKey(operation))
        {
            issues.Error(
                $"'control.players.preferred' entry '{entry}' references operation '{operation}' on channel "
                + $"'{channelId}', which declares no such command or endpoint.",
                node);
        }
    }
}
