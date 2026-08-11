using System.Globalization;

namespace Servyx.Composition;

/// <summary>
/// How long a change plan's recorded pre-/post-image content is kept, and how often the sweep that discards
/// it runs.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this knob exists at all.</strong> <c>ChangePlanActionRecord.PreImageContent</c> and
/// <c>PostImageContent</c> hold whole configuration files verbatim and UNMASKED, including the operator's
/// real secret values in plaintext when the surface carries any. That is load-bearing — an exact revert
/// restores the literal pre-image rather than inverting a diff — and it is also a plaintext secret store that
/// grows without bound if nothing ever removes it. The sweep is not optional housekeeping; shipping
/// <c>IPlanExecutor.ApplyAsync</c> without it was explicitly ruled out.
/// </para>
/// <para>
/// <strong>The trade this knob makes, stated plainly.</strong> Once an applied plan's images are discarded,
/// that plan can no longer be reverted — there is nothing left to restore from, and
/// <c>IPlanExecutor.RevertAsync</c> must refuse it rather than pretend. Raising
/// <see cref="ImageRetention"/> buys a longer revert horizon at the cost of holding plaintext secrets for
/// longer; lowering it does the reverse. A plan that changed NOTHING on the server (nothing of its was ever
/// applied) is exempt from the window entirely and has its images discarded as soon as it is terminal,
/// because no revert could ever want them.
/// </para>
/// <para>
/// Non-terminal plans are never swept at any age — see <c>IChangePlanStore.PurgeImagesAsync</c>.
/// </para>
/// <para>
/// The configuration shape:
/// </para>
/// <code>
/// Servyx:ChangePlans:Retention:Enabled            = true
/// Servyx:ChangePlans:Retention:ImageRetentionDays = 30
/// Servyx:ChangePlans:Retention:SweepMinutes       = 60
/// </code>
/// </remarks>
public sealed class ChangePlanRetentionOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionKey = "Servyx:ChangePlans:Retention";

    /// <summary>The key that switches the sweep off. Absent means enabled.</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>
    /// How long an applied plan keeps its images by default: long enough that reverting a bad configuration
    /// change days later is still possible, short enough that a plaintext secret does not live in the
    /// database indefinitely.
    /// </summary>
    public static readonly TimeSpan DefaultImageRetention = TimeSpan.FromDays(30);

    /// <summary>How often the sweep runs by default.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The shortest sweep interval that may be configured, so a typo cannot turn the sweep into a hot loop
    /// against the database.
    /// </summary>
    public static readonly TimeSpan MinimumSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The sweep does not run. Nothing is ever purged and no plan is ever promoted to stale.</summary>
    public static readonly ChangePlanRetentionOptions Disabled =
        new(enabled: false, DefaultImageRetention, DefaultSweepInterval);

    /// <summary>Creates options.</summary>
    /// <param name="enabled">Whether the sweep runs at all.</param>
    /// <param name="imageRetention">How long a plan that took effect keeps its images. Negative values are clamped to zero.</param>
    /// <param name="sweepInterval">How often the sweep runs. Values below <see cref="MinimumSweepInterval"/> are raised to it.</param>
    public ChangePlanRetentionOptions(bool enabled, TimeSpan imageRetention, TimeSpan sweepInterval)
    {
        Enabled = enabled;
        ImageRetention = imageRetention < TimeSpan.Zero ? TimeSpan.Zero : imageRetention;
        SweepInterval = sweepInterval < MinimumSweepInterval ? MinimumSweepInterval : sweepInterval;
    }

    /// <summary>Whether the retention sweep runs.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// How long a plan that actually changed something keeps its recorded images after it took effect. After
    /// this elapses the plan can no longer be reverted from them.
    /// </summary>
    public TimeSpan ImageRetention { get; }

    /// <summary>How often the sweep runs.</summary>
    public TimeSpan SweepInterval { get; }

    /// <summary>Reads the options from configuration, falling back to the documented defaults.</summary>
    /// <remarks>
    /// Defaults to ENABLED when the section is absent, which is the opposite of how the provisioning-gated
    /// options in this project default — and deliberately so. Those gate operations that touch a game server;
    /// this one only deletes plaintext secrets Servyx itself recorded, and an install that never configured
    /// it is precisely the install that should not accumulate them forever.
    /// </remarks>
    /// <param name="configuration">The application configuration.</param>
    public static ChangePlanRetentionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionKey);

        var enabled = !bool.TryParse(section[EnabledKey], out var parsed) || parsed;
        var retention = ReadDays(section["ImageRetentionDays"]) ?? DefaultImageRetention;
        var sweep = ReadMinutes(section["SweepMinutes"]) ?? DefaultSweepInterval;

        return new ChangePlanRetentionOptions(enabled, retention, sweep);
    }

    private static TimeSpan? ReadDays(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var days) && days >= 0
            ? TimeSpan.FromDays(days)
            : null;

    private static TimeSpan? ReadMinutes(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;
}
