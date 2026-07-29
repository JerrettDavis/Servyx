using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;

using Servyx.Infrastructure.Azure.Backups;

namespace Servyx.Infrastructure.Azure.Tests.Backups;

/// <summary>
/// The Azure managed-disk snapshot backup provider, exercised against a substituted subscription.
/// </summary>
/// <remarks>
/// Nothing here opens a socket or needs an Azure account: every request goes through
/// <c>AzureArmApiDouble</c>, which is also what lets a test claiming "no mutating request was issued" prove it
/// rather than infer it from a return value.
/// </remarks>
public sealed class AzureSnapshotBackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------------------------------
    // Multi-disk behaviour and the consistency caveat
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_backup_covers_every_attached_managed_disk_not_just_the_os_disk()
    {
        var scenario = new AzureSnapshotScenario();

        var artifact = await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        var writes = scenario.MutatingRequests.Where(r => r.Method == HttpMethod.Put).ToList();
        writes.Should().HaveCount(2, "the machine has an OS disk and one data disk, and the saves are on the data disk");

        writes.Select(w => w.Body ?? string.Empty).Should().Contain(
            b => b.Contains(AzureSnapshotScenario.DiskId(AzureSnapshotScenario.OsDiskName), StringComparison.Ordinal));
        writes.Select(w => w.Body ?? string.Empty).Should().Contain(
            b => b.Contains(AzureSnapshotScenario.DiskId(AzureSnapshotScenario.DataDiskName), StringComparison.Ordinal));

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Id.Should().Be(AzureSnapshotScenario.SetBackupId(Now));
    }

    [Fact]
    public async Task Each_snapshot_write_names_exactly_one_source_disk_which_is_why_the_set_is_not_atomic()
    {
        var scenario = new AzureSnapshotScenario();

        await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        foreach (var write in scenario.MutatingRequests.Where(r => r.Method == HttpMethod.Put))
        {
            var body = write.Body ?? string.Empty;
            Occurrences(body, "sourceResourceId").Should().Be(
                1,
                "Microsoft.Compute/snapshots takes exactly one source disk — there is no plural form of this call, "
                + "which is the whole reason an Azure multi-disk capture cannot be one instant");
        }
    }

    [Fact]
    public async Task Inspecting_a_multi_disk_set_states_plainly_that_it_is_not_a_consistent_point_in_time()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var lines = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        lines.Should().Contain(l => l.Contains("NOT A CONSISTENT POINT IN TIME", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("SEPARATE ARM operations at DIFFERENT instants", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("no atomic multi-disk snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspecting_a_single_disk_set_says_crash_consistent_rather_than_claiming_a_problem_it_does_not_have()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.Disks.RemoveAll(d => d.Lun is not null);
        scenario.AddServyxSet(Now);

        var lines = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        lines.Should().Contain(l => l.Contains("CRASH-CONSISTENT for this machine's single disk", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.Contains("NOT A CONSISTENT POINT IN TIME", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspection_names_what_the_backup_does_not_cover_and_refuses_to_invent_a_file_list()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var lines = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        lines.Should().Contain(l => l.Contains("File list: NOT AVAILABLE", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("temporary/resource disk", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("NOT application-consistent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_machine_whose_os_disk_is_unmanaged_is_refused_rather_than_partially_captured()
    {
        var scenario = new AzureSnapshotScenario { OsDiskIsManaged = false };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        (await act.Should().ThrowAsync<AzureSnapshotFailedException>())
            .WithMessage("*not managed disks*");

        scenario.MutatingRequests.Should().BeEmpty("nothing was created, so nothing is billing");
    }

    [Fact]
    public async Task A_machine_with_no_managed_disks_says_so_and_names_what_can_never_be_captured()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.Disks.Clear();

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        (await act.Should().ThrowAsync<AzureSnapshotFailedException>())
            .WithMessage("*ephemeral OS disk or the temporary resource disk*");

        scenario.MutatingRequests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Submission is not success
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_reads_every_snapshot_back_after_writing_it_and_never_trusts_the_write()
    {
        var scenario = new AzureSnapshotScenario();

        await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        var requests = scenario.Api.ArmRequests;

        foreach (var write in requests.Where(r => r.Method == HttpMethod.Put))
        {
            var writeIndex = requests.ToList().FindIndex(r => ReferenceEquals(r, write));
            var readsAfter = requests
                .Skip(writeIndex + 1)
                .Count(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath == write.Uri.AbsolutePath);

            readsAfter.Should().BeGreaterThanOrEqualTo(
                2,
                "the ARM operation poll and the incremental copy poll are two separate finish lines, and neither "
                + "may be skipped");
        }
    }

    [Fact]
    public async Task A_snapshot_arm_never_reports_finished_is_not_reported_as_a_backup()
    {
        var scenario = new AzureSnapshotScenario { CreatedProvisioningStates = ["Creating"] };

        var act = async () => await scenario.Provider(snapshotPollAttempts: 2)
            .CreateAsync(AzureSnapshotScenario.ServerId);

        var thrown = await act.Should().ThrowAsync<AzureSnapshotNotConfirmedException>();

        thrown.Which.Submitted.Should().BeTrue();
        thrown.Which.SnapshotNames.Should().NotBeNull().And.NotBeEmpty(
            "the snapshots exist and are billing — the most damaging thing this could do is imply nothing was created");
        thrown.Which.Message.Should().Contain("only submitted is not a backup that exists");
        thrown.Which.Message.Should().Contain("Do not resubmit blindly");
    }

    [Fact]
    public async Task A_snapshot_provisioned_but_still_copying_is_not_reported_as_a_backup()
    {
        // The finish line with no EBS analogue: ARM says Succeeded while the incremental copy runs on.
        var scenario = new AzureSnapshotScenario { CreatedCompletionPercents = [40d] };

        var act = async () => await scenario.Provider(snapshotPollAttempts: 2)
            .CreateAsync(AzureSnapshotScenario.ServerId);

        var thrown = await act.Should().ThrowAsync<AzureSnapshotNotConfirmedException>();

        thrown.Which.Message.Should().Contain("provisioned but still copying");
        thrown.Which.Message.Should().Contain("completionPercent reaches 100");
        thrown.Which.SnapshotNames.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_create_waits_for_the_incremental_copy_and_then_succeeds()
    {
        var scenario = new AzureSnapshotScenario { CreatedCompletionPercents = [40d, 40d, 100d] };

        var artifact = await scenario.Provider(snapshotPollAttempts: 5)
            .CreateAsync(AzureSnapshotScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        scenario.Snapshots.Should().OnlyContain(s => s.Reads >= 3);
    }

    [Fact]
    public async Task An_arm_operation_that_fails_is_a_different_answer_from_one_that_is_still_running()
    {
        var scenario = new AzureSnapshotScenario { CreatedProvisioningStates = ["Failed"] };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        (await act.Should().ThrowAsync<AzureSnapshotFailedException>())
            .WithMessage("*does NOT report a partial set as a backup*");
    }

    [Fact]
    public async Task A_write_azure_refuses_partway_names_the_snapshots_that_already_exist_and_bill()
    {
        var scenario = new AzureSnapshotScenario { DisksCoveredByCreate = 1 };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        var thrown = await act.Should().ThrowAsync<AzureSnapshotFailedException>();

        thrown.Which.SnapshotNames.Should().HaveCount(1);
        thrown.Which.Message.Should().Contain("DO exist and ARE billing");
        thrown.Which.Message.Should().Contain("set is INCOMPLETE");
        thrown.Which.InnerException.Should().BeOfType<AzureApiException>("Azure's own refusal must not be lost");
    }

    [Fact]
    public async Task Every_snapshot_write_asks_for_an_incremental_snapshot()
    {
        var scenario = new AzureSnapshotScenario();

        await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        // The scenario's router fails the test outright on a write that omits it, so reaching here is already
        // the assertion; this makes the claim visible at the call site too.
        scenario.MutatingRequests
            .Where(r => r.Method == HttpMethod.Put)
            .Should()
            .OnlyContain(r => r.Body!.Contains("\"incremental\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_created_snapshot_carries_all_four_ownership_marks()
    {
        var scenario = new AzureSnapshotScenario();

        await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        foreach (var snapshot in scenario.Snapshots)
        {
            AzureSnapshotOwnership
                .Classify(
                    snapshot.Tags,
                    AzureSnapshotScenario.ServerId,
                    AzureSnapshotScenario.ResourceGroup,
                    AzureSnapshotScenario.VmName)
                .Should()
                .Be(BackupOwnership.Servyx);
        }
    }

    [Fact]
    public async Task A_snapshot_created_without_its_tags_is_refused_as_unmanaged_rather_than_reported_as_a_backup()
    {
        var scenario = new AzureSnapshotScenario { TagsStick = false };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        var thrown = await act.Should().ThrowAsync<AzureSnapshotOwnershipNotRecordedException>();

        thrown.Which.Message.Should().Contain("retention will NEVER remove these");
        thrown.Which.SnapshotNames.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_snapshot_that_vanishes_between_the_write_and_the_read_is_handled_honestly()
    {
        var scenario = new AzureSnapshotScenario { SnapshotVanishesAfterCreate = true };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        (await act.Should().ThrowAsync<AzureSnapshotFailedException>())
            .WithMessage("*vanished between the write and a read of it*");
    }

    [Fact]
    public async Task A_machine_azure_no_longer_reports_says_the_snapshots_of_it_still_exist_and_still_bill()
    {
        var scenario = new AzureSnapshotScenario { MachineExists = false };

        var act = async () => await scenario.Provider().CreateAsync(AzureSnapshotScenario.ServerId);

        (await act.Should().ThrowAsync<AzureSnapshotNotFoundException>())
            .WithMessage("*still exists, and still bills*");

        scenario.MutatingRequests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Listing
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Servyx_snapshots_are_grouped_into_one_artifact_per_set_and_foreign_ones_stand_alone()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);
        scenario.AddForeignSnapshot("hand-taken-before-the-update", Now.AddHours(-1));

        var listed = await scenario.Provider().ListAsync(AzureSnapshotScenario.ServerId);

        listed.Should().HaveCount(2);
        listed.Count(a => a.Ownership == BackupOwnership.Servyx).Should().Be(1);
        listed.Count(a => a.Ownership == BackupOwnership.Foreign).Should().Be(1);
    }

    [Fact]
    public async Task A_servyx_snapshot_of_a_disk_that_has_since_been_detached_is_still_found()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);
        scenario.Disks.RemoveAll(d => d.Lun is not null);

        var listed = await scenario.Provider().ListAsync(AzureSnapshotScenario.ServerId);

        listed.Should().ContainSingle(a => a.Ownership == BackupOwnership.Servyx);
        listed.Single(a => a.Ownership == BackupOwnership.Servyx).Id.Should().Be(
            AzureSnapshotScenario.SetBackupId(Now),
            "a listing keyed on live attachments would lose these, and Servyx never prunes what it cannot see");
    }

    [Fact]
    public async Task A_servyx_snapshot_belonging_to_another_server_is_never_this_servers_backup()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now, serverId: "srv-9999");

        var listed = await scenario.Provider().ListAsync(AzureSnapshotScenario.ServerId);

        listed.Should().OnlyContain(a => a.Ownership == BackupOwnership.Foreign);
    }

    [Fact]
    public async Task Listing_does_not_mutate_anything()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);
        scenario.AddForeignSnapshot("hand-taken", Now);

        await scenario.Provider().ListAsync(AzureSnapshotScenario.ServerId);

        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Listing_a_server_with_no_configured_context_is_a_not_found_rather_than_an_empty_list()
    {
        var scenario = new AzureSnapshotScenario();

        var act = async () => await scenario.Provider().ListAsync("srv-unknown");

        (await act.Should().ThrowAsync<AzureSnapshotNotFoundException>())
            .WithMessage("*does not know which virtual machine backs it*");
    }

    [Fact]
    public async Task A_backup_id_that_no_longer_resolves_is_a_not_found()
    {
        var scenario = new AzureSnapshotScenario();

        var act = async () => await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        (await act.Should().ThrowAsync<AzureSnapshotNotFoundException>())
            .WithMessage("*may have been deleted in the portal*");
    }

    [Fact]
    public async Task A_set_missing_one_of_its_snapshots_is_reported_with_the_coverage_mismatch_stated()
    {
        var scenario = new AzureSnapshotScenario();
        var set = scenario.AddServyxSet(Now);
        scenario.Snapshots.Remove(set[1]);

        var lines = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        lines.Should().Contain(l => l.Contains("THE COUNTS DIFFER", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------------
    // Prune: foreign snapshots are never deleted, under any dryRun value
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_prune_never_schedules_a_foreign_snapshot_and_issues_no_mutating_call()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddDays(-9));
        scenario.AddServyxSet(Now);
        var foreign = scenario.AddForeignSnapshot("hand-taken-precious", Now.AddDays(-30));

        var result = await scenario.Provider(new RetentionPolicy(0, 1, 0))
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 1, 0), dryRun: true);

        result.SkippedForeign.Should().Be(1);
        result.Removed.Should().NotContain(id => id.Contains(foreign.Name, StringComparison.Ordinal));
        result.Removed.Should().ContainSingle().Which.Should().Be(AzureSnapshotScenario.SetBackupId(Now.AddDays(-9)));

        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Should().Contain(foreign);
    }

    [Fact]
    public async Task A_live_prune_never_deletes_a_foreign_snapshot()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddDays(-9));
        scenario.AddServyxSet(Now);
        var foreign = scenario.AddForeignSnapshot("hand-taken-precious", Now.AddDays(-30));

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 1, 0), dryRun: false);

        result.SkippedForeign.Should().Be(1);
        scenario.Deleted.Should().NotContain(foreign.Name);
        scenario.Snapshots.Should().Contain(foreign, "Servyx never deletes a snapshot it did not create");
        scenario.Deleted.Should().HaveCount(2, "the released set had two members and they go together or not at all");
    }

    [Fact]
    public async Task An_entire_account_of_foreign_snapshots_is_reported_and_nothing_is_deleted()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddForeignSnapshot("azure-backup-001", Now.AddDays(-40));
        scenario.AddForeignSnapshot("azure-backup-002", Now.AddDays(-30), AzureSnapshotScenario.DataDiskName);
        scenario.AddForeignSnapshot("by-hand-003", Now.AddDays(-20));

        foreach (var dryRun in new[] { true, false })
        {
            var result = await scenario.Provider()
                .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 0, 0), dryRun);

            result.SkippedForeign.Should().Be(3);
            result.Removed.Should().BeEmpty();
        }

        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_snapshot_carrying_servyx_tags_for_a_different_machine_is_skipped_as_foreign_not_deleted()
    {
        var scenario = new AzureSnapshotScenario();
        var setName = AzureSnapshotOwnership.FormatSetName(AzureSnapshotScenario.ServerId, Now.AddDays(-5));
        var impostor = scenario.AddForeignSnapshot("looks-servyx-shaped", Now.AddDays(-5));

        foreach (var tag in AzureSnapshotScenario.ServyxSnapshotTags(
                     setName,
                     AzureSnapshotScenario.OsDiskName,
                     vmName: AzureSnapshotScenario.OtherVmName))
        {
            impostor.Tags[tag.Key] = tag.Value;
        }

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 0, 0), dryRun: false);

        result.SkippedForeign.Should().Be(1);
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Should().Contain(impostor);
    }

    [Fact]
    public void Retention_refuses_to_even_evaluate_a_foreign_artifact()
    {
        var foreign = new BackupArtifact(
            "srv-0001::snapshot:hand-taken",
            BackupOwnership.Foreign,
            Now,
            0,
            "azure://compute/sub/rg/snapshots/hand-taken");

        var act = () => AzureSnapshotRetentionEvaluator.SelectForRemoval([foreign], new RetentionPolicy(0, 0, 0));

        act.Should().Throw<ForeignAzureSnapshotProtectedException>()
            .WithMessage("*must never be evaluated against a Servyx retention policy*");
    }

    [Fact]
    public async Task A_snapshot_that_has_already_vanished_provider_side_is_still_reported_as_removed()
    {
        var scenario = new AzureSnapshotScenario { DeleteStatus = System.Net.HttpStatusCode.NotFound };
        scenario.AddServyxSet(Now.AddDays(-9));
        scenario.AddServyxSet(Now);

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 1, 0), dryRun: false);

        result.Removed.Should().ContainSingle().Which.Should().Be(AzureSnapshotScenario.SetBackupId(Now.AddDays(-9)));
    }

    // -----------------------------------------------------------------------------------------------
    // Retention
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retention_keeps_the_newest_capture_of_each_of_the_most_recent_days()
    {
        var scenario = new AzureSnapshotScenario();
        for (var day = 0; day < 5; day++)
        {
            scenario.AddServyxSet(Now.AddDays(-day));
        }

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 3, 0), dryRun: true);

        result.Removed.Should().BeEquivalentTo(
        [
            AzureSnapshotScenario.SetBackupId(Now.AddDays(-4)),
            AzureSnapshotScenario.SetBackupId(Now.AddDays(-3)),
        ]);
    }

    [Fact]
    public async Task A_second_capture_in_the_same_day_fills_the_same_daily_bucket_rather_than_a_second_one()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddDays(-2));
        scenario.AddServyxSet(Now.AddHours(-4));
        scenario.AddServyxSet(Now);

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 2, 0), dryRun: true);

        result.Removed.Should().ContainSingle().Which.Should().Be(
            AzureSnapshotScenario.SetBackupId(Now.AddHours(-4)),
            "a daily bucket keeps its NEWEST capture, so the earlier capture on the same day is released while the "
            + "older day still holds the second slot");
    }

    [Fact]
    public async Task An_hourly_policy_keeps_both_captures_the_daily_one_released()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddHours(-4));
        scenario.AddServyxSet(Now);

        var result = await scenario.Provider()
            .PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(2, 0, 0), dryRun: true);

        result.Removed.Should().BeEmpty("the two captures are in different clock hours");
    }

    [Fact]
    public async Task A_prune_with_no_policy_falls_back_to_the_context_default()
    {
        var scenario = new AzureSnapshotScenario();
        for (var day = 0; day < 5; day++)
        {
            scenario.AddServyxSet(Now.AddDays(-day));
        }

        var result = await scenario.Provider(new RetentionPolicy(0, 1, 0))
            .PruneAsync(AzureSnapshotScenario.ServerId, null!, dryRun: true);

        result.Removed.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_set_is_deleted_all_together_or_not_at_all()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddDays(-9));
        scenario.AddServyxSet(Now);

        await scenario.Provider().PruneAsync(AzureSnapshotScenario.ServerId, new RetentionPolicy(0, 1, 0), false);

        var releasedSet = AzureSnapshotOwnership.FormatSetName(AzureSnapshotScenario.ServerId, Now.AddDays(-9));
        scenario.Deleted.Should().OnlyContain(name => name.StartsWith(releasedSet, StringComparison.Ordinal));
        scenario.Snapshots.Should().HaveCount(2, "the kept set's two members survive intact");
    }

    // -----------------------------------------------------------------------------------------------
    // Restore
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Planning_a_restore_issues_no_mutating_call_at_all()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var plan = await scenario.Provider().PlanRestoreAsync(AzureSnapshotScenario.SetBackupId(Now));

        plan.BackupId.Should().Be(AzureSnapshotScenario.SetBackupId(Now));
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Api.ArmRequests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task A_restore_plan_says_what_a_restore_does_and_what_it_does_not()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var plan = await scenario.Provider().PlanRestoreAsync(AzureSnapshotScenario.SetBackupId(Now));

        plan.AffectedPaths.Should().Contain(p => p.Contains("NOT AN OVERWRITE, AND NOT ONE CALL", StringComparison.Ordinal));
        plan.AffectedPaths.Should().Contain(p => p.Contains("THIS PROVIDER WILL NOT CARRY IT OUT", StringComparison.Ordinal));
        plan.AffectedPaths.Should().Contain(p => p.Contains("DEALLOCATE", StringComparison.Ordinal));
        plan.AffectedPaths.Should().Contain(p => p.Contains("FULL PROVISIONED size", StringComparison.Ordinal));
        plan.AffectedPaths.Should().Contain(p => p.Contains(DataImpact.Destroyed.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restore_plan_leads_with_the_consistency_caveat()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var plan = await scenario.Provider().PlanRestoreAsync(AzureSnapshotScenario.SetBackupId(Now));

        plan.AffectedPaths[0].Should().Contain(
            "NOT A CONSISTENT POINT IN TIME",
            "for a multi-disk set this is the fact that changes how a restore is planned, not a footnote");
    }

    [Fact]
    public async Task A_restore_plan_names_one_disk_creation_step_per_snapshot()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);

        var plan = await scenario.Provider().PlanRestoreAsync(AzureSnapshotScenario.SetBackupId(Now));

        plan.AffectedPaths
            .Count(p => p.Contains("create a managed disk from snapshot", StringComparison.Ordinal))
            .Should()
            .Be(2);

        plan.AffectedPaths.Should().Contain(p => p.Contains("region eastus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restoring_always_refuses_and_sends_nothing_not_even_a_token_exchange()
    {
        var scenario = new AzureSnapshotScenario();

        var act = async () => await scenario.Provider().RestoreAsync("restore-abc");

        (await act.Should().ThrowAsync<AzureSnapshotRestoreNotPerformedException>())
            .WithMessage("*Nothing was sent to Azure*");

        scenario.Api.Requests.Should().BeEmpty();
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task Restoring_refuses_for_a_foreign_backup_too()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddForeignSnapshot("hand-taken", Now);

        var act = async () => await scenario.Provider()
            .RestoreAsync(AzureSnapshotBackupId.FormatSnapshot(AzureSnapshotScenario.ServerId, "hand-taken"));

        await act.Should().ThrowAsync<AzureSnapshotRestoreNotPerformedException>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_backup_is_inspectable_and_says_its_consistency_is_unknown()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddForeignSnapshot("hand-taken", Now);

        var lines = await scenario.Provider()
            .InspectAsync(AzureSnapshotBackupId.FormatSnapshot(AzureSnapshotScenario.ServerId, "hand-taken"));

        lines.Should().Contain(l => l.Contains("Consistency: UNKNOWN", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Contains("only a human", StringComparison.Ordinal));
        scenario.MutatingRequests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Cost
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_storage_figure_is_a_ceiling_and_never_a_list_price()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now);
        scenario.AddForeignSnapshot("hand-taken", Now);

        var ceiling = await scenario.Provider().EstimateStorageCeilingAsync(AzureSnapshotScenario.ServerId);

        ceiling.ServyxOwnedSetCount.Should().Be(1);
        ceiling.ForeignSnapshotCount.Should().Be(1);
        ceiling.ServyxOwnedMonthlyCeiling.Confidence.Should().Be(
            CostConfidence.Estimated,
            "the rate is a list price but the quantity is an upper bound this adapter derived");
        ceiling.ServyxOwnedMonthlyCeiling.Hourly.Should().BeNull("Azure bills snapshot storage per GB-month");
        ceiling.ServyxOwnedMonthlyCeiling.Source.Should().Contain("THIS IS A CEILING, NOT A PRICE");
        ceiling.ForeignMonthlyCeiling.Monthly.Should().NotBeNull(
            "a foreign snapshot is a real charge on the subscription even though retention will never reduce it");
    }

    [Fact]
    public void A_snapshot_whose_size_azure_did_not_report_answers_unknown_rather_than_zero()
    {
        var estimate = AzureSnapshotPricing.Ceiling(null);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Monthly.Should().BeNull();
        estimate.Source.Should().Contain("It is still billing");
    }

    [Fact]
    public async Task Cost_wording_says_a_later_capture_costs_a_fraction_of_the_ceiling()
    {
        var scenario = new AzureSnapshotScenario();
        scenario.AddServyxSet(Now.AddDays(-1));
        scenario.AddServyxSet(Now);

        var first = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now.AddDays(-1)));
        var later = await scenario.Provider().InspectAsync(AzureSnapshotScenario.SetBackupId(Now));

        first.Should().Contain(l => l.Contains("only capture Servyx holds", StringComparison.Ordinal));
        later.Should().Contain(l => l.Contains("small fraction of the figure above", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cost_source_states_that_servyx_chose_incremental_rather_than_inheriting_it()
    {
        AzureSnapshotPricing.Source.Should().Contain("Servyx creates every snapshot with incremental=true");
        AzureSnapshotPricing.Source.Should().Contain("SOURCE DISK's PROVISIONED size");
        AzureSnapshotPricing.SnapshotDate.Should().NotBeNullOrWhiteSpace();
    }

    // -----------------------------------------------------------------------------------------------
    // Composition
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_opt_in_factory_builds_a_provider_without_touching_the_network()
    {
        var scenario = new AzureSnapshotScenario();

        var provider = AzureSnapshotBackups.Create(
            scenario.Api.Client(),
            scenario.Secrets,
            new AzureServicePrincipal(
                Provisioning.AzureScenario.TenantId,
                Provisioning.AzureScenario.ClientId,
                Provisioning.AzureScenario.ClientSecretUrn),
            AzureSnapshotScenario.SubscriptionId,
            new AzureSnapshotScenario.StubContextSource(new AzureSnapshotContext(
                AzureSnapshotScenario.ServerId,
                AzureSnapshotScenario.ResourceGroup,
                AzureSnapshotScenario.VmName,
                AzureSnapshotScenario.JobId,
                AzureSnapshotScenario.ConnectorId,
                new RetentionPolicy(0, 3, 0))));

        provider.SubscriptionId.Should().Be(AzureSnapshotScenario.SubscriptionId);
        scenario.Api.Requests.Should().BeEmpty("constructing a provider must not spend money or exchange a token");
        scenario.Secrets.Resolved.Should().BeEmpty();
    }

    [Fact]
    public void A_provider_cannot_be_built_without_a_context_source()
    {
        var scenario = new AzureSnapshotScenario();

        var act = () => new AzureSnapshotBackupProvider(
            scenario.Api.Client(),
            scenario.Secrets,
            new AzureServicePrincipal(
                Provisioning.AzureScenario.TenantId,
                Provisioning.AzureScenario.ClientId,
                Provisioning.AzureScenario.ClientSecretUrn),
            AzureSnapshotScenario.SubscriptionId,
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task A_server_id_that_could_never_be_recognised_afterwards_is_refused_before_anything_is_created()
    {
        var scenario = new AzureSnapshotScenario();

        var act = async () => await scenario.Provider(serverId: "srv 0001").CreateAsync("srv 0001");

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*bill forever and never be pruned*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
