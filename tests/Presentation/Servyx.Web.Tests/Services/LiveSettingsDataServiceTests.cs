using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Application.Hosts;
using Servyx.Composition;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Web.Authentication;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Tests for <see cref="LiveSettingsDataService"/> — the real-collaborator implementation behind the
/// <c>/settings</c> page's <see cref="ISettingsDataService"/>.
/// </summary>
/// <remarks>
/// Two claims run through this file: every section degrades to <c>Available: false</c> — never an exception —
/// when its backing collaborator was not composed, and every mutating member is a thin, honest pass-through to
/// the same collaborator the rest of the app already uses (the retention sweeper, <c>IHostRegistrationService</c>,
/// <c>OperatorCredentialStore</c>) rather than a second implementation of any of their rules.
/// </remarks>
public class LiveSettingsDataServiceTests
{
    // ── Availability: nothing composed ───────────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_collaborators_composed_every_section_reports_unavailable()
    {
        var service = new LiveSettingsDataService(NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced);

        var view = await service.GetSettingsAsync();

        view.Find<RetentionSettingsSection>()!.Available.Should().BeFalse();
        view.Find<HostConnectionsSettingsSection>()!.Available.Should().BeFalse();
        view.Find<OperatorCredentialSettingsSection>()!.Available.Should().BeFalse();
    }

    [Fact]
    public async Task With_nothing_composed_a_sweep_request_reports_unavailable_rather_than_throwing()
    {
        var service = new LiveSettingsDataService(NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced);

        var result = await service.RunRetentionSweepAsync();

        result.Outcome.Should().Be(RetentionSweepOutcome.Unavailable);
    }

    [Fact]
    public async Task With_nothing_composed_a_password_change_reports_unavailable_rather_than_throwing()
    {
        var service = new LiveSettingsDataService(NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced);

        var result = await service.ChangeOperatorPasswordAsync("anything", "brand-new-password-1");

        result.Outcome.Should().Be(OperatorPasswordChangeOutcome.Unavailable);
    }

    // ── Retention section ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_retention_section_reports_the_composed_options_verbatim()
    {
        var options = new ChangePlanRetentionOptions(true, TimeSpan.FromDays(14), TimeSpan.FromMinutes(30));
        var store = new RecordingChangePlanStore();
        var retentionService = new ChangePlanRetentionService(options, store, NullLogger<ChangePlanRetentionService>.Instance);
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, options, retentionService);

        var section = (await service.GetSettingsAsync()).Find<RetentionSettingsSection>()!;

        section.Available.Should().BeTrue();
        section.Enabled.Should().BeTrue();
        section.ImageRetention.Should().Be(TimeSpan.FromDays(14));
        section.SweepInterval.Should().Be(TimeSpan.FromMinutes(30));
        section.ConfigurationSectionKey.Should().Be(ChangePlanRetentionOptions.SectionKey);
    }

    [Fact]
    public async Task An_operator_requested_sweep_runs_the_same_sweeper_and_reports_what_it_did()
    {
        var options = new ChangePlanRetentionOptions(true, TimeSpan.FromDays(7), TimeSpan.FromHours(1));
        var store = new RecordingChangePlanStore { Result = new ChangePlanImagePurgeResult(1, 2, 3) };
        var retentionService = new ChangePlanRetentionService(options, store, NullLogger<ChangePlanRetentionService>.Instance);
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, options, retentionService);

        var result = await service.RunRetentionSweepAsync();

        result.Outcome.Should().Be(RetentionSweepOutcome.Swept);
        result.PlansMarkedStale.Should().Be(1);
        result.PlansPurged.Should().Be(2);
        result.ActionsPurged.Should().Be(3);
        store.Calls.Should().ContainSingle("the settings service must drive the real sweeper, not a second implementation");
    }

    [Fact]
    public async Task A_sweep_request_while_retention_is_disabled_in_configuration_does_nothing()
    {
        var options = ChangePlanRetentionOptions.Disabled;
        var store = new RecordingChangePlanStore();
        var retentionService = new ChangePlanRetentionService(options, store, NullLogger<ChangePlanRetentionService>.Instance);
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, options, retentionService);

        var result = await service.RunRetentionSweepAsync();

        result.Outcome.Should().Be(RetentionSweepOutcome.Disabled);
        store.Calls.Should().BeEmpty("a disabled sweep must never touch the store, even on an explicit request");
    }

    // ── Host connections section ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_host_section_counts_registered_and_enabled_hosts()
    {
        var hosts = new FakeHostRegistrationService();
        hosts.Hosts.Add(new RegisteredHost(HostId.New(), "alpha", "10.0.0.4:22", "requirePinned", "SHA256:aaa", Enabled: true, "operator", DateTimeOffset.UtcNow));
        hosts.Hosts.Add(new RegisteredHost(HostId.New(), "beta", "10.0.0.5:22", "requirePinned", "SHA256:bbb", Enabled: false, "operator", DateTimeOffset.UtcNow));

        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, hostRegistration: hosts);

        var section = (await service.GetSettingsAsync()).Find<HostConnectionsSettingsSection>()!;

        section.Available.Should().BeTrue();
        section.RegisteredCount.Should().Be(2);
        section.EnabledCount.Should().Be(1);
        section.ListingFailed.Should().BeFalse();
    }

    [Fact]
    public async Task A_failed_host_listing_is_reported_as_failed_rather_than_an_empty_count()
    {
        var hosts = new FakeHostRegistrationService { ListingFailureDetail = "the database is locked" };
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, hostRegistration: hosts);

        var section = (await service.GetSettingsAsync()).Find<HostConnectionsSettingsSection>()!;

        section.Available.Should().BeTrue();
        section.ListingFailed.Should().BeTrue();
        section.FailureDetail.Should().Be("the database is locked");
        section.RegisteredCount.Should().Be(0, "a failed read must never be reported as a genuinely empty one");
    }

    [Fact]
    public async Task A_host_listing_that_throws_outright_still_degrades_to_a_failed_section()
    {
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance,
            AuthenticationGate.Enforced,
            hostRegistration: new ThrowingHostRegistrationService());

        var section = (await service.GetSettingsAsync()).Find<HostConnectionsSettingsSection>()!;

        section.ListingFailed.Should().BeTrue();
        section.FailureDetail.Should().Contain("the host table is gone");
    }

    // ── Operator credential section and rotation ─────────────────────────────────────────────────

    [Fact]
    public async Task A_fresh_install_reports_no_password_set_so_the_page_never_offers_a_rotation_form()
    {
        // Rotation requires the current password; bootstrapping the first one is /login's one-time flow. The
        // section has to say which state the install is in for the page to render the right one of the two.
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance,
            AuthenticationGate.Enforced,
            operatorCredentials: new OperatorCredentialStore(new RecordingSecretStore()));

        var section = (await service.GetSettingsAsync()).Find<OperatorCredentialSettingsSection>()!;

        section.Available.Should().BeTrue();
        section.PasswordSet.Should().BeFalse();
    }

    [Fact]
    public async Task A_rotation_on_an_install_with_no_password_yet_is_indistinguishable_from_a_wrong_one()
    {
        // Deliberate: a distinct outcome here would let this form be used to probe whether an install has
        // been bootstrapped, and TrySetInitialPasswordAsync stays the only way a first password is ever set.
        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance,
            AuthenticationGate.Enforced,
            operatorCredentials: new OperatorCredentialStore(new RecordingSecretStore()));

        var result = await service.ChangeOperatorPasswordAsync("anything-at-all", "brand-new-password-1");

        result.Outcome.Should().Be(OperatorPasswordChangeOutcome.CurrentPasswordIncorrect);
    }


    [Fact]
    public async Task The_operator_section_reports_whether_a_password_is_set_and_the_authentication_gates_state()
    {
        var secrets = new RecordingSecretStore();
        var credentials = new OperatorCredentialStore(secrets);
        await credentials.TrySetInitialPasswordAsync("correct-horse-battery-staple");

        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Disabled, operatorCredentials: credentials);

        var section = (await service.GetSettingsAsync()).Find<OperatorCredentialSettingsSection>()!;

        section.Available.Should().BeTrue();
        section.PasswordSet.Should().BeTrue();
        section.AuthenticationEnabled.Should().BeFalse();
        section.MinimumPasswordLength.Should().Be(OperatorCredentialStore.MinimumPasswordLength);
        section.AuthenticationConfigurationKey.Should().Be(AuthenticationGate.ConfigurationKey);
    }

    [Fact]
    public async Task Changing_the_password_delegates_to_the_credential_store_and_verifies_the_current_one()
    {
        var secrets = new RecordingSecretStore();
        var credentials = new OperatorCredentialStore(secrets);
        await credentials.TrySetInitialPasswordAsync("correct-horse-battery-staple");

        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, operatorCredentials: credentials);

        (await service.ChangeOperatorPasswordAsync("wrong-password", "brand-new-password-1")).Outcome
            .Should().Be(OperatorPasswordChangeOutcome.CurrentPasswordIncorrect);

        (await credentials.VerifyPasswordAsync("correct-horse-battery-staple")).Should().BeTrue(
            "a refused rotation must not touch the stored credential");

        var changed = await service.ChangeOperatorPasswordAsync("correct-horse-battery-staple", "brand-new-password-1");
        changed.Outcome.Should().Be(OperatorPasswordChangeOutcome.Changed);
        (await credentials.VerifyPasswordAsync("brand-new-password-1")).Should().BeTrue();
    }

    [Fact]
    public async Task A_too_short_new_password_is_reported_as_rejected_rather_than_throwing_through_the_page()
    {
        var secrets = new RecordingSecretStore();
        var credentials = new OperatorCredentialStore(secrets);
        await credentials.TrySetInitialPasswordAsync("correct-horse-battery-staple");

        var service = new LiveSettingsDataService(
            NullLogger<LiveSettingsDataService>.Instance, AuthenticationGate.Enforced, operatorCredentials: credentials);

        var result = await service.ChangeOperatorPasswordAsync("correct-horse-battery-staple", "short");

        result.Outcome.Should().Be(OperatorPasswordChangeOutcome.NewPasswordRejected);
        result.Detail.Should().NotBeNullOrWhiteSpace();
        (await credentials.VerifyPasswordAsync("correct-horse-battery-staple")).Should().BeTrue();
    }

    // ── Test doubles ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IHostRegistrationService.ListAsync"/> reports an ordinary read failure through
    /// <see cref="RegisteredHostsResult"/> rather than throwing, so this covers the last line of defense: an
    /// implementation that does not honor that contract must still leave the settings page renderable.
    /// </summary>
    private sealed class ThrowingHostRegistrationService : IHostRegistrationService
    {
        public Task<HostProbeResult> ProbeAsync(string endpoint, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");

        public Task<RegistrationResult> RegisterAsync(
            string name, string endpoint, string confirmedFingerprint, ReadOnlyMemory<byte> privateKeyBytes,
            string? passphrase, string actor, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");

        public Task<RegisteredHostsResult> ListAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the host table is gone");

        public Task<DeregistrationResult> DeregisterAsync(string name, string actor, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");
    }

    private sealed class RecordingChangePlanStore : IChangePlanStore
    {
        public List<(DateTimeOffset Now, TimeSpan Retention)> Calls { get; } = [];

        public ChangePlanImagePurgeResult Result { get; set; } = ChangePlanImagePurgeResult.Nothing;

        public Task<ChangePlanImagePurgeResult> PurgeImagesAsync(
            DateTimeOffset now, TimeSpan imageRetention, CancellationToken ct = default)
        {
            Calls.Add((now, imageRetention));
            return Task.FromResult(Result);
        }

        public Task SaveAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");

        public Task<StoredChangePlan?> TryGetAsync(ChangePlanId id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");

        public Task UpdateAsync(
            ChangePlanRecord plan, IReadOnlyList<ChangePlanActionRecord> actions, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");

        public Task<IReadOnlyList<ChangePlanSummary>> ListRecentAsync(
            ServerId serverId, int limit, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not exercised by these tests.");
    }
}
