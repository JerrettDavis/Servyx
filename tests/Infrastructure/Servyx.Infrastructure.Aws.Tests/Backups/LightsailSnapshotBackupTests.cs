using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Backups;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>
/// Barrier 1 in practice: what <see cref="LightsailSnapshotBackupProvider.PruneAsync"/> deletes, and — far more
/// importantly — what it does not.
/// </summary>
/// <remarks>
/// Deleting a Lightsail instance snapshot is irreversible and may be removing the only copy of somebody's saves,
/// so these assert on the substituted account's <em>state</em> and on the exact set of names the adapter asked
/// Lightsail to delete, not merely on the <see cref="PruneResult"/> it returned. A prune that reported the right
/// thing and deleted the wrong one would pass a return-value assertion.
/// </remarks>
public sealed class LightsailSnapshotPruneTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static LightsailSnapshotScenario WithFourServyxAndTwoForeign()
    {
        var scenario = new LightsailSnapshotScenario();

        scenario.AddServyxSnapshot(Day.AddDays(-3));
        scenario.AddServyxSnapshot(Day.AddDays(-2));
        scenario.AddServyxSnapshot(Day.AddDays(-1));
        scenario.AddServyxSnapshot(Day);

        scenario.AddForeignSnapshot("taken-by-hand-before-the-update", Day.AddDays(-30));
        scenario.AddForeignSnapshot(
            "auto-snapshot-2026-06-27",
            Day.AddDays(-29),
            isFromAutoSnapshot: true);

        return scenario;
    }

    [Fact]
    public async Task A_dry_run_names_only_servyx_snapshots_counts_the_foreign_ones_and_deletes_nothing()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: true);

        result.Removed.Should().BeEquivalentTo([LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3))]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(6);
    }

    [Fact]
    public async Task A_live_run_deletes_the_released_servyx_snapshot_and_leaves_every_foreign_one_in_place()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo([LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3))]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEquivalentTo(
            [LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-3))]);

        scenario.Snapshots.Select(s => s.Name).Should()
            .Contain(["taken-by-hand-before-the-update", "auto-snapshot-2026-06-27"]);
    }

    /// <summary>
    /// The non-negotiable, stated as directly as it can be: an account of nothing but foreign snapshots, a
    /// retention policy that keeps nothing at all, and both values of <c>dryRun</c>. Nothing may be deleted, and
    /// <see cref="PruneResult.SkippedForeign"/> must account for every one of them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_prune_that_would_keep_nothing_still_deletes_no_foreign_snapshot(bool dryRun)
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddForeignSnapshot("taken-by-hand", Day.AddDays(-30));
        scenario.AddForeignSnapshot("auto-snapshot-2026-06-28", Day.AddDays(-29), isFromAutoSnapshot: true);

        // The most dangerous shape of all: a foreign snapshot whose NAME is exactly the one Servyx would have
        // written. Only the tags tell it apart from a Servyx snapshot.
        scenario.AddForeignSnapshot(
            LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-28)),
            Day.AddDays(-28));

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(3);
    }

    /// <summary>
    /// The four-mark mutation, exercised end to end through a live prune rather than only against the classifier.
    /// Each run removes exactly one of Servyx's marks from an otherwise perfect snapshot and asserts that a prune
    /// keeping nothing still deletes nothing — so no single mark's absence can be shrugged off by any code path
    /// between the listing and the <c>DeleteInstanceSnapshot</c>.
    /// </summary>
    /// <remarks>
    /// The expected <see cref="PruneResult.SkippedForeign"/> differs for mark 1 and that difference is the point,
    /// not an inconsistency. Breaking <c>fromInstanceName</c> does not make the snapshot a foreign backup
    /// <em>of this server</em> — it makes it a snapshot of a different machine, which never enters this server's
    /// listing at all and is therefore not counted. The other three leave it in the listing, labelled foreign.
    /// </remarks>
    [Theory]
    [InlineData("fromInstanceName", 0)]
    [InlineData("servyx.managed", 1)]
    [InlineData("servyx.instance-id", 1)]
    [InlineData("name", 1)]
    public async Task A_snapshot_missing_any_one_of_the_four_marks_survives_a_prune_that_keeps_nothing(
        string markToBreak,
        int expectedSkippedForeign)
    {
        var scenario = new LightsailSnapshotScenario();
        var snapshot = scenario.AddServyxSnapshot(Day);

        switch (markToBreak)
        {
            case "fromInstanceName":
                snapshot.FromInstanceName = LightsailSnapshotScenario.OtherInstanceName;
                break;
            case "name":
                snapshot.Name = "renamed-by-hand-in-the-console";
                break;
            default:
                snapshot.Tags.Remove(markToBreak);
                break;
        }

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(expectedSkippedForeign);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().ContainSingle();
    }

    /// <summary>
    /// The same mutation under a <em>dry run</em>. Barrier 2 is what makes the guarantee hold here: a dry run's
    /// report comes from the same ownership-asserting call the live run uses, so a foreign snapshot cannot even
    /// be hypothetically scheduled for deletion.
    /// </summary>
    [Theory]
    [InlineData("servyx.managed")]
    [InlineData("servyx.instance-id")]
    public async Task A_dry_run_never_even_names_a_snapshot_missing_a_mark(string markToRemove)
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day).Tags.Remove(markToRemove);

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: true);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(1);
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// The invariant barrier 3 exists to enforce, asserted over a whole live prune rather than over one branch:
    /// <em>every</em> snapshot the adapter asked Lightsail to delete carried all four of Servyx's marks at the
    /// moment it was deleted. The fixture deliberately includes a snapshot carrying three of the four — the shape
    /// most likely to slip past a weaker check — and it survives.
    /// </summary>
    [Fact]
    public async Task Every_snapshot_a_prune_deleted_carried_all_four_marks()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var threeOfFour = scenario.AddForeignSnapshot(
            LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-40)),
            Day.AddDays(-40));

        threeOfFour.Tags["servyx.managed"] = "true";
        threeOfFour.FromInstanceName = LightsailSnapshotScenario.InstanceName;

        var before = scenario.Snapshots.ToDictionary(
            s => s.Name,
            s => (s.FromInstanceName, Tags: (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                s.Tags,
                StringComparer.Ordinal)),
            StringComparer.Ordinal);

        await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        scenario.Deleted.Should().NotBeEmpty();
        scenario.Deleted.Should().NotContain(threeOfFour.Name);

        foreach (var name in scenario.Deleted)
        {
            var (fromInstance, tags) = before[name];

            LightsailSnapshotOwnership
                .Classify(fromInstance, name, tags, "srv-0001", LightsailSnapshotScenario.InstanceName)
                .Should().Be(
                    BackupOwnership.Servyx,
                    "the adapter must never delete a snapshot it cannot prove it owns");
        }
    }

    /// <summary>A snapshot of another instance is not this server's backup at all and never enters the listing.</summary>
    [Fact]
    public async Task Snapshots_of_another_instance_are_not_this_servers_backups_at_all()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);
        scenario.AddServyxSnapshot(Day.AddDays(-9), instanceName: LightsailSnapshotScenario.OtherInstanceName);

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 1, 0), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(0, "a snapshot of another instance is not in this server's listing at all");
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_null_policy_falls_back_to_the_contexts_default()
    {
        var scenario = WithFourServyxAndTwoForeign();

        var result = await scenario.Provider(new RetentionPolicy(0, 3, 0))
            .PruneAsync("srv-0001", null!, dryRun: true);

        result.Removed.Should().BeEquivalentTo([LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3))]);
    }

    /// <summary>
    /// A snapshot that vanished provider-side between the listing and the delete. Lightsail answers
    /// <c>NotFoundException</c>; the snapshot is gone, which is what retention asked for, so it is still reported
    /// as removed and no exception is raised.
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_vanished_before_the_delete_is_reported_removed_not_failed()
    {
        var scenario = WithFourServyxAndTwoForeign();
        scenario.DeleteAnswersNotFound = true;

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo([LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3))]);
        scenario.Deleted.Should().BeEquivalentTo(
            [LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-3))]);
    }

    /// <summary>Retention keeps exactly the snapshots it should, and every kept one is still in the account.</summary>
    [Fact]
    public async Task Retention_keeps_the_newest_capture_of_each_of_the_most_recent_days()
    {
        var scenario = WithFourServyxAndTwoForeign();
        scenario.AddServyxSnapshot(Day.AddHours(-2));

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo(
        [
            LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3)),
            LightsailSnapshotScenario.BackupIdOf(Day.AddHours(-2)),
        ]);

        scenario.Snapshots.Select(s => s.Name).Should().BeEquivalentTo(
        [
            LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-2)),
            LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day.AddDays(-1)),
            LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day),
            "taken-by-hand-before-the-update",
            "auto-snapshot-2026-06-27",
        ]);
    }

    /// <summary>
    /// The listing follows <c>nextPageToken</c> to the end. Stopping at page one would report "no snapshots
    /// beyond page one" as "no snapshots", which for a backup listing reads as data loss — and for a prune would
    /// silently stop retention reaching anything past the first page.
    /// </summary>
    [Fact]
    public async Task A_prune_sees_snapshots_on_later_pages_of_the_listing()
    {
        var scenario = WithFourServyxAndTwoForeign();
        scenario.PageSize = 2;

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: true);

        result.Removed.Should().BeEquivalentTo([LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3))]);
        result.SkippedForeign.Should().Be(2);
    }
}

/// <summary>
/// Taking a backup: it covers the whole instance, it costs money, it takes minutes, and it is not a backup until
/// Lightsail says the snapshot is available.
/// </summary>
public sealed class LightsailSnapshotCreateTests
{
    [Fact]
    public async Task A_backup_is_one_snapshot_of_the_whole_instance_taken_by_one_call()
    {
        var scenario = new LightsailSnapshotScenario();

        var artifact = await scenario.Provider().CreateAsync("srv-0001");

        var submission = scenario.MutatingRequests.Should().ContainSingle().Subject;
        submission.LightsailAction.Should().Be("CreateInstanceSnapshot");
        submission.Body.Should().Contain("\"instanceName\"").And.Contain(LightsailSnapshotScenario.InstanceName);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Id.Should().Be("srv-0001::servyx-snapshot-srv-0001-20260727T100000Z");
        artifact.Location.Should().Be(
            "aws://lightsail/us-east-1/instance-snapshots/servyx-snapshot-srv-0001-20260727T100000Z");

        // 40 GB system disk + 80 GB attached data disk. A figure computed from the system disk alone would
        // understate the source, which is the one direction a ceiling must never err in.
        artifact.SizeBytes.Should().Be(120L * 1024 * 1024 * 1024);
    }

    /// <summary>
    /// The ownership marks are applied by the creating call, which is what Lightsail's API allows and what closes
    /// the window in which a billing snapshot could exist untagged. No <c>TagResource</c> is issued at all.
    /// </summary>
    [Fact]
    public async Task The_ownership_marks_travel_in_the_create_call_and_no_separate_tagging_call_is_made()
    {
        var scenario = new LightsailSnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        var submission = scenario.MutatingRequests.Should().ContainSingle().Subject;
        submission.LightsailAction.Should().Be("CreateInstanceSnapshot");
        submission.Body.Should().NotBeNull();
        submission.Body!.Should()
            .Contain("\"servyx.managed\"").And.Contain("\"servyx.instance-id\"")
            .And.Contain("\"servyx.job-id\"").And.Contain("\"servyx.connector-id\"");

        scenario.Api.Requests.Should().NotContain(r => r.LightsailAction == "TagResource");

        var created = scenario.Snapshots.Should().ContainSingle().Subject;
        created.Tags["servyx.managed"].Should().Be("true");
        created.Tags["servyx.instance-id"].Should().Be("srv-0001");
        created.Tags["servyx.job-id"].Should().Be("job-42");
        created.Tags["servyx.connector-id"].Should().Be("conn-1");
    }

    /// <summary>
    /// Submission is not success: the adapter reads the snapshot back until Lightsail reports it
    /// <c>available</c>, and the artifact only exists after that observation.
    /// </summary>
    [Fact]
    public async Task A_create_polls_until_the_snapshot_is_available_before_reporting_a_backup()
    {
        var scenario = new LightsailSnapshotScenario
        {
            CreatedSnapshotStates = ["pending", "pending", "available"],
        };

        var artifact = await scenario.Provider(snapshotPollAttempts: 3).CreateAsync("srv-0001");

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        scenario.SnapshotReads.Should().Be(3, "the first two reads reported the snapshot as still pending");
    }

    /// <summary>
    /// The negative half of the same claim, and the one that matters: a snapshot Lightsail never reports
    /// available is NOT a backup, and no <see cref="BackupArtifact"/> comes back for it.
    /// </summary>
    [Fact]
    public async Task A_snapshot_still_pending_when_the_polls_are_spent_is_not_reported_as_a_backup()
    {
        var scenario = new LightsailSnapshotScenario
        {
            CreatedSnapshotStates = ["pending"],
        };

        var act = async () => await scenario.Provider(snapshotPollAttempts: 2).CreateAsync("srv-0001");

        var failure = (await act.Should().ThrowAsync<LightsailSnapshotNotConfirmedException>()).Which;
        failure.Submitted.Should().BeTrue();
        failure.Observed.Should().BeTrue("the snapshot was read back at least once, so it exists and is billing");
        failure.SnapshotName.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
        failure.Message.Should().Contain("only submitted").And.Contain("Do not resubmit blindly");

        // It exists at Lightsail, unfinished and billing. Servyx neither deletes it nor claims it as a backup.
        scenario.Snapshots.Should().ContainSingle();
    }

    /// <summary>
    /// A create whose snapshot never becomes readable at all. Still not confirmed, still not a backup, and the
    /// message says outright that Servyx cannot tell whether anything exists.
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_never_appears_is_reported_as_unconfirmed_and_never_as_absent()
    {
        var scenario = new LightsailSnapshotScenario { CreatedSnapshotEverAppears = false };

        var act = async () => await scenario.Provider(snapshotPollAttempts: 2).CreateAsync("srv-0001");

        var failure = (await act.Should().ThrowAsync<LightsailSnapshotNotConfirmedException>()).Which;
        failure.Observed.Should().BeFalse();
        failure.Submitted.Should().BeTrue();
        failure.Message.Should().Contain("Servyx cannot tell");
    }

    [Fact]
    public async Task A_snapshot_lightsail_reports_as_errored_is_a_failure_not_an_unconfirmed_result()
    {
        var scenario = new LightsailSnapshotScenario
        {
            CreatedSnapshotStates = ["error"],
        };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        (await act.Should().ThrowAsync<LightsailSnapshotFailedException>())
            .Which.SnapshotName.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
    }

    /// <summary>
    /// Lightsail answers a create with pending <c>Operation</c> records, and one reporting <c>Failed</c> is the
    /// provider saying the request itself did not take. That is a failure, distinguished from a slow copy.
    /// </summary>
    [Fact]
    public async Task An_operation_lightsail_reports_as_failed_fails_the_create_immediately()
    {
        var scenario = new LightsailSnapshotScenario
        {
            CreateOperationStatus = "Failed",
            CreatedSnapshotEverAppears = false,
        };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var failure = (await act.Should().ThrowAsync<LightsailSnapshotFailedException>()).Which;
        failure.Message.Should().Contain("Failed").And.Contain("snapshottable state");
        scenario.SnapshotReads.Should().Be(0, "a failed operation is not polled for");
    }

    /// <summary>
    /// A snapshot that exists but cannot be verified as Servyx's is billing and unprunable, so it is raised as an
    /// error naming it and its cost — never returned as a successful backup.
    /// </summary>
    [Fact]
    public async Task A_snapshot_whose_marks_did_not_stick_is_refused_rather_than_reported_as_a_backup()
    {
        var scenario = new LightsailSnapshotScenario { TagsStick = false };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var failure = (await act.Should().ThrowAsync<LightsailSnapshotOwnershipNotRecordedException>()).Which;
        failure.SnapshotName.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
        failure.Message.Should().Contain("will bill").And.Contain("NEVER remove");
        failure.Message.Should().Contain("Cost ceiling");
    }

    [Fact]
    public async Task A_create_against_an_instance_lightsail_no_longer_knows_takes_no_snapshot()
    {
        var scenario = new LightsailSnapshotScenario { InstanceExists = false };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        (await act.Should().ThrowAsync<LightsailSnapshotNotFoundException>())
            .Which.Message.Should().Contain("still bills");

        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_with_no_configured_context_is_never_snapshotted()
    {
        var scenario = new LightsailSnapshotScenario();

        var act = async () => await scenario.Provider().CreateAsync("srv-unknown");

        await act.Should().ThrowAsync<LightsailSnapshotNotFoundException>();
        scenario.Api.Requests.Should().BeEmpty();
    }
}

/// <summary>Listing and inspecting: what Servyx will say about a backup, and what it refuses to claim.</summary>
public sealed class LightsailSnapshotListAndInspectTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_listing_labels_servyx_and_foreign_snapshots_and_excludes_other_instances()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);
        scenario.AddForeignSnapshot("taken-by-hand", Day.AddDays(-1));
        scenario.AddServyxSnapshot(Day.AddDays(-2), instanceName: LightsailSnapshotScenario.OtherInstanceName);

        var listed = await scenario.Provider().ListAsync("srv-0001");

        listed.Select(a => a.Id).Should().BeEquivalentTo(
        [
            "srv-0001::taken-by-hand",
            LightsailSnapshotScenario.BackupIdOf(Day),
        ]);

        listed.Single(a => a.Id.EndsWith("taken-by-hand", StringComparison.Ordinal))
            .Ownership.Should().Be(BackupOwnership.Foreign);
        listed.Single(a => a.Id == LightsailSnapshotScenario.BackupIdOf(Day))
            .Ownership.Should().Be(BackupOwnership.Servyx);

        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// The central shape claim of this adapter, asserted where an operator would read it: an instance snapshot
    /// covers the attached block storage disks too, and the description names them rather than asserting coverage
    /// it has not read.
    /// </summary>
    [Fact]
    public async Task Inspect_names_the_attached_block_storage_disks_the_snapshot_copied()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var described = await scenario.Provider().InspectAsync(LightsailSnapshotScenario.BackupIdOf(Day));

        var text = string.Join('\n', described);
        text.Should().Contain("attached block storage disk");
        text.Should().Contain(LightsailSnapshotScenario.DataDiskName);
        text.Should().Contain(LightsailSnapshotScenario.DataDiskPath);
        text.Should().Contain("80 GB");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// An instance with no attached disks must not read as "attached disks were covered". The description says
    /// plainly that there were none, which is the honest answer and the one an operator can act on.
    /// </summary>
    [Fact]
    public async Task Inspect_says_plainly_when_the_snapshot_carried_no_attached_disks()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AttachedDisks.Clear();
        scenario.AddServyxSnapshot(Day);

        var text = string.Join(
            '\n',
            await scenario.Provider().InspectAsync(LightsailSnapshotScenario.BackupIdOf(Day)));

        text.Should().Contain("no attached block storage disks");
        text.Should().Contain("attached later is not in this backup");
    }

    [Fact]
    public async Task Inspect_refuses_to_invent_a_file_list_and_states_the_consistency_it_can_actually_claim()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var text = string.Join(
            '\n',
            await scenario.Provider().InspectAsync(LightsailSnapshotScenario.BackupIdOf(Day)));

        text.Should().Contain("File list: NOT AVAILABLE");
        text.Should().Contain("CRASH-CONSISTENT at best");
        text.Should().Contain("does not claim application consistency");
        text.Should().Contain("CUSTOM FIREWALL RULES");
        text.Should().Contain("Cost ceiling");
    }

    /// <summary>A foreign snapshot is inspectable, and the description says Servyx will never delete it.</summary>
    [Fact]
    public async Task A_foreign_snapshot_is_inspectable_and_described_as_untouchable()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddForeignSnapshot("auto-snapshot-2026-07-26", Day, isFromAutoSnapshot: true);

        var text = string.Join('\n', await scenario.Provider().InspectAsync("srv-0001::auto-snapshot-2026-07-26"));

        text.Should().Contain("Ownership: Foreign");
        text.Should().Contain("will never delete it");
        text.Should().Contain("automatic-snapshot add-on");
    }

    /// <summary>
    /// A backup that has vanished provider-side fails as "not found" rather than being trusted as something to
    /// act on. Resolution always goes back through a fresh listing, so a stale id can never authorise anything.
    /// </summary>
    [Fact]
    public async Task A_backup_that_vanished_provider_side_resolves_as_not_found()
    {
        var scenario = new LightsailSnapshotScenario();

        var act = async () => await scenario.Provider().InspectAsync(LightsailSnapshotScenario.BackupIdOf(Day));

        (await act.Should().ThrowAsync<LightsailSnapshotNotFoundException>())
            .Which.BackupId.Should().Be(LightsailSnapshotScenario.BackupIdOf(Day));
    }

    [Fact]
    public async Task A_backup_id_this_provider_never_issued_resolves_as_not_found()
    {
        var scenario = new LightsailSnapshotScenario();

        var act = async () => await scenario.Provider().InspectAsync("not-an-id");

        await act.Should().ThrowAsync<LightsailSnapshotNotFoundException>();
        scenario.Api.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// The cost ceiling counts the system disk and every attached disk, and reports foreign snapshots separately
    /// — they are a real charge on the account that Servyx's retention will never reduce.
    /// </summary>
    [Fact]
    public async Task The_storage_ceiling_counts_attached_disks_and_separates_foreign_snapshots()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);
        scenario.AddForeignSnapshot("taken-by-hand", Day.AddDays(-1));

        var ceiling = await scenario.Provider().EstimateStorageCeilingAsync("srv-0001");

        ceiling.ServyxOwnedCount.Should().Be(1);
        ceiling.ForeignCount.Should().Be(1);

        // (40 + 80) GB at $0.05/GB-month.
        ceiling.ServyxOwnedMonthlyCeiling.Monthly.Should().Be(6.00m);
        ceiling.ForeignMonthlyCeiling.Monthly.Should().Be(6.00m);
        ceiling.ServyxOwnedMonthlyCeiling.Confidence.Should().Be(CostConfidence.Estimated);
        ceiling.ServyxOwnedMonthlyCeiling.Hourly.Should().BeNull();
        ceiling.ServyxOwnedMonthlyCeiling.Source.Should().Contain("CEILING, NOT A PRICE").And.Contain("INCREMENTAL");
        ceiling.AnySizeUnknown.Should().BeFalse();
    }

    [Fact]
    public async Task A_snapshot_whose_size_lightsail_never_reported_is_flagged_rather_than_counted_as_zero()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day).SizeInGb = null;

        var ceiling = await scenario.Provider().EstimateStorageCeilingAsync("srv-0001");

        ceiling.AnySizeUnknown.Should().BeTrue();
    }
}

/// <summary>
/// Restore: what this provider will preview, and what it flatly will not do — because a Lightsail restore
/// produces a new instance rather than putting this one back.
/// </summary>
public sealed class LightsailSnapshotRestoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The claim the whole restore design rests on: previewing issues no mutating call whatsoever, and the plan
    /// says outright that a restore creates a new, separately-billing instance and overwrites nothing.
    /// </summary>
    [Fact]
    public async Task Plan_restore_issues_no_mutating_call_and_says_a_restore_creates_a_new_instance()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var plan = await scenario.Provider().PlanRestoreAsync(LightsailSnapshotScenario.BackupIdOf(Day));

        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Should().ContainSingle();

        plan.BackupId.Should().Be(LightsailSnapshotScenario.BackupIdOf(Day));

        var text = string.Join('\n', plan.AffectedPaths);
        text.Should().Contain("NOT AN OVERWRITE");
        text.Should().Contain("NEW, SEPARATE, SEPARATELY-BILLING instance");
        text.Should().Contain("is not touched, not stopped");
        text.Should().Contain("THIS PROVIDER WILL NOT CARRY IT OUT");
        text.Should().Contain("Nothing has been sent to AWS");
    }

    /// <summary>
    /// The plan names the real parameters <c>CreateInstancesFromSnapshot</c> would demand, so the refusal is not
    /// obstructive: an operator can carry the procedure out by hand from what is written here.
    /// </summary>
    [Fact]
    public async Task The_plan_names_the_snapshot_the_bundle_floor_the_zone_and_the_disks()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var plan = await scenario.Provider().PlanRestoreAsync(LightsailSnapshotScenario.BackupIdOf(Day));
        var text = string.Join('\n', plan.AffectedPaths);

        text.Should().Contain("CreateInstancesFromSnapshot");
        text.Should().Contain(LightsailSnapshotOwnership.FormatSnapshotName("srv-0001", Day));
        text.Should().Contain("medium_3_0");
        text.Should().Contain(LightsailSnapshotScenario.AvailabilityZone);
        text.Should().Contain("attachedDiskMapping");
        text.Should().Contain(LightsailSnapshotScenario.DataDiskName);
        text.Should().Contain("smaller bundle");
    }

    /// <summary>
    /// The data impact stated honestly in both directions: the one call this provider could make destroys
    /// nothing, and the destructive step is the one Servyx refuses to take.
    /// </summary>
    [Fact]
    public async Task The_plan_states_that_creating_the_new_instance_destroys_nothing_and_that_both_then_bill()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var text = string.Join(
            '\n',
            (await scenario.Provider().PlanRestoreAsync(LightsailSnapshotScenario.BackupIdOf(Day))).AffectedPaths);

        text.Should().Contain("DATA IMPACT of step 1 alone: " + DataImpact.Preserved);
        text.Should().Contain("The destructive part is step 4");
        text.Should().Contain(DataImpact.Destroyed.ToString());
        text.Should().Contain("BOTH instances exist and BOTH bill");
        text.Should().Contain("DEFAULT firewall rules");
    }

    /// <summary>
    /// The interface member always refuses, sends nothing, and explains the real procedure. There is no
    /// acknowledging overload to reach for and no argument that makes it proceed.
    /// </summary>
    [Fact]
    public async Task Restore_always_refuses_and_issues_no_request()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);
        var provider = scenario.Provider();

        var plan = await provider.PlanRestoreAsync(LightsailSnapshotScenario.BackupIdOf(Day));
        var before = scenario.Api.Requests.Count;

        var act = async () => await provider.RestoreAsync(plan.Id);

        var refusal = (await act.Should().ThrowAsync<LightsailSnapshotRestoreNotPerformedException>()).Which;
        refusal.RestorePlanId.Should().Be(plan.Id);
        refusal.Message.Should().Contain("SECOND, separately-billing instance");
        refusal.Message.Should().Contain("Nothing was sent to AWS");

        scenario.Api.Requests.Should().HaveCount(before, "a refusal issues no request of any kind");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// The refusal does not depend on the plan being one this provider issued: there is no plan id, valid or
    /// otherwise, that reaches a mutating call. That is what "no force path" means here.
    /// </summary>
    [Fact]
    public async Task No_plan_id_reaches_a_mutating_call()
    {
        var scenario = new LightsailSnapshotScenario();
        scenario.AddServyxSnapshot(Day);

        var act = async () => await scenario.Provider().RestoreAsync("restore-anything-at-all");

        await act.Should().ThrowAsync<LightsailSnapshotRestoreNotPerformedException>();
        scenario.Api.Requests.Should().BeEmpty();
    }
}
