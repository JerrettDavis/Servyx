using Servyx.Domain.Configuration;

namespace Servyx.Composition;

/// <summary>
/// The <see cref="BackgroundService"/> that periodically promotes expired change plans to stale and discards
/// the recorded configuration-file images no plan needs any more.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This service is the reason applying a plan is shippable at all.</strong>
/// <c>ChangePlanActionRecord.PreImageContent</c>/<c>PostImageContent</c> are whole configuration files stored
/// verbatim and unmasked, secrets included; <c>ChangePlanRecord.ExpiresAt</c> was written on every plan from
/// the day the table existed and nothing read it. Without a sweep those rows accumulate forever, which turns
/// Servyx's own database into an ever-growing plaintext credential store. The apply path enforces
/// <c>ExpiresAt</c> at the moment of use; this closes the other half by making expiry and retention
/// eventually consistent facts in storage rather than checks nobody ever runs.
/// </para>
/// <para>
/// <strong>Deliberately a scheduled sweep, not work bolted onto apply.</strong> Purging inside
/// <c>ApplyAsync</c> would only ever clean up on a server somebody happens to be changing, and would make a
/// safety-critical write path also responsible for deleting data. Following
/// <see cref="ScheduledBackupService"/>'s shape instead — a <see cref="PeriodicTimer"/> over an injected
/// <see cref="TimeProvider"/>, a public <see cref="RunOnceAsync"/> so a test drives one sweep with no host
/// and no wall-clock wait, and a failure that is logged rather than ending the service — keeps it a
/// background concern that cannot take an apply down with it.
/// </para>
/// <para>
/// <strong>What it can destroy.</strong> The rules live in <see cref="IChangePlanStore.PurgeImagesAsync"/>,
/// not here; this type only decides when to ask. In short: a plan none of whose actions ever landed loses its
/// images as soon as it is terminal, and a plan that did change something keeps them for
/// <see cref="ChangePlanRetentionOptions.ImageRetention"/> after taking effect and is then unrevertable.
/// </para>
/// </remarks>
public sealed class ChangePlanRetentionService : BackgroundService
{
    private readonly ChangePlanRetentionOptions _options;
    private readonly IChangePlanStore _store;
    private readonly ILogger<ChangePlanRetentionService> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the sweeper.</summary>
    /// <param name="options">The retention window and schedule. A disabled instance makes this a no-op.</param>
    /// <param name="store">The store the sweep is performed through.</param>
    /// <param name="logger">Where sweep results and failures are reported.</param>
    /// <param name="timeProvider">Clock and timer source. Substituted in tests; defaults to the system clock.</param>
    public ChangePlanRetentionService(
        ChangePlanRetentionOptions options,
        IChangePlanStore store,
        ILogger<ChangePlanRetentionService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether this service has anything to do. Public so a composition test can assert a host's answer
    /// without starting one.
    /// </summary>
    public bool WillRun => _options.Enabled;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning(
                "Change plan retention is switched off ({SectionKey}:{EnabledKey} = false). Expired plans "
                + "will not be marked stale and recorded configuration images — which include secret values "
                + "in plaintext — will be kept indefinitely.",
                ChangePlanRetentionOptions.SectionKey,
                ChangePlanRetentionOptions.EnabledKey);
            return;
        }

        _logger.LogInformation(
            "Change plan retention sweeping every {Interval}; images of plans that took effect are kept for "
            + "{Retention} and are then discarded, after which those plans can no longer be reverted.",
            _options.SweepInterval,
            _options.ImageRetention);

        // Swept once at startup as well as on the interval, unlike the backup scheduler's deliberate
        // one-interval delay. The asymmetry is intentional: a backup at every restart is wasted work against
        // a game server, whereas a process that has been down for a week comes back with a week's worth of
        // expired plans still holding plaintext secrets, and there is no reason to hold them an hour longer.
        await RunOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs one sweep. Exposed so a test can drive it without a host and without waiting on wall-clock
    /// time.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the sweep did, or <see cref="ChangePlanImagePurgeResult.Nothing"/> when it is disabled or failed.</returns>
    public async Task<ChangePlanImagePurgeResult> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return ChangePlanImagePurgeResult.Nothing;
        }

        try
        {
            var result = await _store
                .PurgeImagesAsync(_timeProvider.GetUtcNow(), _options.ImageRetention, ct)
                .ConfigureAwait(false);

            if (result.Any)
            {
                _logger.LogInformation(
                    "Change plan retention: {Stale} expired plan(s) marked stale, {Actions} recorded "
                    + "configuration image(s) across {Plans} plan(s) discarded.",
                    result.ExpiredPlansMarkedStale,
                    result.ActionsPurged,
                    result.PlansPurged);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Logged and swallowed so the loop survives, matching ScheduledBackupService. A failed sweep
            // keeps data that should have gone, which is the safe direction: nothing is lost, and the next
            // tick tries again.
            _logger.LogError(ex, "A change plan retention sweep failed. Nothing was purged; it will be retried.");
            return ChangePlanImagePurgeResult.Nothing;
        }
    }
}
