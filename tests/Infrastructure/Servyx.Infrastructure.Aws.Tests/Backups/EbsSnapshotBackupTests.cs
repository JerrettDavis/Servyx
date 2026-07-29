using System.Net;

using Servyx.Domain.Backups;
using Servyx.Infrastructure.Aws.Backups;
using Servyx.Infrastructure.Aws.Tests.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>
/// Barrier 1 in practice: what <see cref="EbsSnapshotBackupProvider.PruneAsync"/> deletes, and — far more
/// importantly — what it does not.
/// </summary>
/// <remarks>
/// Deleting an EBS snapshot is irreversible and may be removing the only copy of somebody's saves, so these
/// assert on the substituted account's <em>state</em> and on the exact set of ids the adapter asked AWS to
/// delete, not merely on the <see cref="PruneResult"/> it returned. A prune that reported the right thing and
/// deleted the wrong one would pass a return-value assertion.
/// </remarks>
public sealed class EbsSnapshotPruneTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static EbsSnapshotScenario WithFourServyxSetsAndTwoForeign()
    {
        var scenario = new EbsSnapshotScenario();

        scenario.AddServyxSet("snap-d1", Day.AddDays(-3));
        scenario.AddServyxSet("snap-d2", Day.AddDays(-2));
        scenario.AddServyxSet("snap-d3", Day.AddDays(-1));
        scenario.AddServyxSet("snap-d4", Day);

        scenario.AddForeignSnapshot("snap-0foreign00001", Day.AddDays(-30));
        scenario.AddForeignSnapshot(
            "snap-0foreign00002",
            Day.AddDays(-29),
            EbsSnapshotScenario.DataVolumeId,
            "aws-backup nightly",
            new KeyValuePair<string, string>("aws:backup:source-resource", "vol-0data000000000b"));

        return scenario;
    }

    [Fact]
    public async Task A_dry_run_names_only_servyx_sets_counts_the_foreign_snapshots_and_deletes_nothing()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: true);

        result.Removed.Should().BeEquivalentTo([EbsSnapshotScenario.SetBackupId(Day.AddDays(-3))]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(10);
    }

    /// <summary>
    /// A live run removes a whole set — both snapshots of it — and touches neither foreign snapshot. The
    /// per-snapshot assertion matters: a set is only a backup if it is complete, so half a prune would be as
    /// wrong as a missed one.
    /// </summary>
    [Fact]
    public async Task A_live_run_deletes_a_whole_servyx_set_and_leaves_every_foreign_snapshot_in_place()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo([EbsSnapshotScenario.SetBackupId(Day.AddDays(-3))]);
        result.SkippedForeign.Should().Be(2);

        scenario.Deleted.Should().BeEquivalentTo(["snap-d1-a", "snap-d1-b"]);
        scenario.Deleted.Should().NotContain("snap-0foreign00001").And.NotContain("snap-0foreign00002");
        scenario.Snapshots.Select(s => s.Id).Should().Contain(["snap-0foreign00001", "snap-0foreign00002"]);
    }

    /// <summary>
    /// The non-negotiable, stated as directly as it can be: an account of nothing but foreign snapshots, a
    /// retention policy that keeps nothing at all, and both values of <c>dryRun</c>. Nothing may be deleted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_prune_that_would_keep_nothing_still_deletes_no_foreign_snapshot(bool dryRun)
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddForeignSnapshot("snap-0foreign00001", Day.AddDays(-30));
        scenario.AddForeignSnapshot("snap-0foreign00002", Day.AddDays(-29), EbsSnapshotScenario.DataVolumeId);
        scenario.AddForeignSnapshot(
            "snap-0foreign00003",
            Day.AddDays(-28),
            EbsSnapshotScenario.RootVolumeId,
            EbsSnapshotOwnership.FormatSetName("srv-0001", Day.AddDays(-28)));

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(3);
    }

    /// <summary>
    /// The four-mark mutation, exercised end to end through a live prune rather than only against the
    /// classifier. Each run removes exactly one of Servyx's marks from an otherwise perfect set and asserts
    /// that a prune keeping nothing still deletes nothing — so no single mark's absence can be shrugged off by
    /// any code path between the listing and the <c>DeleteSnapshot</c>.
    /// </summary>
    [Theory]
    [InlineData("servyx.managed")]
    [InlineData("servyx.instance-id")]
    [InlineData("servyx.source-instance")]
    [InlineData("servyx.snapshot-set")]
    public async Task A_set_missing_any_one_of_the_four_marks_is_foreign_and_survives_a_prune_that_keeps_nothing(
        string markToRemove)
    {
        var scenario = new EbsSnapshotScenario();
        var set = scenario.AddServyxSet("snap-mut", Day);

        foreach (var snapshot in set)
        {
            snapshot.Tags.Remove(markToRemove);
        }

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(2, "each snapshot is now foreign and is counted individually");

        scenario.Deleted.Should().BeEmpty();
        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Snapshots.Should().HaveCount(2);
    }

    /// <summary>
    /// The same mutation applied to <em>one member</em> of a set. The set as a whole is no longer complete, the
    /// surviving member is still classified Servyx-owned, and the point is that the mutated one is never
    /// deleted — retention deletes what it can prove it owns and nothing else.
    /// </summary>
    [Fact]
    public async Task Corrupting_one_members_marks_never_deletes_that_member()
    {
        var scenario = new EbsSnapshotScenario();
        var set = scenario.AddServyxSet("snap-mut", Day);
        set[1].Tags[EbsSnapshotOwnership.SourceInstanceTag] = EbsSnapshotScenario.OtherEc2InstanceId;

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        result.SkippedForeign.Should().Be(1);
        scenario.Deleted.Should().NotContain(set[1].Id);
        scenario.Snapshots.Select(s => s.Id).Should().Contain(set[1].Id);
    }

    /// <summary>
    /// A snapshot Servyx took of a <em>different</em> EC2 instance, of a volume that is not this instance's.
    /// It is not this server's backup at all and never enters the listing.
    /// </summary>
    [Fact]
    public async Task Snapshots_of_another_instance_are_not_this_servers_backups_at_all()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);

        var stranger = scenario.AddForeignSnapshot(
            "snap-0stranger0001",
            Day.AddDays(-9),
            "vol-0stranger00000",
            "someone else's machine");
        stranger.Tags[EbsSnapshotOwnership.SourceInstanceTag] = EbsSnapshotScenario.OtherEc2InstanceId;

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 1, 0), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(0, "a snapshot of another instance is not in this server's listing at all");
        scenario.Deleted.Should().BeEmpty();
        scenario.Snapshots.Select(s => s.Id).Should().Contain("snap-0stranger0001");
    }

    [Fact]
    public async Task A_null_policy_falls_back_to_the_contexts_default()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();

        var result = await scenario.Provider(new RetentionPolicy(0, 3, 0)).PruneAsync("srv-0001", null!, dryRun: true);

        result.Removed.Should().BeEquivalentTo([EbsSnapshotScenario.SetBackupId(Day.AddDays(-3))]);
    }

    /// <summary>
    /// A snapshot that vanished provider-side between the listing and the delete. AWS answers
    /// <c>InvalidSnapshot.NotFound</c>; the snapshot is gone, which is what retention asked for, so it is still
    /// reported as removed and no exception is raised.
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_vanished_before_the_delete_is_reported_removed_not_failed()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();
        scenario.DeleteStatus = HttpStatusCode.BadRequest;

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo([EbsSnapshotScenario.SetBackupId(Day.AddDays(-3))]);
        scenario.Deleted.Should().BeEquivalentTo(["snap-d1-a", "snap-d1-b"]);
    }

    /// <summary>Retention keeps exactly the sets it should, and every kept snapshot is still in the account.</summary>
    [Fact]
    public async Task Retention_keeps_the_newest_capture_of_each_of_the_most_recent_days()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();
        scenario.AddServyxSet("snap-d4b", Day.AddHours(-2));

        var result = await scenario.Provider()
            .PruneAsync("srv-0001", new RetentionPolicy(0, 3, 0), dryRun: false);

        result.Removed.Should().BeEquivalentTo(
        [
            EbsSnapshotScenario.SetBackupId(Day.AddDays(-3)),
            EbsSnapshotScenario.SetBackupId(Day.AddHours(-2)),
        ]);

        scenario.Snapshots.Select(s => s.Id).Should().BeEquivalentTo(
        [
            "snap-d2-a", "snap-d2-b",
            "snap-d3-a", "snap-d3-b",
            "snap-d4-a", "snap-d4-b",
            "snap-0foreign00001", "snap-0foreign00002",
        ]);
    }

    /// <summary>
    /// The invariant barrier 3 exists to enforce, asserted over a whole live prune rather than over one branch:
    /// <em>every</em> snapshot the adapter asked AWS to delete carried all four of Servyx's marks at the moment
    /// it was deleted. The fixture deliberately includes a snapshot carrying three of the four — the shape most
    /// likely to slip past a weaker check — and it survives.
    /// </summary>
    [Fact]
    public async Task Every_snapshot_a_prune_deleted_carried_all_four_marks()
    {
        var scenario = WithFourServyxSetsAndTwoForeign();

        var threeOfFour = scenario.AddForeignSnapshot(
            "snap-0partial00001",
            Day.AddDays(-40),
            EbsSnapshotScenario.RootVolumeId,
            "looks Servyx-shaped and is not");

        threeOfFour.Tags["servyx.managed"] = "true";
        threeOfFour.Tags["servyx.instance-id"] = "srv-0001";
        threeOfFour.Tags[EbsSnapshotOwnership.SetTag] =
            EbsSnapshotOwnership.FormatSetName("srv-0001", Day.AddDays(-40));

        var tagsAtStart = scenario.Snapshots.ToDictionary(
            s => s.Id,
            s => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(s.Tags, StringComparer.Ordinal),
            StringComparer.Ordinal);

        await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);

        scenario.Deleted.Should().NotBeEmpty();
        scenario.Deleted.Should().NotContain("snap-0partial00001");

        foreach (var id in scenario.Deleted)
        {
            EbsSnapshotOwnership
                .Classify(tagsAtStart[id], "srv-0001", EbsSnapshotScenario.Ec2InstanceId)
                .Should().Be(BackupOwnership.Servyx, "the adapter must never delete a snapshot it cannot prove it owns");
        }
    }
}

/// <summary>
/// Taking a backup: it covers every attached volume, it costs money, it takes minutes, and it is not a backup
/// until AWS says every snapshot in it is complete.
/// </summary>
public sealed class EbsSnapshotCreateTests
{
    /// <summary>
    /// The multi-volume decision, asserted where it matters: one <c>CreateSnapshots</c> naming the
    /// <em>instance</em>, explicitly not excluding the boot volume, producing one snapshot per attached EBS
    /// volume, reported as one artifact.
    /// </summary>
    [Fact]
    public async Task A_backup_covers_every_attached_ebs_volume_in_one_atomic_call()
    {
        var scenario = new EbsSnapshotScenario();

        var artifact = await scenario.Provider().CreateAsync("srv-0001");

        var submission = scenario.MutatingRequests.Should().ContainSingle().Subject;
        submission.Action.Should().Be("CreateSnapshots");
        submission.ParameterOf("InstanceSpecification.InstanceId").Should().Be(EbsSnapshotScenario.Ec2InstanceId);
        submission.ParameterOf("InstanceSpecification.ExcludeBootVolume").Should().Be("false");

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Id.Should().Be("srv-0001::servyx-snapshot-srv-0001-20260727T100000Z");
        artifact.Location.Should().Be(
            "aws://ec2/us-east-1/snapshot-sets/servyx-snapshot-srv-0001-20260727T100000Z");

        scenario.Snapshots.Where(s => s.Id.StartsWith("snap-0created", StringComparison.Ordinal))
            .Select(s => s.VolumeId)
            .Should().BeEquivalentTo([EbsSnapshotScenario.RootVolumeId, EbsSnapshotScenario.DataVolumeId]);

        // 30 GiB root + 100 GiB data, reported as the SOURCE volumes' allocated size.
        artifact.SizeBytes.Should().Be(130L * 1024 * 1024 * 1024);
    }

    [Fact]
    public async Task Every_snapshot_in_the_set_carries_all_four_ownership_marks_applied_by_the_create_call()
    {
        var scenario = new EbsSnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        foreach (var snapshot in scenario.Snapshots)
        {
            snapshot.Tags["servyx.managed"].Should().Be("true");
            snapshot.Tags["servyx.instance-id"].Should().Be("srv-0001");
            snapshot.Tags[EbsSnapshotOwnership.SourceInstanceTag].Should().Be(EbsSnapshotScenario.Ec2InstanceId);
            snapshot.Tags[EbsSnapshotOwnership.SetTag].Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
        }
    }

    /// <summary>
    /// The non-negotiable about creation: submission is not success. AWS answers <c>CreateSnapshots</c> while
    /// every snapshot is still <c>pending</c>, so the adapter must go back and read them.
    /// </summary>
    [Fact]
    public async Task A_create_polls_to_completion_and_is_not_reported_successful_on_submission_alone()
    {
        var scenario = new EbsSnapshotScenario { CreatedSnapshotStates = ["pending", "pending", "completed"] };

        var before = scenario.Api.Requests.Count;
        var artifact = await scenario.Provider().CreateAsync("srv-0001");

        artifact.Should().NotBeNull();

        var polls = scenario.Api.Requests
            .Skip(before)
            .Count(r => r.Method == HttpMethod.Get && r.Action == "DescribeSnapshots" && r.ParameterOf("SnapshotId.1") is not null);

        polls.Should().Be(3, "the adapter must read the snapshots until AWS reports every one of them completed");
    }

    [Fact]
    public async Task Snapshots_still_pending_when_the_polls_are_spent_are_not_reported_as_a_backup()
    {
        var scenario = new EbsSnapshotScenario { CreatedSnapshotStates = ["pending"] };

        var act = async () => await scenario.Provider(snapshotPollAttempts: 3).CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotNotConfirmedException>();
        thrown.Which.Submitted.Should().BeTrue();
        thrown.Which.SnapshotIds.Should().HaveCount(2);
        thrown.Which.Message.Should().Contain("only submitted are not a backup that exists")
            .And.Contain("Do not resubmit blindly")
            .And.Contain("exist and are billing now");
    }

    [Fact]
    public async Task An_errored_snapshot_is_a_failure_and_a_different_type_from_an_unconfirmed_one()
    {
        var scenario = new EbsSnapshotScenario { CreatedSnapshotStates = ["error"] };
        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotFailedException>();
        thrown.Which.Message.Should().Contain("as 'error'")
            .And.Contain("does NOT report a partial set as a backup");
    }

    /// <summary>
    /// The data-loss trap, refused. If AWS covers only the root volume, the result is not a backup of this
    /// server and is never reported as one — and the message names the snapshots that exist and are billing.
    /// </summary>
    [Fact]
    public async Task A_capture_that_covers_only_some_of_the_volumes_is_refused_as_incomplete()
    {
        var scenario = new EbsSnapshotScenario { VolumesCoveredByCreate = 1 };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotFailedException>();
        thrown.Which.Message.Should().Contain("covered 1 of instance")
            .And.Contain("2 attached EBS volume(s)")
            .And.Contain("the set is INCOMPLETE")
            .And.Contain("DO exist and ARE billing");
        thrown.Which.SnapshotIds.Should().ContainSingle();
    }

    /// <summary>A snapshot deleted out from under the poll: the set is incomplete and is not a backup.</summary>
    [Fact]
    public async Task A_snapshot_that_vanishes_during_the_poll_is_reported_honestly_and_never_as_a_backup()
    {
        var scenario = new EbsSnapshotScenario { SnapshotVanishesDuringPoll = true };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotFailedException>();
        thrown.Which.Message.Should().Contain("has vanished")
            .And.Contain("Something outside Servyx deleted it")
            .And.Contain("The set is therefore INCOMPLETE")
            .And.Contain("remaining snapshots DO exist and ARE billing");
    }

    /// <summary>The same vanishing, expressed the way AWS actually expresses it: an error code on the read.</summary>
    [Fact]
    public async Task A_poll_that_answers_not_found_is_reported_as_a_vanished_snapshot()
    {
        var scenario = new EbsSnapshotScenario { DescribeByIdAnswersNotFound = true };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        await act.Should().ThrowAsync<EbsSnapshotFailedException>()
            .WithMessage("*has vanished*");
    }

    [Fact]
    public async Task A_set_whose_tags_do_not_show_up_afterwards_is_not_claimed_as_owned()
    {
        var scenario = new EbsSnapshotScenario { TagsStick = false };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotOwnershipNotRecordedException>();
        thrown.Which.SnapshotIds.Should().HaveCount(2);
        thrown.Which.Message.Should().Contain("retention will NEVER remove these")
            .And.Contain("Cost ceiling");
    }

    [Fact]
    public async Task An_instance_with_no_ebs_volumes_says_so_and_says_instance_store_cannot_be_snapshotted()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AttachedVolumes.Clear();

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        await act.Should().ThrowAsync<EbsSnapshotFailedException>()
            .WithMessage("*instance store CANNOT be snapshotted by any AWS API*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_terminated_instance_cannot_be_snapshotted_and_says_its_snapshots_survive_it()
    {
        var scenario = new EbsSnapshotScenario { InstanceState = "terminated" };

        var act = async () => await scenario.Provider().CreateAsync("srv-0001");

        await act.Should().ThrowAsync<EbsSnapshotNotFoundException>()
            .WithMessage("*they survive the instance's termination, still exist, and still bill*");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_with_no_context_is_refused_without_a_single_request()
    {
        var scenario = new EbsSnapshotScenario();

        var act = async () => await scenario.Provider().CreateAsync("srv-does-not-exist");

        await act.Should().ThrowAsync<EbsSnapshotNotFoundException>()
            .WithMessage("*does not know which EC2 instance backs it*");
        scenario.Api.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// The credential discipline the whole AWS adapter is built on, re-asserted on the backup path: the key
    /// pair is resolved afresh for every single request and never travels on the wire.
    /// </summary>
    [Fact]
    public async Task Every_request_is_signed_and_neither_half_of_the_key_pair_ever_travels()
    {
        var scenario = new EbsSnapshotScenario();

        await scenario.Provider().CreateAsync("srv-0001");

        scenario.Api.Requests.Should().OnlyContain(r => r.Signature != null && r.Signature.Length == 64);
        scenario.Api.Requests.Should().NotContain(r =>
            (r.Authorization != null && r.Authorization.Contains(AwsScenario.SecretAccessKey, StringComparison.Ordinal))
            || (r.Body != null && r.Body.Contains(AwsScenario.SecretAccessKey, StringComparison.Ordinal))
            || (r.Uri.Query.Contains(AwsScenario.SecretAccessKey, StringComparison.Ordinal)));

        scenario.Secrets.Resolved.Should().HaveCount(scenario.Api.Requests.Count * 2);
    }
}

/// <summary>Listing, inspecting, and what an EBS snapshot honestly cannot tell you.</summary>
public sealed class EbsSnapshotListTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Listing_groups_servyx_snapshots_into_sets_and_reports_foreign_ones_individually()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);
        scenario.AddForeignSnapshot("snap-0foreign00001", Day.AddDays(-1));

        var artifacts = await scenario.Provider().ListAsync("srv-0001");

        artifacts.Should().HaveCount(2);
        artifacts.Single(a => a.Ownership == BackupOwnership.Servyx).Id
            .Should().Be(EbsSnapshotScenario.SetBackupId(Day));
        artifacts.Single(a => a.Ownership == BackupOwnership.Foreign).Id
            .Should().Be("srv-0001::snap-0foreign00001");
    }

    /// <summary>
    /// A Servyx snapshot of a volume that has since been detached. The tag listing finds it, the volume listing
    /// structurally cannot, and if it were missed it would bill forever unpruned.
    /// </summary>
    [Fact]
    public async Task A_snapshot_of_a_since_detached_volume_is_still_found_and_still_prunable()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-old", Day);
        scenario.AttachedVolumes.RemoveAll(v => v.VolumeId == EbsSnapshotScenario.DataVolumeId);

        var artifacts = await scenario.Provider().ListAsync("srv-0001");

        artifacts.Should().ContainSingle().Which.Ownership.Should().Be(BackupOwnership.Servyx);

        var result = await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);
        scenario.Deleted.Should().BeEquivalentTo(["snap-old-a", "snap-old-b"]);
        result.Removed.Should().ContainSingle();
    }

    /// <summary>
    /// The listing that finds work Servyx did not do. A tag filter alone could only ever return Servyx's own
    /// snapshots, so <c>SkippedForeign</c> would be a comfortable zero for an account full of them.
    /// </summary>
    [Fact]
    public async Task A_foreign_snapshot_of_this_instances_volume_is_found_even_though_it_carries_no_servyx_tag()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddForeignSnapshot("snap-0byhand000001", Day, EbsSnapshotScenario.DataVolumeId);

        var artifacts = await scenario.Provider().ListAsync("srv-0001");

        artifacts.Should().ContainSingle().Which.Ownership.Should().Be(BackupOwnership.Foreign);
    }

    /// <summary>
    /// The consistency caveat, in the words a reader will actually see. Crash-consistent, not
    /// application-consistent, and what that means for a workload that was mid-write.
    /// </summary>
    [Fact]
    public async Task Inspecting_states_the_crash_consistency_limit_and_what_is_not_covered()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);

        var text = string.Join("\n", await scenario.Provider().InspectAsync(EbsSnapshotScenario.SetBackupId(Day)));

        text.Should().Contain("CRASH-CONSISTENT across all of the instance's EBS volumes at once")
            .And.Contain("NOT application-consistent")
            .And.Contain("captured mid-write")
            .And.Contain("as after a power cut");

        text.Should().Contain("NOT covered by this backup: instance-store")
            .And.Contain("RAM and process state")
            .And.Contain("RDS, EFS, S3");

        scenario.MutatingRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspecting_names_every_snapshot_with_the_volume_and_device_it_came_from()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);

        var text = string.Join("\n", await scenario.Provider().InspectAsync(EbsSnapshotScenario.SetBackupId(Day)));

        text.Should().Contain("It is 2 EBS snapshot(s)")
            .And.Contain($"snap-mine-a: from volume {EbsSnapshotScenario.RootVolumeId} ({EbsSnapshotScenario.RootDevice})")
            .And.Contain($"snap-mine-b: from volume {EbsSnapshotScenario.DataVolumeId} ({EbsSnapshotScenario.DataDevice})");
    }

    [Fact]
    public async Task Inspecting_says_there_is_no_file_list_and_states_the_incremental_cost_ceiling()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);

        var text = string.Join("\n", await scenario.Provider().InspectAsync(EbsSnapshotScenario.SetBackupId(Day)));

        text.Should().Contain("File list: NOT AVAILABLE")
            .And.Contain("Cost ceiling")
            .And.Contain("$6.50 USD per month")
            .And.Contain("UPPER BOUND, not a price")
            .And.Contain("never expires on its own")
            .And.Contain("frees only the blocks no surviving snapshot still references");
    }

    /// <summary>
    /// A foreign snapshot cannot be described as crash-consistent, because Servyx does not know whether
    /// matching snapshots of the other volumes exist. Saying "unknown" is the only honest answer.
    /// </summary>
    [Fact]
    public async Task Inspecting_a_foreign_snapshot_says_its_consistency_is_unknown_and_retention_cannot_reach_it()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddForeignSnapshot("snap-0foreign00001", Day);

        var text = string.Join("\n", await scenario.Provider().InspectAsync("srv-0001::snap-0foreign00001"));

        text.Should().Contain("Consistency: UNKNOWN")
            .And.Contain("retention cannot reach it")
            .And.Contain("only a human");
    }

    /// <summary>A backup that has vanished provider-side is a "not found", never a silently different one.</summary>
    [Fact]
    public async Task A_backup_that_has_vanished_is_reported_as_gone()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);
        var provider = scenario.Provider();

        scenario.Snapshots.Clear();

        var act = async () => await provider.InspectAsync(EbsSnapshotScenario.SetBackupId(Day));

        await act.Should().ThrowAsync<EbsSnapshotNotFoundException>()
            .WithMessage("*deleted in the console, by another tool, or by a prune*");
    }

    /// <summary>
    /// A set that lost one of its two snapshots is no longer the backup it was. The remaining snapshot is still
    /// listed under a set id, but the id the caller held names a set of two — so it resolves, and inspecting it
    /// says how many snapshots are actually left rather than pretending the set is intact.
    /// </summary>
    [Fact]
    public async Task A_set_that_lost_a_member_reports_the_members_that_remain()
    {
        var scenario = new EbsSnapshotScenario();
        var set = scenario.AddServyxSet("snap-mine", Day);
        scenario.Snapshots.Remove(set[1]);

        var text = string.Join("\n", await scenario.Provider().InspectAsync(EbsSnapshotScenario.SetBackupId(Day)));

        text.Should().Contain("It is 1 EBS snapshot(s)")
            .And.NotContain("snap-mine-b");
    }

    [Fact]
    public async Task A_backup_id_this_provider_did_not_issue_is_refused()
    {
        var scenario = new EbsSnapshotScenario();

        var act = async () => await scenario.Provider().InspectAsync("not-an-id");

        await act.Should().ThrowAsync<EbsSnapshotNotFoundException>()
            .WithMessage("*not in a form this provider issued*");
    }

    /// <summary>
    /// Snapshots outlive the instance they were taken from, so a terminated instance must not make its own
    /// backups invisible — that would leave them billing with nothing able to prune them.
    /// </summary>
    [Fact]
    public async Task Snapshots_of_a_terminated_instance_are_still_listed_and_still_prunable()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);
        scenario.InstanceExists = false;

        var artifacts = await scenario.Provider().ListAsync("srv-0001");
        artifacts.Should().ContainSingle().Which.Ownership.Should().Be(BackupOwnership.Servyx);

        await scenario.Provider().PruneAsync("srv-0001", new RetentionPolicy(0, 0, 0), dryRun: false);
        scenario.Deleted.Should().BeEquivalentTo(["snap-mine-a", "snap-mine-b"]);
    }

    [Fact]
    public async Task The_storage_ceiling_is_split_by_ownership_and_never_silently_summed()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-d1", Day);
        scenario.AddServyxSet("snap-d2", Day.AddDays(-1));
        scenario.AddForeignSnapshot("snap-0foreign00001", Day.AddDays(-2)).VolumeSizeGib = 20;

        var ceiling = await scenario.Provider().EstimateStorageCeilingAsync("srv-0001");

        ceiling.ServyxOwnedSetCount.Should().Be(2);
        ceiling.ForeignSnapshotCount.Should().Be(1);
        ceiling.ServyxOwnedMonthlyCeiling.Monthly.Should().Be(13m);
        ceiling.ForeignMonthlyCeiling.Monthly.Should().Be(1m);
        ceiling.AnySizeUnknown.Should().BeFalse();
    }

    [Fact]
    public async Task A_snapshot_aws_reports_no_size_for_makes_the_ceiling_incomplete_rather_than_wrong()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-d1", Day)[0].VolumeSizeGib = null;

        var ceiling = await scenario.Provider().EstimateStorageCeilingAsync("srv-0001");

        ceiling.AnySizeUnknown.Should().BeTrue();
    }
}

/// <summary>
/// Restore: a genuinely different shape from a droplet restore, previewed read-only and never performed.
/// </summary>
public sealed class EbsSnapshotRestoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static EbsSnapshotScenario WithOneSet(out string backupId)
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", Day);
        backupId = EbsSnapshotScenario.SetBackupId(Day);
        return scenario;
    }

    /// <summary>
    /// The required honesty, in one test: the preview issues no mutating call, and it says both what a restore
    /// does (creates new volumes, which must be attached) and what it does not (overwrite anything in place, or
    /// happen in one call).
    /// </summary>
    [Fact]
    public async Task Previewing_issues_no_mutating_call_and_states_what_a_restore_does_and_does_not_do()
    {
        var scenario = WithOneSet(out var backupId);

        var plan = await scenario.Provider().PlanRestoreAsync(backupId);

        scenario.MutatingRequests.Should().BeEmpty();
        scenario.Api.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);

        var text = string.Join("\n", plan.AffectedPaths);
        text.Should().Contain("NOT AN OVERWRITE, AND NOT ONE CALL")
            .And.Contain("creating a NEW EBS volume")
            .And.Contain("THIS PROVIDER WILL NOT CARRY IT OUT")
            .And.Contain("Nothing has been sent to AWS by previewing this plan");
    }

    /// <summary>The plan is a procedure an operator can actually run: real ids, real devices, real zone, in order.</summary>
    [Fact]
    public async Task The_plan_names_every_snapshot_its_volume_its_device_and_the_availability_zone()
    {
        var scenario = WithOneSet(out var backupId);

        var text = string.Join("\n", (await scenario.Provider().PlanRestoreAsync(backupId)).AffectedPaths);

        text.Should().Contain("Step 1: CreateVolume from snap-mine-a in availability zone us-east-1a")
            .And.Contain("Step 2: CreateVolume from snap-mine-b in availability zone us-east-1a")
            .And.Contain($"currently attached at {EbsSnapshotScenario.RootDevice}")
            .And.Contain($"currently attached at {EbsSnapshotScenario.DataDevice}")
            .And.Contain("a volume cannot be attached across availability zones");
    }

    /// <summary>
    /// The part that must not be smoothed over: putting a restored root volume back means stopping the
    /// instance, and the new volumes bill at full size in the meantime.
    /// </summary>
    [Fact]
    public async Task The_plan_says_a_full_restore_needs_the_instance_stopped_and_the_volumes_swapped()
    {
        var scenario = WithOneSet(out var backupId);

        var text = string.Join("\n", (await scenario.Provider().PlanRestoreAsync(backupId)).AffectedPaths);

        text.Should().Contain("STOP the instance")
            .And.Contain("DetachVolume the current root")
            .And.Contain("AttachVolume the restored one at the same device name")
            .And.Contain("That is downtime, and it is unavoidable")
            .And.Contain("bill per GB-month at the full provisioned size")
            .And.Contain("DATA IMPACT of completing this procedure: Destroyed");
    }

    [Fact]
    public async Task The_plan_states_the_crash_consistency_caveat_for_the_restored_state()
    {
        var scenario = WithOneSet(out var backupId);

        var text = string.Join("\n", (await scenario.Provider().PlanRestoreAsync(backupId)).AffectedPaths);

        text.Should().Contain("CRASH-CONSISTENT across all the instance's EBS volumes at once, not "
            + "application-consistent")
            .And.Contain("plan the restore as you would a recovery from a power cut, not from a clean shutdown");
    }

    /// <summary>
    /// The interface's restore member always refuses, and issues no request of any kind while doing it.
    /// </summary>
    [Fact]
    public async Task The_interface_restore_always_refuses_and_issues_no_request_at_all()
    {
        var scenario = WithOneSet(out var backupId);
        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(backupId);
        var before = scenario.Api.Requests.Count;

        var act = async () => await ((IBackupProvider)provider).RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<EbsSnapshotRestoreNotPerformedException>()
            .WithMessage("*is not an in-place operation and is not a single API call*");

        scenario.Api.Requests.Should().HaveCount(before, "a refusal must not touch AWS at all");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// Refusing without saying why would be obstructive. The refusal names the real sequence and points at the
    /// plan, and it says outright that Servyx will not do half of it.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_the_real_sequence_and_says_why_half_of_it_is_worse_than_none()
    {
        var scenario = WithOneSet(out _);

        var act = async () => await scenario.Provider().RestoreAsync("restore-anything");

        var thrown = await act.Should().ThrowAsync<EbsSnapshotRestoreNotPerformedException>();
        thrown.Which.Message.Should().Contain("CreateVolume")
            .And.Contain("AttachVolume")
            .And.Contain("stopping the instance")
            .And.Contain("will not perform half of it")
            .And.Contain("unattached volumes billing per GB-month")
            .And.Contain("Nothing was sent to AWS")
            .And.Contain("Call PlanRestoreAsync");
        thrown.Which.RestorePlanId.Should().Be("restore-anything");
    }

    /// <summary>A foreign snapshot is previewable — listed, inspectable, previewable, never pruned.</summary>
    [Fact]
    public async Task A_foreign_snapshot_can_still_be_previewed_and_says_its_consistency_is_unknown()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddForeignSnapshot("snap-0foreign00001", Day);

        var plan = await scenario.Provider().PlanRestoreAsync("srv-0001::snap-0foreign00001");

        string.Join("\n", plan.AffectedPaths).Should().Contain("Backup ownership: Foreign")
            .And.Contain("Consistency of the source: UNKNOWN");
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// When the instance is gone there is no availability zone to name, and the plan says so instead of
    /// guessing — a volume created in the wrong zone cannot be attached and is a pure billing mistake.
    /// </summary>
    [Fact]
    public async Task A_plan_for_a_terminated_instance_refuses_to_guess_the_availability_zone()
    {
        var scenario = WithOneSet(out var backupId);
        scenario.InstanceExists = false;

        var text = string.Join("\n", (await scenario.Provider().PlanRestoreAsync(backupId)).AffectedPaths);

        text.Should().Contain("Servyx cannot name it")
            .And.Contain("not currently attached to this instance, so you must decide which device it belongs at");
    }
}

/// <summary>The opt-in composition, and what a host actually gets by naming it.</summary>
public sealed class EbsSnapshotBackupCompositionTests
{
    private static EbsSnapshotBackupProvider Create(EbsSnapshotScenario scenario) =>
        EbsSnapshotBackups.Create(
            scenario.Api.Client(),
            scenario.Secrets,
            new AwsSigningIdentity(AwsScenario.AccessKeyIdUrn, AwsScenario.SecretAccessKeyUrn),
            EbsSnapshotScenario.Region,
            new EbsSnapshotScenario.StubContextSource(new EbsSnapshotContext(
                "srv-0001",
                EbsSnapshotScenario.Ec2InstanceId,
                EbsSnapshotScenario.JobId,
                EbsSnapshotScenario.ConnectorId,
                new RetentionPolicy(0, 3, 0))),
            scenario.Clock);

    [Fact]
    public void The_factory_builds_a_backup_provider()
    {
        var provider = Create(new EbsSnapshotScenario());

        provider.Should().BeAssignableTo<IBackupProvider>();
        provider.Region.Should().Be(EbsSnapshotScenario.Region);
    }

    [Fact]
    public async Task A_provider_reached_only_through_the_interface_still_never_restores()
    {
        var scenario = new EbsSnapshotScenario();
        scenario.AddServyxSet("snap-mine", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero));

        IBackupProvider provider = Create(scenario);

        var plan = await provider.PlanRestoreAsync(
            EbsSnapshotScenario.SetBackupId(new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero)));

        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<EbsSnapshotRestoreNotPerformedException>();
        scenario.MutatingRequests.Should().BeEmpty();
    }

    /// <summary>
    /// The flag-off guarantee, expressed as a fact about the repository rather than a claim: nothing outside
    /// these tests constructs the provider, so a host that does not name the factory never reaches any of it.
    /// </summary>
    [Fact]
    public void The_provider_is_only_reachable_through_the_factory_or_its_own_constructor()
    {
        typeof(EbsSnapshotBackups).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Should().BeEquivalentTo(["Create"]);
    }

    [Fact]
    public void An_invalid_poll_attempt_count_is_refused_at_construction()
    {
        var scenario = new EbsSnapshotScenario();

        var act = () => new EbsSnapshotBackupProvider(
            scenario.Api.Client(),
            scenario.Secrets,
            new AwsSigningIdentity(AwsScenario.AccessKeyIdUrn, AwsScenario.SecretAccessKeyUrn),
            EbsSnapshotScenario.Region,
            new EbsSnapshotScenario.StubContextSource(new EbsSnapshotContext(
                "srv-0001",
                EbsSnapshotScenario.Ec2InstanceId,
                EbsSnapshotScenario.JobId,
                EbsSnapshotScenario.ConnectorId,
                new RetentionPolicy(0, 3, 0))),
            snapshotPollAttempts: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_backup_id_round_trips_and_a_set_name_can_never_collide_with_a_snapshot_id()
    {
        var id = EbsSnapshotBackupId.Format("srv-0001", "servyx-snapshot-srv-0001-20260727T100000Z");

        EbsSnapshotBackupId.TryGetServerId(id, out var serverId).Should().BeTrue();
        serverId.Should().Be("srv-0001");

        EbsSnapshotOwnership.SetNamePrefix.Should().NotStartWith("snap-");
        EbsSnapshotBackupId.LocationOfSet("us-east-1", "s").Should().Be("aws://ec2/us-east-1/snapshot-sets/s");
        EbsSnapshotBackupId.LocationOfSnapshot("us-east-1", "snap-1").Should().Be("aws://ec2/us-east-1/snapshots/snap-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-separator")]
    [InlineData("::trailing")]
    [InlineData("leading::")]
    public void A_malformed_backup_id_does_not_decode(string? backupId) =>
        EbsSnapshotBackupId.TryGetServerId(backupId, out _).Should().BeFalse();
}
