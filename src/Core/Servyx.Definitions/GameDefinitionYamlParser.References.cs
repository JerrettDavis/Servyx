using System.Text.RegularExpressions;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    /// <summary>
    /// Parses a <c>port:</c> value: a literal integer (<c>port: 8211</c>) or a whole-string
    /// <c>${SETTING_KEY}</c> reference (<c>port: "${RCON_PORT}"</c>). Any other shape is malformed. The
    /// referenced key, when present, is queued against the deferred settings-key check — see
    /// <see cref="ParseState.PendingVariableRefs"/> — since <c>settings</c> is not necessarily parsed yet.
    /// </summary>
    private static PortRef? ParsePortRef(YamlMappingNode parent, string key, ParseIssues issues, ParseState state, bool allowHostVariable, string context)
    {
        if (!parent.TryGet(key, out var node))
        {
            issues.Error($"{context} declares no '{key}'.", parent);
            return null;
        }

        return ParsePortRefValue(node, issues, state, allowHostVariable, $"{context}'s '{key}'");
    }

    private static PortRef? ParsePortRefValue(YamlNode node, ParseIssues issues, ParseState state, bool allowHostVariable, string context)
    {
        if (node is not YamlScalarNode { Value: { } raw })
        {
            issues.Error($"{context} must be a port number or a '${{SETTING_KEY}}' reference.", node);
            return null;
        }

        if (int.TryParse(raw, out var literal))
        {
            return new PortRef.Literal(literal);
        }

        var match = WholeTemplateTokenPattern.Match(raw);
        if (match.Success)
        {
            var key = match.Groups[1].Value;
            state.PendingVariableRefs.Add((key, node, allowHostVariable));
            return new PortRef.SettingRef(key);
        }

        issues.Error($"{context} value '{raw}' is neither a port number nor a '${{SETTING_KEY}}' reference.", node);
        return null;
    }

    private static readonly Regex WholeTemplateTokenPattern = new(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a <c>passwordRef:</c>/<c>secret</c>-typed reference, e.g. <c>"secret:admin-password"</c>.
    /// Only the <c>secret:</c> scheme is accepted — see the remarks on <see cref="SecretRef"/> for why this
    /// is a closed, definition-authored reference rather than the fully-qualified <c>secret://</c> URN
    /// format resolved at the point of use.
    /// </summary>
    private static SecretRef? ParseSecretRefValue(YamlNode node, ParseIssues issues, string context)
    {
        var raw = AsString(node, issues, context);
        if (raw is null)
        {
            return null;
        }

        var colon = raw.IndexOf(':');
        if (colon <= 0 || colon == raw.Length - 1)
        {
            issues.Error($"{context} value '{raw}' must be of the form 'scheme:key'.", node);
            return null;
        }

        var scheme = raw[..colon];
        var key = raw[(colon + 1)..];
        if (!string.Equals(scheme, "secret", StringComparison.Ordinal))
        {
            issues.Error($"{context} declares scheme '{scheme}'; only 'secret:' is accepted.", node);
            return null;
        }

        return new SecretRef(scheme, key);
    }

    /// <summary>
    /// Parses an <c>enabledWhen:</c> expression. Only the closed, documented shape
    /// <c>surface.key == 'value'</c> is recognized — see the remarks on <see cref="EnabledWhenPredicate"/>
    /// for why this is not a general expression evaluator. Anything else is a validation Error, never
    /// evaluated.
    /// </summary>
    private static readonly Regex EnabledWhenPattern = new(
        @"^\s*(?<surface>[A-Za-z_][A-Za-z0-9_]*)\.(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*==\s*'(?<value>[^']*)'\s*$",
        RegexOptions.Compiled);

    private static EnabledWhenPredicate? ParseEnabledWhenValue(YamlNode node, ParseIssues issues, string context)
    {
        var raw = AsString(node, issues, context);
        if (raw is null)
        {
            return null;
        }

        var match = EnabledWhenPattern.Match(raw);
        if (!match.Success)
        {
            issues.Error(
                $"{context} value '{raw}' does not match the only supported shape, \"surface.key == 'value'\". "
                + "Compound or otherwise-shaped expressions are rejected rather than evaluated.",
                node);
            return null;
        }

        return new EnabledWhenPredicate(match.Groups["surface"].Value, match.Groups["key"].Value, match.Groups["value"].Value);
    }
}
