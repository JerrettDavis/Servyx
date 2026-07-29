using System.Net;
using System.Text.Json;

using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Backups;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Backups;

/// <summary>
/// Barrier 1 in practice: what <see cref="DigitalOceanSnapshotBackupProvider.PruneAsync"/> deletes, and —
/// far more importantly — what it does not.
/// </summary>
/// <remarks>
/// Deleting a DigitalOcean snapshot is irreversible and may be removing the only copy of somebody's saves, so
/// these assert on the substituted account's <em>state</em> and on the exact set of ids the adapter asked
/// DigitalOcean to delete, not merely on the <see cref="PruneResult"/> it returned. A prune that reported the
/// right thing and deleted the wrong one would pass a return-value assertion.
/// </remarks>
public sealed class DigitalOceanSnapshotPruneTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static SnapshotScenario WithFourServyxAndTwoForeign()
    {
        var scenario = new SnapshotScenario();

        scenario.AddServyxSnapshot("700000001", Day.AddDays(-3));
        scenario.AddServyxSnapshot("700000002", Day.AddDays(-2));
        scenario.AddServyxSnapshot("700000003", Day.AddDays(-1));
        scenario.AddServyxSnapshot("700000004", Day);

        scenario.AddForeignSnapshot("900000001", Day.AddDays(-30), "taken-by-hand-before-the-update");
        scenario.AddForeignSnapshot("900000002", Day.AddDays(-29), "restic-nightly-2026-06", tags: "backup");

        return scenario;
    }

    [Fact]
    public async Task A_dry_run_names_only_servyx_snapshots_counts_the_foreign_ones_and_deletes_nothing()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider(new RetentionPolicy(0, 3, 0)).PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: true);

        result.Removed.Should().BeEquivalentTo(["srv-0001::700000001"]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_live_run_deletes_only_servyx_snapshots_and_leaves_every_foreign_one_in_place()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo(["srv-0001::700000001"]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEquivalentTo(["700000001"]);
        scenario.Deleted.Should().NotContain("900000001").And.NotContain("900000002");
        scenario.Snapshots.Select(s => s.Id).Should().Contain(["900000001", "900000002"]);
    }

    /// <summary>
    /// The non-negotiable, stated as directly as it can be: an account of nothing but foreign snapshots, a
    /// retention policy that keeps nothing at all, and a live run. Nothing may be deleted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_prune_that_would_keep_nothing_still_deletes_no_foreign_snapshot(bool dryRun)
    {
        var scenario = new SnapshotScenario();
        scenario.AddForeignSnapshot("900000001", Day.AddDays(-30), "taken-by-hand");
        scenario.AddForeignSnapshot("900000002", Day.AddDays(-29), "another-tools-snapshot", tags: "restic");
        scenario.AddForeignSnapshot("900000003", Day.AddDays(-28), SnapshotOwnership.FormatName("srv-0001", Day.AddDays(-28)));

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(3);
    }

    /// <summary>
    /// A snapshot whose name is Servyx's but whose tags were never applied — the exact artifact a failed
    /// <c>CreateAsync</c> leaves behind. It is foreign, so it survives a prune that keeps nothing.
    /// </summary>
    [Fact]
    public async Task A_servyx_named_but_untagged_snapshot_is_treated_as_foreign_and_survives()
    {
        var scenario = new SnapshotScenario();
        scenario.AddForeignSnapshot("900000009", Day, SnapshotOwnership.FormatName("srv-0001", Day));

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        result.SkippedForeign.Should().Be(1);
        result.Removed.Should().BeEmpty();
        scenario.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshots_of_another_droplet_are_not_this_servers_backups_at_all()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000004", Day);
        scenario.AddServyxSnapshot("700000099", Day.AddDays(-9), dropletId: SnapshotScenario.OtherDropletId);

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 1, 0), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(0);
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Select(s => s.Id).Should().Contain("700000099");
    }

    [Fact]
    public async Task A_null_policy_falls_back_to_the_contexts_default()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider(new RetentionPolicy(0, 3, 0)).PruneAsync("srv-0001", null!, dryRun: true);

        result.Removed.Should().BeEquivalentTo(["srv-0001::700000001"]);
    }

    /// <summary>
    /// A snapshot that vanished provider-side between the listing and the delete. DigitalOcean answers 404;
    /// the snapshot is gone, which is what retention asked for, so it is still reported as removed and no
    /// exception is raised.
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_vanished_before_the_delete_is_reported_removed_not_failed()
    {
        var scenario = WithFourServyxAndTwoForeign();
        scenario.DeleteStatus = HttpStatusCode.NotFound;

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo(["srv-0001::700000001"]);
        scenario.Deleted.Should().BeEquivalentTo(["700000001"]);
    }
}

/// <summary>
/// Taking a snapshot: it costs money, it takes minutes, and it is not a backup until DigitalOcean says so.
/// </summary>
public sealed class DigitalOceanSnapshotCreateTests
{
    [Fact]
    public async Task A_create_polls_the_action_to_completion_before_reporting_a_backup()
    {
        var scenario = new SnapshotScenario { SnapshotActionStatuses = ["in-progress", "in-progress", "completed"] };

        var artifact = await scenario.Provider().CreateAsync("srv-0001");

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Id.Should().Be("srv-0001::800000100");
        artifact.Location.Should().Be("digitalocean://snapshots/800000100");
        artifact.SizeBytes.Should().Be(21474836480L);

        scenario.Api.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/v2/actions/", StringComparison.Ordinal))
            .Should().Be(3, "the adapter must watch the action until DigitalOcean reports it finished");
    }

    [Fact]
    public async Task The_submitted_body_is_a_snapshot_action_carrying_the_servyx_name()
    {
        var scenario = new SnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        var submission = scenario.MutatingRequests.First(r => r.Uri.AbsolutePath.EndsWith("/actions", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(submission.Body!);

        body.RootElement.GetProperty("type").GetString().Should().Be("snapshot");
        body.RootElement.GetProperty("name").GetString().Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
    }

    /// <summary>
    /// The non-negotiable about creation: an action DigitalOcean never reported finished is not a backup, and
    /// is never returned as one.
    /// </summary>
    [Fact]
    public async Task An_action_that_never_completes_is_not_reported_as_a_backup()
    {
        var scenario = new SnapshotScenario { SnapshotActionStatuses = ["in-progress"] };

        var act = async () => await scenario.Provider(actionPollAttempts: 3).CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<SnapshotActionNotConfirmedException>();
        thrown.Which.Submitted.Should().BeTrue();
        thrown.Which.Message.Should().Contain("only submitted is not a snapshot that exists")
            .And.Contain("Do not resubmit blindly");
    }

    [Fact]
    public async Task An_errored_action_is_a_failure_and_a_different_type_from_an_unconfirmed_one()
    {
        var scenario = new SnapshotScenario { SnapshotActionStatuses = ["errored"] };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<SnapshotActionFailedException>();
        thrown.Which.Message.Should().Contain("errored")
            .And.Contain("the droplet was not in a snapshottable state");
    }

    [Fact]
    public async Task A_completed_action_that_produced_no_snapshot_is_not_reported_as_a_backup()
    {
        var scenario = new SnapshotScenario { SnapshotAppearsOnCompletion = false };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        await act.Should().ThrowAsync<SnapshotActionFailedException>()
            .WithMessage("*no new snapshot named*appeared in the account*");
    }

    [Fact]
    public async Task A_snapshot_that_could_not_be_tagged_is_reported_as_billing_and_unmanaged()
    {
        var scenario = new SnapshotScenario { TagApplyStatus = HttpStatusCode.NotFound };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<SnapshotOwnershipNotRecordedException>();
        thrown.Which.SnapshotId.Should().Be("800000100");
        thrown.Which.Message.Should().Contain("retention will NEVER remove this one")
            .And.Contain("per month");
    }

    [Fact]
    public async Task A_snapshot_whose_tags_do_not_show_up_afterwards_is_not_claimed_as_owned()
    {
        var scenario = new SnapshotScenario { TagsStick = false };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        await act.Should().ThrowAsync<SnapshotOwnershipNotRecordedException>()
            .WithMessage("*could not mark it as its own*");
    }

    [Fact]
    public async Task Both_ownership_tags_are_applied_to_the_finished_snapshot()
    {
        var scenario = new SnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        scenario.Snapshots.Single(s => s.Id == "800000100").Tags
            .Should().BeEquivalentTo(["servyx_managed:true", "servyx_instance-id:srv-0001"]);
    }

    [Fact]
    public async Task A_server_with_no_context_is_refused_without_a_single_request()
    {
        var scenario = new SnapshotScenario();

        var act = async () => await scenario.Provider().CreateAsync("srv-does-not-exist");

        await act.Should().ThrowAsync<SnapshotNotFoundException>()
            .WithMessage("*does not know which droplet backs it*");
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_request_carries_a_freshly_resolved_bearer_token()
    {
        var scenario = new SnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        scenario.Api.Requests.Should().OnlyContain(r => r.Authorization == "Bearer " + SnapshotScenario.ApiToken);
        scenario.Secrets.Resolved.Should().HaveCount(scenario.Api.Requests.Count);
    }
}

/// <summary>Listing, inspecting, and what a snapshot honestly cannot tell you.</summary>
public sealed class DigitalOceanSnapshotListTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Listing_labels_each_snapshot_at_the_point_it_is_discovered()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", Day);
        scenario.AddForeignSnapshot("900000001", Day.AddDays(-1));
        scenario.AddServyxSnapshot("700000099", Day, dropletId: SnapshotScenario.OtherDropletId);

        var artifacts = await scenario.Provider().ListAsync("srv-0001");

        artifacts.Should().HaveCount(2);
        artifacts.Single(a => a.Id == "srv-0001::700000001").Ownership.Should().Be(BackupOwnership.Servyx);
        artifacts.Single(a => a.Id == "srv-0001::900000001").Ownership.Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public async Task Inspecting_says_what_the_snapshot_covers_what_it_costs_and_that_there_is_no_file_list()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", Day).SizeGigabytes = 20m;

        var lines = await scenario.Provider().InspectAsync("srv-0001::700000001");

        var text = string.Join("\n", lines);
        text.Should().Contain("entire boot disk")
            .And.Contain("File list: NOT AVAILABLE")
            .And.Contain("$1.20 USD per month")
            .And.Contain("never expires on its own")
            .And.Contain("Ownership: Servyx");

        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspecting_a_foreign_snapshot_says_retention_cannot_reach_it()
    {
        var scenario = new SnapshotScenario();
        scenario.AddForeignSnapshot("900000001", Day);

        var lines = await scenario.Provider().InspectAsync("srv-0001::900000001");

        string.Join("\n", lines).Should().Contain("retention cannot reach it")
            .And.Contain("only a human");
    }

    /// <summary>A snapshot that has vanished provider-side is a "not found", never a silently different one.</summary>
    [Fact]
    public async Task A_snapshot_that_has_vanished_is_reported_as_gone()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", Day);
        var provider = scenario.Provider();

        scenario.Snapshots.Clear();

        var act = async () => await provider.InspectAsync("srv-0001::700000001");

        await act.Should().ThrowAsync<SnapshotNotFoundException>()
            .WithMessage("*may have been deleted in the console, by another tool, or by a prune*");
    }

    [Fact]
    public async Task A_backup_id_this_provider_did_not_issue_is_refused()
    {
        var scenario = new SnapshotScenario();

        var act = async () => await scenario.Provider().InspectAsync("not-an-id");

        await act.Should().ThrowAsync<SnapshotNotFoundException>()
            .WithMessage("*not in a form this provider issued*");
    }

    [Fact]
    public async Task The_storage_cost_is_split_by_ownership_and_never_silently_summed()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", Day).SizeGigabytes = 20m;
        scenario.AddServyxSnapshot("700000002", Day.AddDays(-1)).SizeGigabytes = 20m;
        scenario.AddForeignSnapshot("900000001", Day.AddDays(-2)).SizeGigabytes = 5m;

        var cost = await scenario.Provider().EstimateStorageCostAsync("srv-0001");

        cost.ServyxOwnedCount.Should().Be(2);
        cost.ForeignCount.Should().Be(1);
        cost.ServyxOwnedMonthly.Monthly.Should().Be(2.4m);
        cost.ForeignMonthly.Monthly.Should().Be(0.3m);
        cost.AnySizeUnknown.Should().BeFalse();
    }
}

/// <summary>
/// Restore: a disk-erasing operation, previewed read-only and gated at least as strictly as a rebuild.
/// </summary>
public sealed class DigitalOceanSnapshotRestoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static SnapshotScenario WithOneSnapshot(out string backupId)
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", Day);
        backupId = "srv-0001::700000001";
        return scenario;
    }

    [Fact]
    public async Task Previewing_issues_no_mutating_call_and_states_the_destructive_consequence()
    {
        var scenario = WithOneSnapshot(out var backupId);

        var plan = await scenario.Provider().PlanRestoreAsync(backupId);

        scenario.MutatingRequests.Should().BeEmpty();

        var text = string.Join("\n", plan.AffectedPaths);
        text.Should().Contain("DESTRUCTIVE")
            .And.Contain("ENTIRE boot disk of droplet 3164494")
            .And.Contain("DataImpact.Destroyed")
            .And.Contain("It is not undoable")
            .And.Contain("keeps its id");
    }

    [Fact]
    public async Task The_interface_restore_always_refuses_and_issues_no_request_at_all()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        var act = async () => await ((IBackupProvider)provider).RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<SnapshotRestoreNotAcknowledgedException>()
            .WithMessage("*cannot carry an acknowledgement, so it never restores*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_restore_does_not_burn_the_plan()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        await Assert.ThrowsAsync<SnapshotRestoreNotAcknowledgedException>(
            async () => await ((IBackupProvider)provider).RestoreAsync(plan.Id));

        await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        scenario.MutatingRequests.Should().ContainSingle(r => r.Uri.AbsolutePath.EndsWith("/actions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DataImpact.Preserved)]
    [InlineData(DataImpact.AtRisk)]
    public async Task Anything_short_of_an_exact_destroyed_acknowledgement_is_refused_with_zero_mutating_requests(DataImpact? acknowledged)
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        var act = async () => await provider.RestoreAsync(plan.Id, acknowledged);

        await act.Should().ThrowAsync<SnapshotRestoreNotAcknowledgedException>()
            .WithMessage("*is not an approval of data loss*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_acknowledged_restore_submits_a_restore_action_naming_the_snapshot_image_id()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        var submission = scenario.MutatingRequests.Single();
        submission.Uri.AbsolutePath.Should().Be("/v2/droplets/3164494/actions");

        using var body = JsonDocument.Parse(submission.Body!);
        body.RootElement.GetProperty("type").GetString().Should().Be("restore");
        body.RootElement.GetProperty("image").GetInt64().Should().Be(700000001L);
    }

    [Fact]
    public async Task A_restore_is_polled_to_completion()
    {
        var scenario = WithOneSnapshot(out var backupId);
        scenario.RestoreActionStatuses = ["in-progress", "completed"];
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        scenario.Api.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/v2/actions/", StringComparison.Ordinal))
            .Should().Be(2);
    }

    [Fact]
    public async Task A_restore_still_running_when_the_polls_are_spent_is_not_reported_as_done_or_failed()
    {
        var scenario = WithOneSnapshot(out var backupId);
        scenario.RestoreActionStatuses = ["in-progress"];
        var provider = scenario.Provider(actionPollAttempts: 2);
        var plan = await provider.PlanRestoreAsync(backupId);

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        var thrown = await act.Should().ThrowAsync<SnapshotActionNotConfirmedException>();
        thrown.Which.Submitted.Should().BeTrue();
        thrown.Which.Message.Should().Contain("do NOT resubmit");
    }

    [Fact]
    public async Task An_errored_restore_says_the_disk_may_already_be_overwritten()
    {
        var scenario = WithOneSnapshot(out var backupId);
        scenario.RestoreActionStatuses = ["errored"];
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotActionFailedException>()
            .WithMessage("*treat the machine's contents as lost*");
    }

    [Fact]
    public async Task A_plan_is_single_use()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);
        var before = scenario.MutatingRequests.Count;

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotRestorePlanStaleException>()
            .WithMessage("*unknown or has already been applied*");
        scenario.MutatingRequests.Should().HaveCount(before);
    }

    [Fact]
    public async Task An_expired_plan_is_refused_with_zero_mutating_requests()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider(restorePlanTtl: TimeSpan.FromMinutes(15));
        var plan = await provider.PlanRestoreAsync(backupId);

        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(16);

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotRestorePlanStaleException>()
            .WithMessage("*expired*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>The snapshot was deleted between the preview and the apply. Nothing is restored, and nothing else is.</summary>
    [Fact]
    public async Task A_snapshot_that_vanished_after_the_preview_stops_the_restore()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        scenario.Snapshots.Clear();

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotRestorePlanStaleException>()
            .WithMessage("*no longer exists at DigitalOcean*was NOT touched*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_snapshot_that_changed_after_the_preview_stops_the_restore()
    {
        var scenario = WithOneSnapshot(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);

        scenario.Snapshots.Single().SizeGigabytes = 99m;

        var act = async () => await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotRestorePlanStaleException>()
            .WithMessage("*has changed since restore plan*no disk was erased*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_plan_id_is_refused_with_zero_mutating_requests()
    {
        var scenario = WithOneSnapshot(out _);

        var act = async () => await scenario.Provider().RestoreAsync("restore-nope", DataImpact.Destroyed);

        await act.Should().ThrowAsync<SnapshotRestorePlanStaleException>();
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>A foreign snapshot is restorable — listed, inspectable, restorable, never pruned.</summary>
    [Fact]
    public async Task A_foreign_snapshot_can_still_be_restored_from()
    {
        var scenario = new SnapshotScenario();
        scenario.AddForeignSnapshot("900000001", Day);
        var provider = scenario.Provider();

        var plan = await provider.PlanRestoreAsync("srv-0001::900000001");
        string.Join("\n", plan.AffectedPaths).Should().Contain("Backup ownership: Foreign");

        await provider.RestoreAsync(plan.Id, DataImpact.Destroyed);

        scenario.MutatingRequests.Should().ContainSingle();
    }
}

/// <summary>The opt-in composition, and what registering only the interface actually gets you.</summary>
public sealed class DigitalOceanSnapshotBackupCompositionTests
{
    [Fact]
    public void The_factory_builds_a_backup_provider()
    {
        var scenario = new SnapshotScenario();

        var provider = DigitalOceanSnapshotBackups.Create(
            scenario.Api.Client(),
            scenario.Secrets,
            SnapshotScenario.TokenUrn,
            new SnapshotScenario.StubContextSource(
                new DigitalOceanSnapshotContext("srv-0001", 3164494L, new RetentionPolicy(0, 3, 0))),
            scenario.Clock);

        provider.Should().BeAssignableTo<IBackupProvider>();
    }

    [Fact]
    public async Task A_provider_reached_only_through_the_interface_never_restores()
    {
        var scenario = new SnapshotScenario();
        scenario.AddServyxSnapshot("700000001", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero));

        IBackupProvider provider = DigitalOceanSnapshotBackups.Create(
            scenario.Api.Client(),
            scenario.Secrets,
            SnapshotScenario.TokenUrn,
            new SnapshotScenario.StubContextSource(
                new DigitalOceanSnapshotContext("srv-0001", 3164494L, new RetentionPolicy(0, 3, 0))),
            scenario.Clock);

        var plan = await provider.PlanRestoreAsync("srv-0001::700000001");
        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<SnapshotRestoreNotAcknowledgedException>();
        scenario.MutatingRequests.Should().BeEmpty();
    }
}
