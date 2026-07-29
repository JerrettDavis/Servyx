using System.Collections.Concurrent;
using Servyx.Application.Backups;

namespace Servyx.Web.Services;

/// <summary>What one scheduled run of one server did.</summary>
public enum ScheduledBackupOutcome
{
    /// <summary>The previous run for this server was still going, so this one did nothing at all.</summary>
    Skipped,

    /// <summary>A backup was created, and retention applied if the schedule asks for it.</summary>
    Completed,

    /// <summary>The backup or the retention step failed. It was logged; the scheduler keeps running.</summary>
    Failed,
}

/// <summary>
/// An opt-in <see cref="BackgroundService"/> that periodically creates a backup of each configured server
/// and applies that server's retention policy.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It cannot run on a read-only host.</strong> Two independent things stop it: the composition
/// root only registers it inside the <c>Servyx:Provisioning:Enabled</c> block, and
/// <see cref="BackupScheduleOptions.FromConfiguration"/> returns
/// <see cref="BackupScheduleOptions.Disabled"/> whenever that gate is closed. Either alone is sufficient,
/// which is the point: a future edit that registers this unconditionally still schedules nothing.
/// </para>
/// <para>
/// <strong>Runs never stack.</strong> Each server has its own single-permit gate, taken with a zero
/// timeout. A tick that finds the previous run for that server still going logs and returns
/// <see cref="ScheduledBackupOutcome.Skipped"/> without touching the provider — so a backup that takes
/// longer than its interval falls behind rather than running twice over the same files. The gate is
/// per-server, so a slow server never delays a fast one.
/// </para>
/// <para>
/// <strong>Foreign artifacts are never pruned, and nothing here re-implements that.</strong> Retention
/// goes through <see cref="IBackupDashboard.ApplyPruneAsync"/> — the same call the UI makes, with the same
/// dry-run-then-audit barrier and the same provider-side partition beneath it. There is no delete on this
/// path that the interactive path does not also go through, so there is no second implementation to keep
/// in step.
/// </para>
/// <para>
/// <strong>A failure never ends the service.</strong> Every per-server run is wrapped: the failure is
/// logged at Error with the server it belongs to, the outcome is reported, and the loop continues to the
/// next server and the next tick. A backup that fails silently is worse than no backup at all, so nothing
/// here is caught and discarded.
/// </para>
/// <para>
/// <strong>Retention is skipped when the backup failed.</strong> Deleting old archives immediately after
/// failing to write a new one is the one ordering that can leave an operator with less than they started
/// with.
/// </para>
/// </remarks>
public sealed class ScheduledBackupService : BackgroundService
{
    /// <summary>How often due schedules are evaluated. Not the backup interval — see <see cref="ServerBackupSchedule.Interval"/>.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly BackupScheduleOptions _options;
    private readonly ILogger<ScheduledBackupService> _logger;
    private readonly IBackupDashboard? _dashboard;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextDueAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the scheduler.</summary>
    /// <param name="options">The per-server schedules. <see cref="BackupScheduleOptions.Disabled"/> makes this a no-op.</param>
    /// <param name="logger">Where failures and skips are reported.</param>
    /// <param name="dashboard">
    /// The backup surface. <see langword="null"/>, or one reporting
    /// <see cref="IBackupDashboard.ProviderConfigured"/> as <see langword="false"/>, stops the service
    /// before its first tick with a warning rather than throwing once per interval forever.
    /// </param>
    /// <param name="timeProvider">Clock and timer source. Substituted in tests; defaults to the system clock.</param>
    public ScheduledBackupService(
        BackupScheduleOptions options,
        ILogger<ScheduledBackupService> logger,
        IBackupDashboard? dashboard = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _dashboard = dashboard;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether this service has anything to do: at least one schedule, and a dashboard with a provider
    /// behind it. Public so a composition test can assert the read-only host's answer is <c>false</c>
    /// without starting a host.
    /// </summary>
    public bool WillRun => _options.Any && _dashboard is not null && _dashboard.ProviderConfigured;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Any)
        {
            _logger.LogInformation(
                "Scheduled backups are not configured; no server has {Section}:{Key} set to true. Nothing will be backed up automatically.",
                BackupScheduleOptions.SectionKey,
                BackupScheduleOptions.EnabledKey);
            return;
        }

        if (_dashboard is null || !_dashboard.ProviderConfigured)
        {
            _logger.LogWarning(
                "Scheduled backups are configured for {Count} server(s), but no backup provider is registered in this "
                + "process, so nothing can be backed up. Register one (AddServyxDockerBackups()) or remove the schedule.",
                _options.Schedules.Count);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var schedule in _options.Schedules)
        {
            // First run is one full interval away, not at startup: a process that restarts often would
            // otherwise back up on every restart, which is the opposite of a schedule.
            _nextDueAt[schedule.ServerId] = now + schedule.Interval;

            _logger.LogInformation(
                "Scheduled backups for '{ServerId}' every {Interval}; retention {Hourly}h/{Daily}d/{Weekly}w, prune {Prune}.",
                schedule.ServerId,
                schedule.Interval,
                schedule.Retention.KeepHourly,
                schedule.Retention.KeepDaily,
                schedule.Retention.KeepWeekly,
                schedule.PruneAfterBackup);
        }

        using var timer = new PeriodicTimer(PollInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunDueAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs every schedule whose interval has elapsed. Exposed so a test can drive one tick without a
    /// host and without waiting on wall-clock time.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var schedule in _options.Schedules)
        {
            ct.ThrowIfCancellationRequested();

            if (_nextDueAt.TryGetValue(schedule.ServerId, out var due) && now < due)
            {
                continue;
            }

            _nextDueAt[schedule.ServerId] = now + schedule.Interval;
            await RunServerAsync(schedule, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Backs up one server and applies its retention. Returns <see cref="ScheduledBackupOutcome.Skipped"/>
    /// without contacting the provider when a run for the same server is already in flight.
    /// </summary>
    /// <param name="schedule">The schedule to run.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ScheduledBackupOutcome> RunServerAsync(ServerBackupSchedule schedule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var gate = _gates.GetOrAdd(schedule.ServerId, static _ => new SemaphoreSlim(1, 1));

        // Zero-timeout, non-blocking. An overlapping tick must not queue behind the running one — that
        // would turn a slow backup into an ever-growing backlog of identical work.
        if (!gate.Wait(0, CancellationToken.None))
        {
            _logger.LogWarning(
                "Skipping the scheduled backup of '{ServerId}': the previous run has not finished. "
                + "The backup is taking longer than its {Interval} interval.",
                schedule.ServerId,
                schedule.Interval);
            return ScheduledBackupOutcome.Skipped;
        }

        try
        {
            return await RunServerCoreAsync(schedule, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The outermost net. Anything the dashboard did not already translate into a result case ends
            // here, logged against the server it belongs to, and the loop continues.
            _logger.LogError(ex, "The scheduled backup of '{ServerId}' failed unexpectedly.", schedule.ServerId);
            return ScheduledBackupOutcome.Failed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ScheduledBackupOutcome> RunServerCoreAsync(ServerBackupSchedule schedule, CancellationToken ct)
    {
        var dashboard = _dashboard ?? throw new InvalidOperationException(
            $"No {nameof(IBackupDashboard)} is available, so '{schedule.ServerId}' cannot be backed up.");

        var created = await dashboard.CreateAsync(schedule.ServerId, ct).ConfigureAwait(false);
        if (created is BackupCreateResult.Failed failed)
        {
            // Surfaced, never swallowed — and retention is deliberately not attempted. Pruning old
            // archives right after failing to write a new one is the one order that loses data.
            _logger.LogError(
                "The scheduled backup of '{ServerId}' failed: {Reason} Retention was not applied, so no existing "
                + "archive was deleted.",
                schedule.ServerId,
                failed.Message);
            return ScheduledBackupOutcome.Failed;
        }

        var artifact = ((BackupCreateResult.Created)created).Artifact;
        _logger.LogInformation(
            "Scheduled backup of '{ServerId}' created '{ArtifactId}' ({Bytes} bytes).",
            schedule.ServerId,
            artifact.Id,
            artifact.SizeBytes);

        if (!schedule.PruneAfterBackup)
        {
            return ScheduledBackupOutcome.Completed;
        }

        // The same call the Backups page makes. Foreign artifacts are excluded by the provider's partition
        // and by the dashboard's pre-delete audit; nothing on this path re-implements either.
        var pruned = await dashboard.ApplyPruneAsync(schedule.ServerId, schedule.Retention, ct).ConfigureAwait(false);

        switch (pruned)
        {
            case BackupPruneResult.Pruned ok:
                _logger.LogInformation(
                    "Retention for '{ServerId}': {Removed} removed, {SkippedForeign} foreign artifact(s) skipped.",
                    schedule.ServerId,
                    ok.Removed.Count,
                    ok.SkippedForeign);
                return ScheduledBackupOutcome.Completed;

            case BackupPruneResult.RefusedForeign refused:
                _logger.LogError(
                    "Retention for '{ServerId}' was refused and nothing was deleted: {Reason}",
                    schedule.ServerId,
                    refused.Message);
                return ScheduledBackupOutcome.Failed;

            default:
                _logger.LogError(
                    "Retention for '{ServerId}' did not complete: {Reason}",
                    schedule.ServerId,
                    pruned.Message);
                return ScheduledBackupOutcome.Failed;
        }
    }
}
