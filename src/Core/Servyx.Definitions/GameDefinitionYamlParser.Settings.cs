using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> SettingGroupKeys = new HashSet<string>(StringComparer.Ordinal) { "group", "items" };
    private static readonly IReadOnlySet<string> SettingItemKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "key", "label", "type", "required", "default", "renderFormat", "requiresRecreate", "publishByDefault",
        "min", "max", "step", "maxLength", "minLength", "values", "pattern", "trueValue", "falseValue", "bindings",
    };
    private static readonly IReadOnlySet<string> SettingBindingKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "surface", "direction", "key", "member", "unquote", "pointer", "strategy", "sensitive",
    };

    /// <summary>
    /// Parses the <c>settings</c> block, populating <see cref="ParseState.SettingKeys"/> as it goes (read by
    /// every deferred <c>${...}</c>/port-reference check queued while parsing earlier blocks — see
    /// <see cref="ParseState.PendingVariableRefs"/>).
    /// </summary>
    private static IReadOnlyList<SettingGroup>? ParseSettings(YamlMappingNode root, ParseIssues issues, ParseState state)
    {
        var sequence = RequireSequence(root, "settings", issues, "The definition");
        if (sequence is null)
        {
            return null;
        }

        var groups = new List<SettingGroup>();
        foreach (var node in sequence.Children)
        {
            var group = ParseSettingGroup(node, issues, state);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    private static SettingGroup? ParseSettingGroup(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'settings'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, SettingGroupKeys, issues, "A 'settings' entry");
        var name = RequireString(map, "group", issues, "A 'settings' entry");
        var itemsSequence = RequireSequence(map, "items", issues, "A 'settings' entry");

        var items = new List<SettingDescriptor>();
        if (itemsSequence is not null && name is not null)
        {
            foreach (var itemNode in itemsSequence.Children)
            {
                var item = ParseSettingItem(itemNode, name, issues, state);
                if (item is not null)
                {
                    items.Add(item);
                    state.SettingKeys.Add(item.Key);
                }
            }
        }

        return name is null ? null : new SettingGroup(name, items);
    }

    private static SettingDescriptor? ParseSettingItem(YamlNode node, string groupName, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'settings[].items'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, SettingItemKeys, issues, "A 'settings' item");

        var key = RequireString(map, "key", issues, "A 'settings' item");
        var label = RequireString(map, "label", issues, "A 'settings' item");
        var type = ParseSettingType(map, issues);
        var required = OptionalBool(map, "required", issues, "A 'settings' item") ?? false;
        var defaultValue = OptionalString(map, "default", issues, "A 'settings' item");
        var renderFormat = OptionalString(map, "renderFormat", issues, "A 'settings' item");
        var requiresRecreate = OptionalBool(map, "requiresRecreate", issues, "A 'settings' item") ?? false;
        var publishByDefault = OptionalBool(map, "publishByDefault", issues, "A 'settings' item");

        if (type == SettingType.Secret && defaultValue is not null)
        {
            map.TryGet("default", out var defaultNode);
            issues.Error(
                $"Setting '{key}' has type 'secret' but declares a literal 'default'; secrets must always "
                + "originate from the secret store, never from checked-in definition content.",
                defaultNode);
        }

        var constraints = new SettingConstraints(
            MinLength: OptionalInt(map, "minLength", issues, "A 'settings' item"),
            MaxLength: OptionalInt(map, "maxLength", issues, "A 'settings' item"),
            Min: OptionalDouble(map, "min", issues, "A 'settings' item"),
            Max: OptionalDouble(map, "max", issues, "A 'settings' item"),
            Step: OptionalDouble(map, "step", issues, "A 'settings' item"),
            Values: map.TryGet("values", out _) ? OptionalStringList(map, "values", issues, "A 'settings' item") : null,
            Pattern: OptionalString(map, "pattern", issues, "A 'settings' item"),
            TrueValue: OptionalString(map, "trueValue", issues, "A 'settings' item"),
            FalseValue: OptionalString(map, "falseValue", issues, "A 'settings' item"));

        var bindings = new List<SettingBinding>();
        foreach (var bindingNode in OptionalSequence(map, "bindings", issues, "A 'settings' item"))
        {
            var binding = ParseSettingBinding(bindingNode, issues, state);
            if (binding is not null)
            {
                bindings.Add(binding);
            }
        }

        if (key is null || label is null || type is null)
        {
            return null;
        }

        return new SettingDescriptor(key, label, groupName, type.Value, required, defaultValue, renderFormat, requiresRecreate, publishByDefault, constraints, bindings);
    }

    private static SettingType? ParseSettingType(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "type", issues, "A 'settings' item");
        return raw switch
        {
            "string" => SettingType.String,
            "text" => SettingType.Text,
            "int" => SettingType.Int,
            "float" => SettingType.Float,
            "bool" => SettingType.Bool,
            "enum" => SettingType.Enum,
            "port" => SettingType.Port,
            "secret" => SettingType.Secret,
            "path" => SettingType.Path,
            "duration" => SettingType.Duration,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static SettingType? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("type", out var node);
            issues.Error($"A 'settings' item declares 'type: {raw}', which is not a recognized setting type.", node);
            return null;
        }
    }

    private static SettingBinding? ParseSettingBinding(YamlNode node, ParseIssues issues, ParseState state)
    {
        var map = AsMapping(node, issues, "An entry of 'bindings'");
        if (map is null)
        {
            return null;
        }

        RejectUnknownKeys(map, SettingBindingKeys, issues, "A 'bindings' entry");

        var surfaceId = RequireString(map, "surface", issues, "A 'bindings' entry");
        var direction = ParseBindingDirection(map, issues);
        var sensitive = OptionalBool(map, "sensitive", issues, "A 'bindings' entry") ?? false;

        if (surfaceId is not null)
        {
            map.TryGet("surface", out var surfaceNode);
            ValidateSettingSurfaceReference(surfaceId, surfaceNode, map, issues, state);
        }

        var hasKey = map.TryGet("key", out var keyNode);
        var hasMember = map.TryGet("member", out var memberNode);
        var hasPointer = map.TryGet("pointer", out var pointerNode);

        var addressingCount = (hasKey ? 1 : 0) + (hasMember ? 1 : 0) + (hasPointer ? 1 : 0);
        if (addressingCount != 1)
        {
            issues.Error(
                "A 'bindings' entry must declare exactly one of 'key', 'member', or 'pointer' to address its value.",
                map);
            return null;
        }

        if (surfaceId is null || direction is null)
        {
            return null;
        }

        if (hasKey)
        {
            var keyValue = AsString(keyNode, issues, "A 'bindings' entry's 'key'");
            ValidateBindingSurfaceFormat(surfaceId, KeyAddressableFormats, "key", map, issues, state);
            return keyValue is null ? null : new SettingBinding.ByKey(surfaceId, direction.Value, sensitive, keyValue);
        }

        if (hasMember)
        {
            var memberValue = AsString(memberNode, issues, "A 'bindings' entry's 'member'");
            var unquote = OptionalBool(map, "unquote", issues, "A 'bindings' entry") ?? false;
            ValidateBindingSurfaceFormat(surfaceId, MemberAddressableFormats, "member", map, issues, state);
            return memberValue is null ? null : new SettingBinding.ByMember(surfaceId, direction.Value, sensitive, memberValue, unquote);
        }

        var pointerValue = AsString(pointerNode, issues, "A 'bindings' entry's 'pointer'");
        var strategy = OptionalString(map, "strategy", issues, "A 'bindings' entry");
        ValidateBindingSurfaceFormat(surfaceId, null, "pointer", map, issues, state);
        return pointerValue is null ? null : new SettingBinding.ByPointer(surfaceId, direction.Value, sensitive, pointerValue, strategy);
    }

    /// <summary>
    /// Formats a <c>key</c>-addressed binding may target: <see cref="SurfaceFormat.Dotenv"/> (the original,
    /// single-format rule) plus <see cref="SurfaceFormat.Properties"/> — both are flat <c>KEY=value</c> text
    /// with no nesting, so the same flat-key addressing scheme applies to either. Added alongside
    /// <see cref="SurfaceFormat.Properties"/> for <c>definitions/minecraft-itzg.yaml</c>'s <c>server.properties</c>
    /// surface; see that enum member's remarks.
    /// </summary>
    private static readonly IReadOnlySet<SurfaceFormat> KeyAddressableFormats =
        new HashSet<SurfaceFormat> { SurfaceFormat.Dotenv, SurfaceFormat.Properties };

    /// <summary>Formats a <c>member</c>-addressed binding may target: unchanged from the original single-format rule.</summary>
    private static readonly IReadOnlySet<SurfaceFormat> MemberAddressableFormats = new HashSet<SurfaceFormat> { SurfaceFormat.Ini };

    private static BindingDirection? ParseBindingDirection(YamlMappingNode map, ParseIssues issues)
    {
        var raw = RequireString(map, "direction", issues, "A 'bindings' entry");
        return raw switch
        {
            "read" => BindingDirection.Read,
            "write" => BindingDirection.Write,
            null => null,
            _ => Fail(map, issues, raw),
        };

        static BindingDirection? Fail(YamlMappingNode map, ParseIssues issues, string raw)
        {
            map.TryGet("direction", out var node);
            issues.Error($"A 'bindings' entry declares 'direction: {raw}'; only 'read' and 'write' are recognized.", node);
            return null;
        }
    }

    /// <summary>Every settings binding's <c>surface</c> must name a surface declared by at least one deployment profile (settings are not themselves profile-scoped — see the class remarks).</summary>
    private static void ValidateSettingSurfaceReference(string surfaceId, YamlNode? node, YamlMappingNode context, ParseIssues issues, ParseState state)
    {
        if (!state.SurfacesById.ContainsKey(surfaceId))
        {
            issues.Error($"A 'bindings' entry references surface '{surfaceId}', which no deployment profile declares.", node ?? context);
        }
    }

    /// <summary>
    /// A binding's addressing scheme must match the format of every surface declared with that id — a
    /// <c>pointer</c> binding on a <c>dotenv</c> surface (or any other mismatch) is an Error per the brief's
    /// "addressing kind must match surface format" rule. <paramref name="allowedFormats"/> is
    /// <see langword="null"/> for <c>pointer</c>, which is valid against either structured format.
    /// </summary>
    private static void ValidateBindingSurfaceFormat(string surfaceId, IReadOnlySet<SurfaceFormat>? allowedFormats, string addressingKind, YamlNode node, ParseIssues issues, ParseState state)
    {
        if (!state.SurfacesById.TryGetValue(surfaceId, out var declarations))
        {
            return;
        }

        foreach (var (deploymentId, format) in declarations)
        {
            var ok = allowedFormats is { } set
                ? set.Contains(format)
                : format is SurfaceFormat.Yaml or SurfaceFormat.Json;

            if (!ok)
            {
                issues.Error(
                    $"A '{addressingKind}' binding targets surface '{surfaceId}', which deployment '{deploymentId}' "
                    + $"declares with format '{format}'; a '{addressingKind}' addressing scheme is not valid for that format.",
                    node);
            }
        }
    }
}
