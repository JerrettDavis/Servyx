namespace Servyx.Domain.Configuration;

/// <summary>Which pairs of columns in a <see cref="SettingState"/> disagree.</summary>
[Flags]
public enum DriftKind
{
    /// <summary>No drift detected.</summary>
    None = 0,

    /// <summary>Servyx's desired value differs from the current authoritative surface value.</summary>
    DesiredVsAuthoritative = 1 << 0,

    /// <summary>The authoritative surface value differs from the rendered (derived) surface value.</summary>
    AuthoritativeVsRendered = 1 << 1,

    /// <summary>The rendered (derived) value differs from the live runtime value.</summary>
    RenderedVsRuntime = 1 << 2,

    /// <summary>One or more columns could not be read.</summary>
    Unreadable = 1 << 3,
}

/// <summary>
/// The four-column view of a single setting, mirroring the surface roles: Servyx's intent, the current
/// authoritative value, the current rendered (derived) value, and the current live (runtime) value.
/// </summary>
/// <param name="Desired">Servyx's intent, as stored in the database.</param>
/// <param name="Authoritative">The current value of the authoritative surface (e.g. <c>.env</c>).</param>
/// <param name="Rendered">The current value of the generated (derived) surface.</param>
/// <param name="Runtime">The current live value on the running server.</param>
/// <param name="Drift">Which columns disagree.</param>
/// <param name="PendingRegeneration">True when a restart is needed before drift can resolve.</param>
/// <param name="IsWritable">Whether this setting currently has a writable, authoritative binding.</param>
/// <param name="NotWritableReason">Human-readable reason, when <paramref name="IsWritable"/> is false.</param>
public sealed record SettingState(
    string? Desired,
    string? Authoritative,
    string? Rendered,
    string? Runtime,
    DriftKind Drift,
    bool PendingRegeneration,
    bool IsWritable,
    string? NotWritableReason);

/// <summary>Computes <see cref="SettingState"/> for a setting across its bound surfaces.</summary>
public interface ISettingStateResolver
{
    /// <summary>Resolves the current state of a single setting, by its catalogue key.</summary>
    Task<SettingState> ResolveAsync(string settingKey, CancellationToken ct = default);
}
