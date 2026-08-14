using System.Globalization;

namespace Servyx.Composition;

/// <summary>How often <see cref="ServerStatusRefreshService"/> refreshes every adopted server's cached status and resource sample.</summary>
/// <remarks>
/// <para>
/// Registered unconditionally (see <c>ServyxCoreCompositionExtensions.AddServyxCore</c>) — unlike
/// <c>ChangePlanRetentionOptions</c> or <c>BackupScheduleOptions</c>, there is no "disabled" state here: a
/// background refresh worker with a cache behind it is strictly better than the live-call-per-page-load
/// behaviour it replaces, for every deployment shape this project supports (single-instance, SQLite-backed —
/// see the class remarks on <c>ServerStatusCache</c>). There is nothing an operator would want to switch off.
/// </para>
/// <code>
/// Servyx:ServerStatus:Refresh:IntervalSeconds = 20
/// </code>
/// </remarks>
public sealed class ServerStatusRefreshOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionKey = "Servyx:ServerStatus:Refresh";

    /// <summary>How often the refresh runs by default.</summary>
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The shortest refresh interval that may be configured, so a typo cannot turn this into a hot loop
    /// against every configured host's transport.
    /// </summary>
    public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>Creates options with the documented default interval.</summary>
    public ServerStatusRefreshOptions()
        : this(DefaultRefreshInterval)
    {
    }

    /// <summary>Creates options.</summary>
    /// <param name="refreshInterval">How often to refresh. Values below <see cref="MinimumRefreshInterval"/> are raised to it.</param>
    public ServerStatusRefreshOptions(TimeSpan refreshInterval)
    {
        RefreshInterval = refreshInterval < MinimumRefreshInterval ? MinimumRefreshInterval : refreshInterval;
    }

    /// <summary>How often every adopted server's cached status and resource sample is refreshed.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>Reads the options from configuration, falling back to the documented default.</summary>
    /// <param name="configuration">The application configuration.</param>
    public static ServerStatusRefreshOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionKey);
        var interval = ReadSeconds(section["IntervalSeconds"]) ?? DefaultRefreshInterval;

        return new ServerStatusRefreshOptions(interval);
    }

    private static TimeSpan? ReadSeconds(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
