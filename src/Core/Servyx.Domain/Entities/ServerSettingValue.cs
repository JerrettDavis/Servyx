using Servyx.Domain.Common;

namespace Servyx.Domain.Entities;

/// <summary>
/// One operator-recorded DESIRED value for a single setting on a single <see cref="Server"/> — Servyx's own
/// recorded intent, and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is intent, not an applied value.</strong> Persisting a row here changes nothing about the
/// running server: there is no <c>IPlanExecutor</c>, no config-surface writer, and no container recreate
/// wired to this table (see <c>docs/plans/ui-management-surface.md</c>, "Phase 4a" vs "Phase 4b — Apply").
/// A caller that reads this table must render it as <em>desired</em>, alongside the authoritative/rendered/
/// runtime values <c>Servyx.Application.Servers.ServerSettingValue</c> already carries, never as a value the
/// server has adopted.
/// </para>
/// <para>
/// Keyed on (<see cref="ServerId"/>, <see cref="Key"/>) — see <see cref="Servyx.Infrastructure.Persistence.Configurations.ServerSettingValueConfiguration"/>
/// for the composite primary key. One row per setting per server; a re-save overwrites the prior value and
/// its attribution rather than appending a history row — there is no audit trail here beyond "who touched it
/// last", matching the same attribution honesty <see cref="Server.WriteModeChangedBy"/> already documents:
/// Servyx has one shared operator password, not per-operator accounts.
/// </para>
/// </remarks>
public sealed class ServerSettingValue
{
    /// <summary>The <see cref="Server"/> this desired value belongs to.</summary>
    public required ServerId ServerId { get; set; }

    /// <summary>The setting's catalogue key, matching <c>SettingDescriptor.Key</c> — not a surface-specific binding key.</summary>
    public required string Key { get; set; }

    /// <summary>
    /// The operator's desired value, as raw text. Stored exactly as typed — no type coercion, no surface
    /// encoding — because this table has no opinion about the setting's <c>SettingType</c>; that shaping is
    /// the concern of whatever eventually renders or applies it.
    /// </summary>
    public required string Value { get; set; }

    /// <summary>
    /// Who recorded this value. Servyx has one shared operator password and no per-operator accounts, so in
    /// practice this is a constant (<c>OperatorAuthentication.OperatorNameClaimValue</c>) — recorded anyway,
    /// the same way <see cref="Server.WriteModeChangedBy"/> is, because a future per-operator identity system
    /// should not require a schema change to become meaningful.
    /// </summary>
    public required string UpdatedBy { get; set; }

    /// <summary>When this value was last recorded.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
