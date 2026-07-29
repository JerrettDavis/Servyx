using System.Globalization;
using Servyx.Domain.Backups;

namespace Servyx.Web.Services;

/// <summary>One server's scheduled-backup configuration.</summary>
/// <param name="ServerId">The server (container name) to back up, as written in configuration.</param>
/// <param name="Interval">How often a backup is taken.</param>
/// <param name="Retention">The retention policy applied after each successful backup.</param>
/// <param name="PruneAfterBackup">
/// Whether retention is applied at all. Defaults to <see langword="true"/>, because a schedule that only
/// creates archives fills a disk; an operator who wants accumulation says so explicitly.
/// </param>
public sealed record ServerBackupSchedule(
    string ServerId,
    TimeSpan Interval,
    RetentionPolicy Retention,
    bool PruneAfterBackup);

/// <summary>
/// The scheduled-backup configuration for this process: zero or more <see cref="ServerBackupSchedule"/>,
/// read from <c>Servyx:Servers:&lt;server&gt;:Backup:*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Defaults to disabled, twice over.</strong> A server with no <c>Backup</c> section is not
/// scheduled, and no server at all is scheduled unless <see cref="ProvisioningGate"/> is open — the same
/// fail-closed rule <see cref="ServerWriteModes.ReadGrants"/> and <see cref="WritableServers"/> follow.
/// Creating a backup writes an archive and applying retention deletes archives; neither may become
/// reachable because someone edited a key in a different section.
/// </para>
/// <para>
/// <strong>Per-server, never process-wide.</strong> There is deliberately no "back up everything" key,
/// mirroring <see cref="Domain.Transport.WriteModeGrant"/>'s refusal to be transport-wide. The scheduler
/// can only touch servers that were each named on purpose.
/// </para>
/// <para>
/// The configuration shape:
/// </para>
/// <code>
/// Servyx:Servers:palworld-server:Backup:Enabled         = true
/// Servyx:Servers:palworld-server:Backup:IntervalMinutes = 360
/// Servyx:Servers:palworld-server:Backup:KeepHourly      = 6
/// Servyx:Servers:palworld-server:Backup:KeepDaily       = 7
/// Servyx:Servers:palworld-server:Backup:KeepWeekly      = 4
/// Servyx:Servers:palworld-server:Backup:Prune           = true
/// </code>
/// </remarks>
public sealed class BackupScheduleOptions
{
    /// <summary>The configuration subsection, within a server's section, holding its backup schedule.</summary>
    public const string SectionKey = "Backup";

    /// <summary>The key that opts a server in. Absent or unparseable means not scheduled.</summary>
    public const string EnabledKey = "Enabled";

    /// <summary>The smallest interval that may be configured, to stop a typo from backing up continuously.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>The interval used when a schedule is enabled without naming one.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>The retention used when a schedule is enabled without naming one.</summary>
    public static readonly RetentionPolicy DefaultRetention = new(KeepHourly: 6, KeepDaily: 7, KeepWeekly: 4);

    /// <summary>Nothing is scheduled. The state of a read-only host, and the default.</summary>
    public static readonly BackupScheduleOptions Disabled = new([]);

    /// <summary>Creates options over the given schedules.</summary>
    /// <param name="schedules">The per-server schedules. An empty list means nothing is scheduled.</param>
    public BackupScheduleOptions(IEnumerable<ServerBackupSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        Schedules = [.. schedules];
    }

    /// <summary>The per-server schedules, in configuration order.</summary>
    public IReadOnlyList<ServerBackupSchedule> Schedules { get; }

    /// <summary>Whether any server is scheduled at all.</summary>
    public bool Any => Schedules.Count > 0;

    /// <summary>
    /// Reads every scheduled server the configuration declares, or <see cref="Disabled"/> when
    /// <paramref name="gate"/> is closed.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="gate">The provisioning gate; a closed gate schedules nothing, whatever the keys say.</param>
    public static BackupScheduleOptions FromConfiguration(IConfiguration configuration, ProvisioningGate gate)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.Enabled)
        {
            return Disabled;
        }

        var schedules = new List<ServerBackupSchedule>();

        foreach (var server in configuration.GetSection(ServerWriteModes.SectionKey).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            var section = server.GetSection(SectionKey);
            if (!bool.TryParse(section[EnabledKey], out var enabled) || !enabled)
            {
                continue;
            }

            var interval = ReadMinutes(section["IntervalMinutes"]) ?? DefaultInterval;
            if (interval < MinimumInterval)
            {
                interval = MinimumInterval;
            }

            var retention = new RetentionPolicy(
                ReadCount(section["KeepHourly"]) ?? DefaultRetention.KeepHourly,
                ReadCount(section["KeepDaily"]) ?? DefaultRetention.KeepDaily,
                ReadCount(section["KeepWeekly"]) ?? DefaultRetention.KeepWeekly);

            // Absent means true: a schedule that never prunes fills the disk it is protecting.
            var prune = !bool.TryParse(section["Prune"], out var parsedPrune) || parsedPrune;

            schedules.Add(new ServerBackupSchedule(server.Key, interval, retention, prune));
        }

        return new BackupScheduleOptions(schedules);
    }

    private static TimeSpan? ReadMinutes(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : null;

    private static int? ReadCount(string? raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count
            : null;
}
