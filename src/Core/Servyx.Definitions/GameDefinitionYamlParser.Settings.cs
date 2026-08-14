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
        "surface", "direction", "key", "member", "unquote", "pointer", "strategy", "sensitive", "mirrorWrite",
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

        // The descriptor-level half of the sensitivity exclusion. ValidateMirrorWriteBinding already refuses a
        // binding that is itself marked 'sensitive: true', but a setting can be sensitive without any single
        // binding saying so — 'type: secret' makes the whole descriptor secret (see SettingDescriptor.IsSecret),
        // and Palworld's join password is exactly that shape. Checked here, where the type and the bindings are
        // both in hand, so an author is told at parse time rather than discovering at plan time that the flag
        // is being ignored.
        if (type == SettingType.Secret && bindings.Any(b => b.MirrorWrite))
        {
            issues.Error(
                $"Setting '{key}' has type 'secret' and declares a binding with 'mirrorWrite: true'. A secret "
                + "is never mirrored onto a derived surface: that would place a second copy of it in a file "
                + "the workload rewrites at will, which the authoritative write already covers without.",
                map);
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
        var mirrorWrite = OptionalBool(map, "mirrorWrite", issues, "A 'bindings' entry") ?? false;

        if (surfaceId is not null)
        {
            map.TryGet("surface", out var surfaceNode);
            ValidateSettingSurfaceReference(surfaceId, surfaceNode, map, issues, state);
        }

        if (mirrorWrite && surfaceId is not null && direction is not null)
        {
            ValidateMirrorWriteBinding(surfaceId, direction.Value, sensitive, map, issues, state);
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
            return keyValue is null
                ? null
                : new SettingBinding.ByKey(surfaceId, direction.Value, sensitive, keyValue) { MirrorWrite = mirrorWrite };
        }

        if (hasMember)
        {
            var memberValue = AsString(memberNode, issues, "A 'bindings' entry's 'member'");
            var unquote = OptionalBool(map, "unquote", issues, "A 'bindings' entry") ?? false;
            ValidateBindingSurfaceFormat(surfaceId, MemberAddressableFormats, "member", map, issues, state);
            return memberValue is null
                ? null
                : new SettingBinding.ByMember(surfaceId, direction.Value, sensitive, memberValue, unquote) { MirrorWrite = mirrorWrite };
        }

        var pointerValue = AsString(pointerNode, issues, "A 'bindings' entry's 'pointer'");
        var strategy = OptionalString(map, "strategy", issues, "A 'bindings' entry");
        ValidateBindingSurfaceFormat(surfaceId, null, "pointer", map, issues, state);
        return pointerValue is null
            ? null
            : new SettingBinding.ByPointer(surfaceId, direction.Value, sensitive, pointerValue, strategy) { MirrorWrite = mirrorWrite };
    }

    /// <summary>
    /// The binding half of the two-key mirrored-write opt-in: a <c>mirrorWrite: true</c> binding must be a
    /// <c>read</c> binding, must target a surface that itself declares <c>mirrorWrites: true</c>, and must
    /// not be marked <c>sensitive</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these is an Error rather than a silent drop. A flag whose author believes it took effect
    /// and which quietly did nothing is worse than no flag: the operator's UI would offer a mirror toggle
    /// that never mirrors, with nothing anywhere saying why.
    /// </para>
    /// <para>
    /// The <c>sensitive</c> refusal is the security-relevant one, and it is only half of the enforcement —
    /// the other half is <see cref="SettingDescriptor.MirroredBindings"/>, which returns nothing for a
    /// secret-typed setting whatever its bindings declare. This check catches the per-binding spelling at
    /// authoring time; that property catches the whole descriptor at plan time, including a setting whose
    /// <c>type: secret</c> makes it sensitive without any binding saying so. Neither is sufficient alone.
    /// </para>
    /// </remarks>
    private static void ValidateMirrorWriteBinding(
        string surfaceId,
        BindingDirection direction,
        bool sensitive,
        YamlMappingNode map,
        ParseIssues issues,
        ParseState state)
    {
        map.TryGet("mirrorWrite", out var mirrorNode);
        var node = mirrorNode ?? map;

        if (direction != BindingDirection.Read)
        {
            issues.Error(
                $"A 'bindings' entry declares 'mirrorWrite: true' with 'direction: {direction}'; a mirrored "
                + "write applies only to a 'read' binding on a derived surface. A 'write' binding already "
                + "writes its surface directly.",
                node);
        }

        if (sensitive)
        {
            issues.Error(
                $"A 'bindings' entry on surface '{surfaceId}' declares both 'sensitive: true' and "
                + "'mirrorWrite: true'. A sensitive value is never mirrored: doing so would write a second "
                + "copy of a secret onto a file the workload rewrites at will, for no benefit the "
                + "authoritative write does not already provide.",
                node);
        }

        if (!state.SurfacesById.TryGetValue(surfaceId, out var declarations))
        {
            // The surface reference itself is already reported by ValidateSettingSurfaceReference; saying so
            // twice would just be noise.
            return;
        }

        // "At least one" rather than "every one", because a surface id is shared across deployment PROFILES
        // while a settings binding is not scoped to any of them (see the class remarks). Palworld is the
        // shipped example: 'palworldsettings' is a derived, mirror-accepting surface under the docker profile
        // and the AUTHORITATIVE surface under native-steamcmd, where mirroring is meaningless rather than
        // wrong. Requiring every declaration to opt in would make the flag unusable on exactly the kind of
        // definition it exists for. What must not pass is a binding that opts in against a surface no profile
        // anywhere accepts mirrored writes on — that one really is a flag that can never do anything.
        if (declarations.Any(d => d.MirrorWrites))
        {
            return;
        }

        var declaredAs = string.Join(
            ", ",
            declarations.Select(d => $"'{d.DeploymentId}' (role: {d.Role})"));

        issues.Error(
            $"A 'bindings' entry declares 'mirrorWrite: true' against surface '{surfaceId}', which no "
            + $"deployment declares with 'mirrorWrites: true' — it is declared by {declaredAs}. Both halves "
            + "are required: the surface must declare that it accepts mirrored writes, and each individual "
            + "setting must opt in.",
            node);
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

        foreach (var (deploymentId, format, _, _) in declarations)
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
