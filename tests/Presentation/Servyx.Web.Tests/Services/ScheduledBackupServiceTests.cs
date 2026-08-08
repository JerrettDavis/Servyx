using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Web.Services;
using Servyx.Composition;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Tests for <see cref="ScheduledBackupService"/> and the configuration it reads.
/// </summary>
/// <remarks>
/// Every failure guarded against here is one that first appears on an operator's machine at 03:00: a
/// scheduler that stacked runs, one that pruned after a failed backup, one that died on the first error,
/// or one that started at all on a host whose provisioning flag is off.
/// </remarks>
public class ScheduledBackupServiceTests
{
    private const string ServerId = "palworld-server";

    private static readonly ServerBackupSchedule Schedule = new(
        ServerId,
        TimeSpan.FromHours(1),
        new RetentionPolicy(1, 1, 1),
        PruneAfterBackup: true);

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    // ── The gate ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_the_provisioning_flag_off_nothing_is_scheduled_however_the_backup_keys_are_set()
    {
        var configuration = Config(
            ("Servyx:Servers:palworld-server:Backup:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Backup:IntervalMinutes", "10"));

        var options = BackupScheduleOptions.FromConfiguration(configuration, ProvisioningGate.Closed);

        options.Any.Should().BeFalse();
        options.Schedules.Should().BeEmpty();
    }

    [Fact]
    public async Task The_scheduler_does_not_start_when_the_flag_is_off()
    {
        var configuration = Config(("Servyx:Servers:palworld-server:Backup:Enabled", "true"));
        var options = BackupScheduleOptions.FromConfiguration(configuration, ProvisioningGate.Closed);

        var dashboard = new FakeBackupDashboard();
        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(options, logger, dashboard);

        service.WillRun.Should().BeFalse();

        // Start and stop it anyway: with nothing scheduled, ExecuteAsync must return before its first tick
        // rather than sitting on a timer that could ever reach a provider.
        await RunToCompletionAsync(service);

        dashboard.CreateCalls.Should().Be(0);
        dashboard.ApplyPruneCalls.Should().Be(0);
        logger.Entries.Should().Contain(e => e.Message.Contains("Scheduled backups are not configured"));
    }

    [Fact]
    public void A_server_with_no_backup_section_is_not_scheduled_even_with_the_flag_on()
    {
        var configuration = Config(("Servyx:Servers:palworld-server:WriteMode", "Enabled"));

        var options = BackupScheduleOptions.FromConfiguration(configuration, new ProvisioningGate(enabled: true));

        options.Any.Should().BeFalse();
    }

    [Fact]
    public void An_enabled_server_reads_its_interval_and_retention()
    {
        var configuration = Config(
            ("Servyx:Servers:palworld-server:Backup:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Backup:IntervalMinutes", "120"),
            ("Servyx:Servers:palworld-server:Backup:KeepHourly", "3"),
            ("Servyx:Servers:palworld-server:Backup:KeepDaily", "5"),
            ("Servyx:Servers:palworld-server:Backup:KeepWeekly", "2"));

        var options = BackupScheduleOptions.FromConfiguration(configuration, new ProvisioningGate(enabled: true));

        var schedule = options.Schedules.Should().ContainSingle().Subject;
        schedule.ServerId.Should().Be("palworld-server");
        schedule.Interval.Should().Be(TimeSpan.FromHours(2));
        schedule.Retention.Should().Be(new RetentionPolicy(3, 5, 2));
        schedule.PruneAfterBackup.Should().BeTrue();
    }

    [Fact]
    public void An_absurdly_small_interval_is_clamped_rather_than_honoured()
    {
        var configuration = Config(
            ("Servyx:Servers:palworld-server:Backup:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Backup:IntervalMinutes", "1"));

        var options = BackupScheduleOptions.FromConfiguration(configuration, new ProvisioningGate(enabled: true));

        options.Schedules.Should().ContainSingle().Which.Interval.Should().Be(BackupScheduleOptions.MinimumInterval);
    }

    [Fact]
    public async Task With_schedules_but_no_provider_the_service_says_so_and_stops()
    {
        var options = new BackupScheduleOptions([Schedule]);
        var dashboard = new FakeBackupDashboard { ProviderConfigured = false };
        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(options, logger, dashboard);

        service.WillRun.Should().BeFalse();

        await RunToCompletionAsync(service);

        dashboard.CreateCalls.Should().Be(0);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no backup provider is registered"));
    }

    /// <summary>
    /// Starts the service and waits for its <c>ExecuteAsync</c> to finish before stopping it.
    /// </summary>
    /// <remarks>
    /// <see cref="Microsoft.Extensions.Hosting.BackgroundService.StartAsync"/> no longer runs
    /// <c>ExecuteAsync</c> inline — it is dispatched to the thread pool so a slow background service cannot
    /// hold up host startup. Asserting on log output immediately after <c>StartAsync</c> therefore races
    /// the service, and stopping it first can cancel the dispatch before the body ever runs. Awaiting
    /// <c>ExecuteTask</c> is what makes "it decided not to run, and said why" an observable fact.
    /// </remarks>
    private static async Task RunToCompletionAsync(ScheduledBackupService service)
    {
        await service.StartAsync(CancellationToken.None);

        if (service.ExecuteTask is { } execute)
        {
            await execute.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await service.StopAsync(CancellationToken.None);
    }

    // ── Overlap ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_overlapping_run_for_the_same_server_is_skipped_not_queued()
    {
        var dashboard = new BlockingBackupDashboard();
        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(new BackupScheduleOptions([Schedule]), logger, dashboard);

        // First run enters CreateAsync and blocks there.
        var first = service.RunServerAsync(Schedule);
        await dashboard.Entered.Task;

        // Second run, same server, while the first is still in flight.
        var second = await service.RunServerAsync(Schedule);

        second.Should().Be(ScheduledBackupOutcome.Skipped);
        dashboard.CreateCalls.Should().Be(1);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("the previous run has not finished"));

        dashboard.Release();
        (await first).Should().Be(ScheduledBackupOutcome.Completed);

        // And the gate is released afterwards, so the next tick is not skipped forever.
        (await service.RunServerAsync(Schedule)).Should().Be(ScheduledBackupOutcome.Completed);
        dashboard.CreateCalls.Should().Be(2);
    }

    [Fact]
    public async Task A_slow_server_does_not_block_a_different_one()
    {
        var dashboard = new BlockingBackupDashboard();
        var other = new ServerBackupSchedule("second-server", TimeSpan.FromHours(1), new RetentionPolicy(1, 1, 1), true);
        var service = new ScheduledBackupService(
            new BackupScheduleOptions([Schedule, other]),
            new RecordingLogger<ScheduledBackupService>(),
            dashboard);

        var first = service.RunServerAsync(Schedule);
        await dashboard.Entered.Task;

        dashboard.Release();

        // The gate is per-server, so the second server is not skipped because the first was busy.
        (await service.RunServerAsync(other)).Should().Be(ScheduledBackupOutcome.Completed);
        await first;
    }

    // ── Failure handling ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_backup_is_logged_at_error_and_does_not_stop_the_scheduler()
    {
        var dashboard = new FakeBackupDashboard
        {
            CreateResult = new BackupCreateResult.Failed("Quiesce command 'save' timed out.", "BackupQuiesceFailedException"),
        };
        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(new BackupScheduleOptions([Schedule]), logger, dashboard);

        var outcome = await service.RunServerAsync(Schedule);

        outcome.Should().Be(ScheduledBackupOutcome.Failed);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Message.Contains("Quiesce command 'save' timed out."));

        // Retention was not attempted: pruning right after failing to write a new archive is the one
        // ordering that leaves an operator with less than they started with.
        dashboard.ApplyPruneCalls.Should().Be(0);

        // And the service is still usable — the failure was reported, not fatal.
        dashboard.CreateResult = null;
        (await service.RunServerAsync(Schedule)).Should().Be(ScheduledBackupOutcome.Completed);
        dashboard.CreateCalls.Should().Be(2);
    }

    [Fact]
    public async Task An_unexpected_exception_is_logged_and_contained()
    {
        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(new BackupScheduleOptions([Schedule]), logger, new ThrowingDashboard());

        var outcome = await service.RunServerAsync(Schedule);

        outcome.Should().Be(ScheduledBackupOutcome.Failed);
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("failed unexpectedly"));
    }

    // ── Foreign artifacts on the scheduled path ───────────────────────────────────────────────────

    [Fact]
    public async Task The_scheduled_path_never_prunes_a_foreign_artifact()
    {
        // Driven over the real BackupDashboardService, so the barrier under test is the production one:
        // the scheduler shares the interactive path's dry-run-then-audit, it does not have its own.
        var provider = new ScriptedBackupProvider()
            .With("servyx-owned", BackupOwnership.Servyx)
            .With("cron-archive", BackupOwnership.Foreign);
        provider.PruneReturns.Add("cron-archive");

        var logger = new RecordingLogger<ScheduledBackupService>();
        var service = new ScheduledBackupService(
            new BackupScheduleOptions([Schedule]),
            logger,
            new BackupDashboardService(provider));

        var outcome = await service.RunServerAsync(Schedule);

        outcome.Should().Be(ScheduledBackupOutcome.Failed);
        provider.LivePruneCalls.Should().Be(0);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error && e.Message.Contains("Foreign artifacts are never pruned"));
    }

    [Fact]
    public async Task A_schedule_that_does_not_prune_creates_and_stops_there()
    {
        var dashboard = new FakeBackupDashboard();
        var schedule = Schedule with { PruneAfterBackup = false };
        var service = new ScheduledBackupService(
            new BackupScheduleOptions([schedule]),
            new RecordingLogger<ScheduledBackupService>(),
            dashboard);

        (await service.RunServerAsync(schedule)).Should().Be(ScheduledBackupOutcome.Completed);

        dashboard.CreateCalls.Should().Be(1);
        dashboard.ApplyPruneCalls.Should().Be(0);
    }

    // ── Due-time evaluation ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tick_before_the_interval_has_elapsed_does_nothing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var dashboard = new FakeBackupDashboard();
        var service = new ScheduledBackupService(
            new BackupScheduleOptions([Schedule]),
            new RecordingLogger<ScheduledBackupService>(),
            dashboard,
            time);

        // The very first RunDueAsync has no recorded due time, so it runs once and schedules the next.
        await service.RunDueAsync();
        dashboard.CreateCalls.Should().Be(1);

        // Half an interval later: not due.
        time.Advance(TimeSpan.FromMinutes(30));
        await service.RunDueAsync();
        dashboard.CreateCalls.Should().Be(1);

        // Past the interval: due again.
        time.Advance(TimeSpan.FromMinutes(31));
        await service.RunDueAsync();
        dashboard.CreateCalls.Should().Be(2);
    }

    private sealed class ThrowingDashboard : IBackupDashboard
    {
        public bool ProviderConfigured => true;

        public Task<BackupListResult> ListAsync(string serverId, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<BackupCreateResult> CreateAsync(string serverId, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<BackupInspectResult> InspectAsync(string backupId, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<RestorePlanResult> PlanRestoreAsync(string backupId, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<RestoreApplyResult> ApplyRestoreAsync(string restorePlanId, int expectedPathCount, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<BackupPruneResult> PreviewPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<BackupPruneResult> ApplyPruneAsync(string serverId, RetentionPolicy policy, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>A minimal manual-advance <see cref="TimeProvider"/>; the test package's own is not referenced here.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
