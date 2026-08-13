using Microsoft.Extensions.Logging;
using Servyx.Application.Hosts;
using Servyx.Application.Users;
using Servyx.Composition;
using Servyx.Web.Models;

namespace Servyx.Web.Services;

/// <summary>
/// <see cref="ISettingsDataService"/> implementation backed by the real, already-composed collaborators each
/// section reports on: <see cref="ChangePlanRetentionOptions"/>/<see cref="ChangePlanRetentionService"/> for
/// retention, <see cref="IHostRegistrationService"/> for the host connections summary — the exact same
/// service <c>/hosts</c> itself is built on — and <see cref="IUserService"/> for the signed-in caller's own
/// password.
/// </summary>
/// <remarks>
/// Every collaborator is optional (nullable, default <see langword="null"/>) and every read degrades to an
/// honest "not composed here" <c>Available: false</c> section rather than throwing, mirroring
/// <see cref="LiveDashboardDataService"/>'s own defensiveness: a process that (today, hypothetically) never
/// wired one of these up must still render the other two, and a page render must never fail because one
/// section's backing service is absent.
/// </remarks>
public sealed class LiveSettingsDataService : ISettingsDataService
{
    private readonly ILogger<LiveSettingsDataService> _logger;
    private readonly AuthenticationGate _authenticationGate;
    private readonly ChangePlanRetentionOptions? _retentionOptions;
    private readonly ChangePlanRetentionService? _retentionService;
    private readonly IHostRegistrationService? _hostRegistration;
    private readonly IUserService? _userService;

    /// <summary>Creates a <see cref="LiveSettingsDataService"/>.</summary>
    /// <param name="logger">Where a section read or a mutating action's unexpected failure is logged.</param>
    /// <param name="authenticationGate">
    /// Whether this process requires a login at all — reported alongside the "your password" section
    /// regardless of whether <paramref name="userService"/> is composed, since it is always resolved
    /// (see <see cref="AuthenticationGate"/>'s own fail-closed-default remarks).
    /// </param>
    /// <param name="retentionOptions">
    /// The effective change-plan retention window and schedule, if this process composed one — see
    /// <c>AddServyxCore</c>, which registers this unconditionally today, but the section still degrades to
    /// <c>Available: false</c> rather than assuming that never changes.
    /// </param>
    /// <param name="retentionService">
    /// The already-running sweeper <see cref="ISettingsDataService.RunRetentionSweepAsync"/> asks to sweep
    /// immediately. The same instance the composition root registered as a hosted service — this is not a
    /// second sweeper, and an operator-requested sweep applies exactly the rules the scheduled one does.
    /// </param>
    /// <param name="hostRegistration">
    /// The service backing <c>/hosts</c>. This type only ever calls <see cref="IHostRegistrationService.ListAsync"/>
    /// on it — nothing here registers, probes, or deregisters a host; that stays exclusively on the Hosts page.
    /// </param>
    /// <param name="userService">
    /// The account service the signed-in caller's own password is changed through. Also used, unconditionally,
    /// by the login pipeline — this is the same singleton, not a second instance.
    /// </param>
    public LiveSettingsDataService(
        ILogger<LiveSettingsDataService> logger,
        AuthenticationGate authenticationGate,
        ChangePlanRetentionOptions? retentionOptions = null,
        ChangePlanRetentionService? retentionService = null,
        IHostRegistrationService? hostRegistration = null,
        IUserService? userService = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(authenticationGate);

        _logger = logger;
        _authenticationGate = authenticationGate;
        _retentionOptions = retentionOptions;
        _retentionService = retentionService;
        _hostRegistration = hostRegistration;
        _userService = userService;
    }

    /// <inheritdoc />
    public async Task<SettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        var retention = BuildRetentionSection();
        var hosts = await BuildHostConnectionsSectionAsync(ct).ConfigureAwait(false);
        var credential = await BuildOperatorCredentialSectionAsync(ct).ConfigureAwait(false);

        return new SettingsView([retention, hosts, credential]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ChangePlanRetentionService.RunOnceAsync"/> already catches and logs its own failures,
    /// returning <c>ChangePlanImagePurgeResult.Nothing</c> — the same value a genuinely-empty sweep returns —
    /// so a failure inside the sweep itself is not observable from here and is reported through the
    /// sweeper's own logging, not through <see cref="RetentionSweepResult.Outcome"/>. The try/catch below is
    /// defense in depth for a failure in this method's own plumbing (for instance, a cancelled call), not a
    /// mechanism this method relies on to surface a sweep failure.
    /// </remarks>
    public async Task<RetentionSweepResult> RunRetentionSweepAsync(CancellationToken ct = default)
    {
        if (_retentionOptions is null || _retentionService is null)
        {
            return RetentionSweepResult.Unavailable;
        }

        if (!_retentionOptions.Enabled)
        {
            return RetentionSweepResult.Disabled;
        }

        try
        {
            var result = await _retentionService.RunOnceAsync(ct).ConfigureAwait(false);
            return new RetentionSweepResult(
                RetentionSweepOutcome.Swept,
                result.ExpiredPlansMarkedStale,
                result.PlansPurged,
                result.ActionsPurged,
                Detail: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An operator-requested retention sweep failed.");
            return new RetentionSweepResult(RetentionSweepOutcome.Failed, 0, 0, 0, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<OperatorPasswordChangeResult> ChangeOperatorPasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (_userService is null)
        {
            return OperatorPasswordChangeResult.Unavailable;
        }

        try
        {
            var result = await _userService
                .ChangePasswordAsync(username, currentPassword, newPassword, ct)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                ChangePasswordOutcome.Changed => OperatorPasswordChangeResult.Changed,
                ChangePasswordOutcome.WeakPassword =>
                    new OperatorPasswordChangeResult(OperatorPasswordChangeOutcome.NewPasswordRejected, result.Detail),
                _ => OperatorPasswordChangeResult.CurrentPasswordIncorrect,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Own-password rotation failed.");
            return new OperatorPasswordChangeResult(OperatorPasswordChangeOutcome.Failed, ex.Message);
        }
    }

    private RetentionSettingsSection BuildRetentionSection() =>
        _retentionOptions is null
            ? RetentionSettingsSection.Unavailable(ChangePlanRetentionOptions.SectionKey)
            : new RetentionSettingsSection(
                Available: true,
                _retentionOptions.Enabled,
                _retentionOptions.ImageRetention,
                _retentionOptions.SweepInterval,
                ChangePlanRetentionOptions.SectionKey);

    private async Task<HostConnectionsSettingsSection> BuildHostConnectionsSectionAsync(CancellationToken ct)
    {
        if (_hostRegistration is null)
        {
            return HostConnectionsSettingsSection.Unavailable;
        }

        try
        {
            var result = await _hostRegistration.ListAsync(ct).ConfigureAwait(false);
            if (result.ListingFailed)
            {
                return new HostConnectionsSettingsSection(
                    Available: true, RegisteredCount: 0, EnabledCount: 0, ListingFailed: true, result.FailureDetail);
            }

            return new HostConnectionsSettingsSection(
                Available: true,
                RegisteredCount: result.Hosts.Count,
                EnabledCount: result.Hosts.Count(h => h.Enabled),
                ListingFailed: false,
                FailureDetail: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Last line of defense — IHostRegistrationService.ListAsync itself never throws for an ordinary
            // read failure (see RegisteredHostsResult), but a degraded settings section must not depend on
            // every implementation honoring that, mirroring LiveDashboardDataService.GetServersWithStatusAsync.
            _logger.LogWarning(ex, "Failed to summarize registered hosts for the settings page.");
            return new HostConnectionsSettingsSection(
                Available: true, RegisteredCount: 0, EnabledCount: 0, ListingFailed: true, ex.Message);
        }
    }

    private Task<OperatorCredentialSettingsSection> BuildOperatorCredentialSectionAsync(CancellationToken ct)
    {
        _ = ct; // No read is needed: reaching this page at all requires an authenticated account.

        if (_userService is null)
        {
            return Task.FromResult(OperatorCredentialSettingsSection.Unavailable(
                _authenticationGate.Enabled, AuthenticationGate.ConfigurationKey));
        }

        // PasswordSet is always true here — see OperatorCredentialSettingsSection's own remarks: an
        // authenticated caller already has a password by construction, so there is no "not set yet" state to
        // discover for a self-service rotation the way there was for the single shared operator password.
        return Task.FromResult(new OperatorCredentialSettingsSection(
            Available: true,
            _authenticationGate.Enabled,
            PasswordSet: true,
            CreateUserResult.MinimumPasswordLength,
            AuthenticationGate.ConfigurationKey));
    }
}
