namespace Servyx.Domain.Definitions.Model;

/// <summary>The type of a single settings-catalogue entry, determining how its value is validated and rendered.</summary>
public enum SettingType
{
    /// <summary>A short single-line string.</summary>
    String,

    /// <summary>A longer, potentially multi-line string.</summary>
    Text,

    /// <summary>An integer.</summary>
    Int,

    /// <summary>A floating-point number.</summary>
    Float,

    /// <summary>A boolean, possibly rendered using non-standard tokens; see <see cref="SettingConstraints.TrueValue"/>/<see cref="SettingConstraints.FalseValue"/>.</summary>
    Bool,

    /// <summary>One of a closed set of string values; see <see cref="SettingConstraints.Values"/>.</summary>
    Enum,

    /// <summary>A TCP/UDP port number.</summary>
    Port,

    /// <summary>A sensitive value. Never carries a literal <c>default</c> in a definition — see the "Secrets must never carry literal defaults" rule in <c>docs/schema.md</c>.</summary>
    Secret,

    /// <summary>A filesystem path.</summary>
    Path,

    /// <summary>A duration, e.g. <c>45s</c>.</summary>
    Duration,
}

/// <summary>Validation and rendering constraints for a <see cref="SettingDescriptor"/>.</summary>
/// <remarks>
/// <strong>Known gap, deferred to the validator, not redesigned here.</strong> Unlike the closed-hierarchy
/// discipline used everywhere else in this model (<see cref="SettingBinding"/>, <see cref="PortRef"/>,
/// <see cref="InstallStep"/>, …), this type is a flat bag of independent nullable fields, correlated with
/// the owning <see cref="SettingDescriptor.Type"/> only by convention — nothing here stops a
/// <see cref="SettingType.Bool"/> descriptor from also carrying <see cref="Values"/> or
/// <see cref="Min"/>/<see cref="Max"/>, an incoherent combination that this record cannot reject by
/// construction. Closing <see cref="SettingDescriptor.Type"/> and <see cref="SettingConstraints"/> into one
/// discriminated <c>SettingSpec</c> hierarchy (one case per <see cref="SettingType"/>, each carrying only
/// its own applicable constraints) would fix this, but is a larger reshaping than this phase warrants.
/// Recorded here so the parser/validator phase picks up type/constraint coherence checking deliberately,
/// rather than rediscovering the gap.
/// </remarks>
/// <param name="MinLength">Minimum string length, for <see cref="SettingType.String"/>/<see cref="SettingType.Text"/>.</param>
/// <param name="MaxLength">Maximum string length, for <see cref="SettingType.String"/>/<see cref="SettingType.Text"/>.</param>
/// <param name="Min">Minimum numeric value, for <see cref="SettingType.Int"/>/<see cref="SettingType.Float"/>/<see cref="SettingType.Port"/>.</param>
/// <param name="Max">Maximum numeric value.</param>
/// <param name="Step">Numeric step increment, for <see cref="SettingType.Float"/>.</param>
/// <param name="Values">The closed set of allowed values, for <see cref="SettingType.Enum"/>.</param>
/// <param name="Pattern">A regex the value must match, if declared.</param>
/// <param name="TrueValue">The literal token written for a <see langword="true"/> <see cref="SettingType.Bool"/>, e.g. <c>True</c>. Null means the surface's own default token.</param>
/// <param name="FalseValue">The literal token written for a <see langword="false"/> <see cref="SettingType.Bool"/>, e.g. <c>False</c>.</param>
public sealed record SettingConstraints(
    int? MinLength,
    int? MaxLength,
    double? Min,
    double? Max,
    double? Step,
    IReadOnlyList<string>? Values,
    string? Pattern,
    string? TrueValue,
    string? FalseValue);

/// <summary>Which way a <see cref="SettingBinding"/> moves data between the settings catalogue and a surface.</summary>
public enum BindingDirection
{
    /// <summary>Servyx reads this surface's value to show <c>Rendered</c>/<c>Runtime</c> columns and compute drift. Never written.</summary>
    Read,

    /// <summary>Servyx writes desired values to this surface. Normally exactly one binding per setting has this direction.</summary>
    Write,
}

/// <summary>
/// One entry of a <see cref="SettingDescriptor"/>'s <c>bindings</c> list: ties the setting to a value that
/// lives on one <see cref="DeclaredConfigSurface"/>.
/// </summary>
/// <remarks>
/// Closed over how the value is addressed within that surface — by a flat key (dotenv/properties), by a
/// codec member (ini + codec), or by a structured pointer (yaml/json, RFC 6901) — because each addressing
/// scheme needs different parameters. A fourth scheme is a deliberate addition to this hierarchy, not a
/// guess encoded into an open string field.
/// </remarks>
/// <param name="SurfaceId">The <see cref="DeclaredConfigSurface.Id"/> this binding targets.</param>
/// <param name="Direction">Whether this binding is read from or written to.</param>
/// <param name="Sensitive">Whether the value at this binding should be masked in the UI and logs.</param>
public abstract record SettingBinding(string SurfaceId, BindingDirection Direction, bool Sensitive)
{
    /// <summary>Addresses a value by a flat key — dotenv and properties-style surfaces.</summary>
    /// <param name="Key">The key within the surface, e.g. <c>SERVER_NAME</c>.</param>
    public sealed record ByKey(string SurfaceId, BindingDirection Direction, bool Sensitive, string Key)
        : SettingBinding(SurfaceId, Direction, Sensitive);

    /// <summary>Addresses a value by member name within a codec-decoded scalar — ini surfaces that declare a <see cref="DeclaredConfigSurface.Codec"/>.</summary>
    /// <param name="Member">The decoded member name, e.g. <c>ServerName</c>.</param>
    /// <param name="Unquote">Whether to strip surrounding quotes when displaying the value.</param>
    public sealed record ByMember(string SurfaceId, BindingDirection Direction, bool Sensitive, string Member, bool Unquote)
        : SettingBinding(SurfaceId, Direction, Sensitive);

    /// <summary>Addresses a value by an RFC 6901 JSON-pointer-style path — structured yaml/json surfaces and control-channel responses.</summary>
    /// <param name="Pointer">The pointer expression, e.g. <c>/services/palworld/ports</c>.</param>
    /// <param name="Strategy">A named transform applied when writing this binding, e.g. <c>publish-udp</c> for compose port publication. Null for a plain value write.</param>
    public sealed record ByPointer(string SurfaceId, BindingDirection Direction, bool Sensitive, string Pointer, string? Strategy)
        : SettingBinding(SurfaceId, Direction, Sensitive);
}

/// <summary>The single writable binding of a <see cref="SettingDescriptor"/>, if it has one.</summary>
/// <param name="SurfaceId">The surface the setting is written to. Same as <c>Binding.SurfaceId</c>, exposed directly for convenience.</param>
/// <param name="Binding">The writable binding itself.</param>
public sealed record SettingWriteTarget(string SurfaceId, SettingBinding Binding);

/// <summary>
/// One entry of a <see cref="SettingGroup"/>'s <c>items</c> list: a single user-facing setting and every
/// surface it is tied to.
/// </summary>
/// <param name="Key">The setting's catalogue key, e.g. <c>SERVER_NAME</c>.</param>
/// <param name="Label">Human-readable label shown in the UI.</param>
/// <param name="Group">
/// The display name of the <see cref="SettingGroup"/> this setting belongs to. Denormalized onto the
/// descriptor itself — duplicating <see cref="SettingGroup.Name"/> — so a flattened list of settings (e.g.
/// search results) still carries its group without the caller needing the enclosing <see cref="SettingGroup"/>.
/// </param>
/// <param name="Type">The setting's value type.</param>
/// <param name="Required">Whether a value must be supplied.</param>
/// <param name="Default">The default value, if any. Must be null for <see cref="SettingType.Secret"/> settings — see the "Secrets must never carry literal defaults" rule in <c>docs/schema.md</c>.</param>
/// <param name="RenderFormat">A type-specific rendering hint, e.g. <c>F6</c> for Unreal's six-decimal floats.</param>
/// <param name="RequiresRecreate">Whether changing this setting requires the workload's container to be recreated rather than just restarted (e.g. because the value is baked in at container create).</param>
/// <param name="PublishByDefault">For a <see cref="SettingType.Port"/> setting, whether Servyx should expose it to the host network by default. Null when not applicable.</param>
/// <param name="Constraints">Validation and rendering constraints.</param>
/// <param name="Bindings">Every surface this setting is tied to.</param>
public sealed record SettingDescriptor(
    string Key,
    string Label,
    string Group,
    SettingType Type,
    bool Required,
    string? Default,
    string? RenderFormat,
    bool RequiresRecreate,
    bool? PublishByDefault,
    SettingConstraints Constraints,
    IReadOnlyList<SettingBinding> Bindings)
{
    /// <summary>
    /// True when this setting's value must be masked in the UI and logs — either because its
    /// <see cref="Type"/> is itself <see cref="SettingType.Secret"/>, or because any of its
    /// <see cref="Bindings"/> is individually marked <see cref="SettingBinding.Sensitive"/>.
    /// </summary>
    public bool IsSecret => Type is SettingType.Secret || Bindings.Any(b => b.Sensitive);

    /// <summary>
    /// The single surface Servyx actually writes desired values to, or <see langword="null"/> if this
    /// setting has no writable binding — every binding is <see cref="BindingDirection.Read"/>.
    /// </summary>
    public SettingWriteTarget? WritableSurface =>
        Bindings.FirstOrDefault(b => b.Direction == BindingDirection.Write) is { } write
            ? new SettingWriteTarget(write.SurfaceId, write)
            : null;
}

/// <summary>One entry of a definition's top-level <c>settings</c> list: a named group of related settings.</summary>
/// <param name="Name">The group's display name, e.g. <c>Identity</c>, <c>Networking</c>.</param>
/// <param name="Items">The settings in this group.</param>
public sealed record SettingGroup(string Name, IReadOnlyList<SettingDescriptor> Items);
