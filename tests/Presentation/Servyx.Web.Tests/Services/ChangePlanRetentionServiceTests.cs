using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Tests for <see cref="ChangePlanRetentionService"/> and <see cref="ChangePlanRetentionOptions"/> — the
/// scheduled half of the retention work that had to ship alongside apply.
/// </summary>
/// <remarks>
/// The rules themselves live in <c>IChangePlanStore.PurgeImagesAsync</c> and are pinned against the real
/// schema in the persistence suite. What is asserted here is the schedule around them: that a sweep is
/// actually requested, with the configured window and the injected clock's reading, and that a failing sweep
/// does not take the host's background service down with it.
/// </remarks>
public class ChangePlanRetentionServiceTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    // ── Options ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_configuration_at_all_the_sweep_is_enabled_with_the_documented_defaults()
    {
        var options = ChangePlanRetentionOptions.FromConfiguration(Config());

        // Defaults ON, unlike the provisioning-gated options: an install that configured nothing is exactly
        // the install that must not accumulate plaintext secrets forever.
        options.Enabled.Should().BeTrue();
        options.ImageRetention.Should().Be(TimeSpan.FromDays(30));
        options.SweepInterval.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void The_window_and_the_interval_are_both_configurable()
    {
        var options = ChangePlanRetentionOptions.FromConfiguration(Config(
            ("Servyx:ChangePlans:Retention:ImageRetentionDays", "7"),
            ("Servyx:ChangePlans:Retention:SweepMinutes", "15")));

        options.ImageRetention.Should().Be(TimeSpan.FromDays(7));
        options.SweepInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void A_sweep_interval_below_the_floor_is_raised_to_it_rather_than_becoming_a_hot_loop()
    {
        var options = ChangePlanRetentionOptions.FromConfiguration(Config(
            ("Servyx:ChangePlans:Retention:SweepMinutes", "0.001")));

        options.SweepInterval.Should().Be(ChangePlanRetentionOptions.MinimumSweepInterval);
    }

    [Fact]
    public void A_zero_day_window_is_allowed_and_a_negative_one_is_clamped()
    {
        // Zero is a legitimate choice for an operator who wants no revert horizon at all.
        ChangePlanRetentionOptions.FromConfiguration(Config(
            ("Servyx:ChangePlans:Retention:ImageRetentionDays", "0")))
            .ImageRetention.Should().Be(TimeSpan.Zero);

        new ChangePlanRetentionOptions(true, TimeSpan.FromDays(-5), TimeSpan.FromHours(1))
            .ImageRetention.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void The_sweep_can_be_switched_off_explicitly()
    {
        var options = ChangePlanRetentionOptions.FromConfiguration(Config(
            ("Servyx:ChangePlans:Retention:Enabled", "false")));

        options.Enabled.Should().BeFalse();
        Service(options, new RecordingStore()).WillRun.Should().BeFalse();
    }

    // ── The sweep ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_sweep_asks_the_store_once_with_the_configured_window_and_the_injected_clock()
    {
        var store = new RecordingStore();
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var options = new ChangePlanRetentionOptions(true, TimeSpan.FromDays(7), TimeSpan.FromHours(1));

        var result = await Service(options, store, new FixedClock(now)).RunOnceAsync();

        store.Calls.Should().ContainSingle();
        store.Calls[0].Now.Should().Be(now, "the sweep must never read the wall clock itself");
        store.Calls[0].Retention.Should().Be(TimeSpan.FromDays(7));
        result.Should().Be(new ChangePlanImagePurgeResult(1, 2, 3));
    }

    [Fact]
    public async Task A_disabled_sweep_does_not_touch_the_store_at_all()
    {
        var store = new RecordingStore();

        var result = await Service(ChangePlanRetentionOptions.Disabled, store).RunOnceAsync();

        store.Calls.Should().BeEmpty();
        result.Any.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_sweep_is_reported_as_nothing_purged_rather_than_taking_the_service_down()
    {
        var store = new RecordingStore { Failure = new InvalidOperationException("the database is locked") };
        var options = new ChangePlanRetentionOptions(true, TimeSpan.FromDays(30), TimeSpan.FromHours(1));

        var result = await Service(options, store).RunOnceAsync();

        // Keeping data that should have gone is the safe direction, and the next tick tries again. A sweep
        // that threw out of the background service would stop every future sweep too.
        result.Any.Should().BeFalse();
        store.Calls.Should().ContainSingle();
    }

    private static ChangePlanRetentionService Service(
        ChangePlanRetentionOptions options,
        IChangePlanStore store,
        TimeProvider? time = null) =>
        new(options, store, NullLogger<ChangePlanRetentionService>.Instance, time);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingStore : IChangePlanStore
    {
        public List<(DateTimeOffset Now, TimeSpan Retention)> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
            DateTimeOffset now, TimeSpan imageRetention, CancellationToken ct = default)
        {
            Calls.Add((now, imageRetention));

            return Failure is not null
                ? Task.FromException<ChangePlanImagePurgeResult>(Failure)
                : Task.FromResult(new ChangePlanImagePurgeResult(1, 2, 3));
        }

        public Task SaveAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must never write a plan.");

        public Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must never read a single plan.");

        public Task UpdateAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            throw new InvalidOperationException("The retention sweep must never transition a plan itself.");
    }
}
