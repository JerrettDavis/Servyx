using System.Globalization;
using System.Text.RegularExpressions;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    // -- Node-shape extraction ------------------------------------------------------------------------------
    // Every helper below reports through ParseIssues rather than throwing: a malformed node is exactly the
    // kind of "content problem" this parser never throws for (see the class remarks).

    private static YamlMappingNode? AsMapping(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlMappingNode mapping)
        {
            return mapping;
        }

        issues.Error($"{context} must be a mapping.", node);
        return null;
    }

    private static YamlSequenceNode? AsSequence(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlSequenceNode sequence)
        {
            return sequence;
        }

        issues.Error($"{context} must be a list.", node);
        return null;
    }

    private static string? AsString(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        issues.Error($"{context} must be a text value.", node);
        return null;
    }

    private static bool? AsBool(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlScalarNode { Value: { } value } && bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        issues.Error($"{context} must be 'true' or 'false'.", node);
        return null;
    }

    private static int? AsInt(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlScalarNode { Value: { } value } && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        issues.Error($"{context} must be a whole number.", node);
        return null;
    }

    private static double? AsDouble(YamlNode node, ParseIssues issues, string context)
    {
        if (node is YamlScalarNode { Value: { } value } && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        issues.Error($"{context} must be a number.", node);
        return null;
    }

    private static YamlMappingNode? RequireMapping(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            issues.Error($"{context} declares no '{key}'.", parent);
            return null;
        }

        return AsMapping(node, issues, $"{context}'s '{key}'");
    }

    private static YamlMappingNode? OptionalMapping(YamlMappingNode parent, string key, ParseIssues issues, string context) =>
        parent.TryGet(key, out var node) ? AsMapping(node, issues, $"{context}'s '{key}'") : null;

    private static YamlSequenceNode? RequireSequence(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            issues.Error($"{context} declares no '{key}'.", parent);
            return null;
        }

        return AsSequence(node, issues, $"{context}'s '{key}'");
    }

    private static IReadOnlyList<YamlNode> OptionalSequence(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            return [];
        }

        var sequence = AsSequence(node, issues, $"{context}'s '{key}'");
        return sequence is null ? [] : [.. sequence.Children];
    }

    private static string? RequireString(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            issues.Error($"{context} declares no '{key}'.", parent);
            return null;
        }

        var value = AsString(node, issues, $"{context}'s '{key}'");
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Error($"{context}'s '{key}' must not be blank.", node);
            return null;
        }

        return value;
    }

    private static string? OptionalString(YamlMappingNode parent, string key, ParseIssues issues, string context) =>
        parent.TryGet(key, out var node) ? AsString(node, issues, $"{context}'s '{key}'") : null;

    private static bool RequireBool(YamlMappingNode parent, string key, ParseIssues issues, string context, bool defaultValue = false)
    {
        if (!parent.TryGet(key, out var node))
        {
            issues.Error($"{context} declares no '{key}'.", parent);
            return defaultValue;
        }

        return AsBool(node, issues, $"{context}'s '{key}'") ?? defaultValue;
    }

    private static bool? OptionalBool(YamlMappingNode parent, string key, ParseIssues issues, string context) =>
        parent.TryGet(key, out var node) ? AsBool(node, issues, $"{context}'s '{key}'") : null;

    private static int? OptionalInt(YamlMappingNode parent, string key, ParseIssues issues, string context) =>
        parent.TryGet(key, out var node) ? AsInt(node, issues, $"{context}'s '{key}'") : null;

    private static double? OptionalDouble(YamlMappingNode parent, string key, ParseIssues issues, string context) =>
        parent.TryGet(key, out var node) ? AsDouble(node, issues, $"{context}'s '{key}'") : null;

    private static IReadOnlyList<string> OptionalStringList(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        var items = OptionalSequence(parent, key, issues, context);
        var result = new List<string>(items.Count);
        foreach (var item in items)
        {
            var value = AsString(item, issues, $"An entry of {context}'s '{key}'");
            if (value is not null)
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>
    /// Flags every key on <paramref name="map"/> not present in <paramref name="known"/>. Per
    /// <c>docs/schema.md</c>'s "Unknown fields are rejected, not warned" validation rule, this is an
    /// <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/> — see the class remarks for why this
    /// project follows the doc over a looser Warning-based forward-compatibility policy.
    /// </summary>
    private static void RejectUnknownKeys(YamlMappingNode map, IReadOnlySet<string> known, ParseIssues issues, string context)
    {
        foreach (var key in map.KeyNames())
        {
            if (!known.Contains(key))
            {
                var keyNode = map.KeyNode(key) ?? map;
                issues.Error($"{context} declares an unrecognized field '{key}'.", keyNode);
            }
        }
    }

    // -- Durations --------------------------------------------------------------------------------------------

    private static readonly Regex DurationPattern = new(@"^(?<value>\d+)(?<unit>ms|s|m|h)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static TimeSpan? ParseDuration(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        var text = RequireString(parent, key, issues, context);
        if (text is null)
        {
            return null;
        }

        parent.TryGet(key, out var node);
        var match = DurationPattern.Match(text);
        if (!match.Success)
        {
            issues.Error(
                $"{context}'s '{key}' value '{text}' is not a recognized duration. Expected a whole number "
                + "followed by one of 'ms', 's', 'm', 'h' — e.g. '45s' or '10m'.",
                node);
            return null;
        }

        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => TimeSpan.Zero,
        };
    }

    private static TimeSpan? OptionalDuration(YamlMappingNode parent, string key, ParseIssues issues, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            return null;
        }

        var text = AsString(node, issues, $"{context}'s '{key}'");
        if (text is null)
        {
            return null;
        }

        var match = DurationPattern.Match(text);
        if (!match.Success)
        {
            issues.Error(
                $"{context}'s '{key}' value '{text}' is not a recognized duration. Expected a whole number "
                + "followed by one of 'ms', 's', 'm', 'h' — e.g. '45s' or '10m'.",
                node);
            return null;
        }

        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => TimeSpan.Zero,
        };
    }

    // -- Path traversal / containment (docs/schema.md "No absolute or traversal paths outside the server root") --

    private static readonly Regex DriveLetterPattern = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex TemplateTokenPattern = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Validates a path-like field against <c>docs/schema.md</c>'s path-containment rule: a <c>..</c>
    /// segment anywhere is rejected outright, and a path that is itself OS-absolute (rather than rooted at
    /// one of the definition's declared root variables — <c>${DATA_DIR}</c>, <c>${COMPOSE_DIR}</c>) is
    /// rejected as escaping the server root. This is a structural, symbolic check: the parser never has a
    /// real filesystem root to resolve against (that only exists once a deployment picks concrete values
    /// for its variables), so "stays within the root" is enforced on the declared template shape instead.
    /// </summary>
    private static void ValidateContainedPath(string value, YamlNode node, ParseIssues issues, string context)
    {
        var segments = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            issues.Error($"{context} value '{value}' contains a '..' path-traversal segment, which is never allowed.", node);
            return;
        }

        // Case-insensitive on purpose: ${data_dir}/${Data_Dir}/${DATA_DIR} are all accepted as rooting the
        // path, matching ParseState.HostVariables' own OrdinalIgnoreCase comparer. The alternative — staying
        // case-sensitive here while HostVariables is case-insensitive — would silently misclassify a
        // differently-cased reference as "escapes the server root" while the deferred variable check accepts
        // the very same token, a confusing split-brain result for a definition author to debug.
        var startsWithRootVariable = TemplateTokenPattern.Match(value) is { Success: true, Index: 0 } m
            && (string.Equals(m.Groups[1].Value, "DATA_DIR", StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Groups[1].Value, "COMPOSE_DIR", StringComparison.OrdinalIgnoreCase));

        if (!startsWithRootVariable && (value.StartsWith('/') || value.StartsWith('\\') || DriveLetterPattern.IsMatch(value)))
        {
            issues.Error(
                $"{context} value '{value}' is an absolute path not rooted at a declared '${{DATA_DIR}}' or "
                + "'${COMPOSE_DIR}' variable, so it would escape the server root.",
                node);
        }
    }

    // -- Variable references (${DATA_DIR}, ${COMPOSE_DIR}, ${INSTANCE_ID}, or a settings key) -----------------

    /// <summary>Queues every <c>${TOKEN}</c> reference in <paramref name="value"/> for the deferred closed-set check.</summary>
    private static void QueueTemplateTokens(string value, YamlNode node, ParseState state)
    {
        foreach (Match match in TemplateTokenPattern.Matches(value))
        {
            state.PendingVariableRefs.Add((match.Groups[1].Value, node, AllowHostVariable: true));
        }
    }

    // -- Regexes authored in an untrusted definition (worldIdPattern, lifecycle patterns) ----------------------

    /// <summary>
    /// Compiles a definition-authored regex under <see cref="RegexOptions.NonBacktracking"/> with an
    /// explicit match timeout, purely to validate it — the compiled instance is discarded. NonBacktracking
    /// guarantees linear-time matching and rejects (at compile time) many of the constructs that cause
    /// catastrophic backtracking, so a pattern that fails to compile here is treated as malformed rather
    /// than evaluated. This is the parser's whole defense against a ReDoS'd definition file: never attempt
    /// backtracking matching against untrusted regex content.
    /// </summary>
    private static void ValidateSafeRegex(string pattern, YamlNode node, ParseIssues issues, string context)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            issues.Error($"{context} pattern '{pattern}' is not a valid non-backtracking regex: {ex.Message}", node);
        }
    }
}
