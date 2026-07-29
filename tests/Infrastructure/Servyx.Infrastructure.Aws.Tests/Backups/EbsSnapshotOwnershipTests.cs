using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Backups;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>
/// The classification that decides whether an EBS snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// Every one of the four ownership marks is removed in isolation below, and every one of those removals must
/// turn the answer to <see cref="BackupOwnership.Foreign"/>. That is the property the whole prune guarantee
/// rests on: a snapshot Servyx cannot positively prove it created is never a deletion candidate, and there is
/// no single mark whose absence can be shrugged off.
/// </remarks>
public sealed class EbsSnapshotOwnershipTests
{
    private const string ServerId = "srv-0001";
    private const string InstanceId = "i-0abcdef1234567890";

    private static readonly DateTimeOffset TakenAt = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    private static string SetName => EbsSnapshotOwnership.FormatSetName(ServerId, TakenAt);

    /// <summary>A tag set carrying all four marks, from which each test removes or corrupts exactly one.</summary>
    private static Dictionary<string, string> AllFourMarks(
        string serverId = ServerId,
        string instanceId = InstanceId,
        string? setName = null) =>
        new(StringComparer.Ordinal)
        {
            [EbsSnapshotOwnership.ManagedTag] = EbsSnapshotOwnership.ManagedTagValue,
            [EbsSnapshotOwnership.InstanceIdTag] = serverId,
            [EbsSnapshotOwnership.SourceInstanceTag] = instanceId,
            [EbsSnapshotOwnership.SetTag] = setName ?? EbsSnapshotOwnership.FormatSetName(serverId, TakenAt),
        };

    private static BackupOwnership Classify(IReadOnlyDictionary<string, string>? tags) =>
        EbsSnapshotOwnership.Classify(tags, ServerId, InstanceId);

    [Fact]
    public void A_snapshot_carrying_all_four_marks_is_servyx_owned() =>
        Classify(AllFourMarks()).Should().Be(BackupOwnership.Servyx);

    [Fact]
    public void Mark_1_removed_the_managed_tag_gone_is_foreign()
    {
        var tags = AllFourMarks();
        tags.Remove(EbsSnapshotOwnership.ManagedTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Mark_1_corrupted_a_managed_tag_that_is_not_exactly_true_is_foreign()
    {
        var tags = AllFourMarks();
        tags[EbsSnapshotOwnership.ManagedTag] = "TRUE";

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Mark_2_removed_no_servyx_instance_id_is_foreign()
    {
        var tags = AllFourMarks();
        tags.Remove(EbsSnapshotOwnership.InstanceIdTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    /// <summary>Another Servyx server's snapshot is foreign to this one — this is what keeps retentions apart.</summary>
    [Fact]
    public void Mark_2_corrupted_another_servers_snapshot_is_foreign_to_this_one() =>
        Classify(AllFourMarks(serverId: "srv-0002")).Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void Mark_3_removed_no_source_instance_tag_is_foreign()
    {
        var tags = AllFourMarks();
        tags.Remove(EbsSnapshotOwnership.SourceInstanceTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    /// <summary>
    /// A snapshot Servyx took of the machine this server <em>used</em> to run on. It is Servyx's work, it names
    /// the right server, and it is still not a backup of the instance that exists now.
    /// </summary>
    [Fact]
    public void Mark_3_corrupted_a_snapshot_of_a_different_ec2_instance_is_foreign() =>
        Classify(AllFourMarks(instanceId: "i-0999888877776666")).Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void Mark_4_removed_no_set_tag_is_foreign()
    {
        var tags = AllFourMarks();
        tags.Remove(EbsSnapshotOwnership.SetTag);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void Mark_4_corrupted_a_hand_written_set_tag_is_foreign() =>
        Classify(AllFourMarks(setName: "before-the-update")).Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void Mark_4_corrupted_a_servyx_shaped_set_name_for_another_server_is_foreign()
    {
        var tags = AllFourMarks();
        tags[EbsSnapshotOwnership.SetTag] = EbsSnapshotOwnership.FormatSetName("srv-0002", TakenAt);

        Classify(tags).Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void An_untagged_snapshot_is_foreign() =>
        Classify(new Dictionary<string, string>(StringComparer.Ordinal)).Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void A_null_tag_set_is_foreign() => Classify(null).Should().Be(BackupOwnership.Foreign);

    /// <summary>
    /// The description is deliberately NOT a mark: it is free-form text a stranger can retype in the console.
    /// A snapshot whose description looks exactly like Servyx's, and whose tags do not, is foreign.
    /// </summary>
    [Fact]
    public void A_servyx_shaped_description_confers_nothing_because_the_description_is_not_a_mark() =>
        Classify(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = SetName,
        }).Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void The_managed_tag_is_the_same_literal_the_ec2_orphan_sweep_uses()
    {
        EbsSnapshotOwnership.ManagedTag.Should().Be("servyx.managed");
        EbsSnapshotOwnership.InstanceIdTag.Should().Be("servyx.instance-id");
        EbsSnapshotOwnership.SourceInstanceTag.Should().Be("servyx.source-instance");
        EbsSnapshotOwnership.SetTag.Should().Be("servyx.snapshot-set");
    }

    [Fact]
    public void A_set_name_round_trips_through_the_encoding()
    {
        var setName = EbsSnapshotOwnership.FormatSetName(ServerId, TakenAt);

        setName.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
        EbsSnapshotOwnership.TryParseSetName(setName, out var serverId, out var takenAt).Should().BeTrue();
        serverId.Should().Be(ServerId);
        takenAt.Should().Be(TakenAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-other-snapshot")]
    [InlineData("servyx-snapshot-")]
    [InlineData("servyx-snapshot-srv-0001")]
    [InlineData("servyx-snapshot-srv-0001-notatimestamp")]
    [InlineData("servyx-snapshot--20260727T100000Z")]
    [InlineData("servyx-snapshot-srv 0001-20260727T100000Z")]
    public void A_set_name_this_encoding_did_not_produce_does_not_parse(string? setName) =>
        EbsSnapshotOwnership.TryParseSetName(setName, out _, out _).Should().BeFalse();

    /// <summary>
    /// A dot is legal here and is not on DigitalOcean — the difference is real, it comes from EC2 tags being
    /// native key/value pairs, and it is asserted rather than left as folklore.
    /// </summary>
    [Theory]
    [InlineData("srv-0001", true)]
    [InlineData("srv_0001", true)]
    [InlineData("srv.0001", true)]
    [InlineData("SRV0001", true)]
    [InlineData("srv:0001", false)]
    [InlineData("srv 0001", false)]
    [InlineData("srv/0001", false)]
    [InlineData("", false)]
    public void Only_ids_that_survive_both_a_set_name_and_a_tag_are_supported(string serverId, bool supported) =>
        EbsSnapshotOwnership.IsSupportedServerId(serverId).Should().Be(supported);

    [Fact]
    public void An_unsupported_server_id_cannot_even_be_named()
    {
        var act = () => EbsSnapshotOwnership.FormatSetName("srv 0001", TakenAt);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*bill forever and are never pruned*");
    }

    [Fact]
    public void The_built_tag_set_carries_all_four_marks_and_is_classified_as_owned()
    {
        var tags = EbsSnapshotOwnership.BuildTags(ServerId, InstanceId, "job-42", "conn-1", SetName);

        tags[EbsSnapshotOwnership.ManagedTag].Should().Be("true");
        tags[EbsSnapshotOwnership.InstanceIdTag].Should().Be(ServerId);
        tags[EbsSnapshotOwnership.SourceInstanceTag].Should().Be(InstanceId);
        tags[EbsSnapshotOwnership.SetTag].Should().Be(SetName);

        EbsSnapshotOwnership.Classify(tags, ServerId, InstanceId).Should().Be(BackupOwnership.Servyx);
    }

    [Fact]
    public void A_set_name_for_another_server_cannot_be_used_to_build_tags()
    {
        var act = () => EbsSnapshotOwnership.BuildTags(
            ServerId,
            InstanceId,
            "job-42",
            "conn-1",
            EbsSnapshotOwnership.FormatSetName("srv-0002", TakenAt));

        act.Should().Throw<ArgumentException>()
            .WithMessage("*could never be classified as Servyx's afterwards*");
    }

    [Fact]
    public void Reading_the_set_name_back_refuses_anything_this_encoding_did_not_write()
    {
        EbsSnapshotOwnership.ReadSetName(AllFourMarks()).Should().Be(SetName);
        EbsSnapshotOwnership.ReadSetName(AllFourMarks(setName: "hand-written")).Should().BeNull();
        EbsSnapshotOwnership.ReadSetName(null).Should().BeNull();
    }
}

/// <summary>
/// Barrier 2: retention cannot even be <em>computed</em> over a backup Servyx does not own.
/// </summary>
public sealed class EbsSnapshotRetentionEvaluatorTests
{
    private static BackupArtifact Artifact(
        string id,
        DateTimeOffset at,
        BackupOwnership ownership = BackupOwnership.Servyx) =>
        new(id, ownership, at, 1024, "aws://ec2/us-east-1/snapshot-sets/" + id);

    [Fact]
    public void A_foreign_artifact_reaching_the_evaluator_throws()
    {
        var artifacts = new[]
        {
            Artifact("a", new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero)),
            Artifact("b", new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero), BackupOwnership.Foreign),
        };

        var act = () => EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 0));

        act.Should().Throw<ForeignEbsSnapshotProtectedException>()
            .WithMessage("*must never be evaluated against a Servyx retention policy*");
    }

    [Fact]
    public void Retention_keeps_the_newest_capture_of_each_of_the_most_recent_days()
    {
        var artifacts = new[]
        {
            Artifact("d1-early", new DateTimeOffset(2026, 7, 27, 02, 0, 0, TimeSpan.Zero)),
            Artifact("d1-late", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d2", new DateTimeOffset(2026, 7, 26, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d3", new DateTimeOffset(2026, 7, 25, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d4", new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero)),
        };

        var removed = EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 3, 0));

        removed.Select(a => a.Id).Should().BeEquivalentTo(["d4", "d1-early"]);
    }

    [Fact]
    public void A_weekly_keep_saves_a_capture_the_daily_keep_would_have_dropped()
    {
        var artifacts = new[]
        {
            Artifact("this-week", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero)),
            Artifact("last-week", new DateTimeOffset(2026, 7, 20, 22, 0, 0, TimeSpan.Zero)),
            Artifact("older", new DateTimeOffset(2026, 7, 13, 22, 0, 0, TimeSpan.Zero)),
        };

        EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 0))
            .Select(a => a.Id).Should().BeEquivalentTo(["older", "last-week"]);

        EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 2))
            .Select(a => a.Id).Should().BeEquivalentTo(["older"]);
    }

    [Fact]
    public void An_hourly_keep_buckets_by_clock_hour()
    {
        var artifacts = new[]
        {
            Artifact("h1", new DateTimeOffset(2026, 7, 27, 10, 5, 0, TimeSpan.Zero)),
            Artifact("h1-later", new DateTimeOffset(2026, 7, 27, 10, 55, 0, TimeSpan.Zero)),
            Artifact("h2", new DateTimeOffset(2026, 7, 27, 11, 5, 0, TimeSpan.Zero)),
        };

        EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(2, 0, 0))
            .Select(a => a.Id).Should().BeEquivalentTo(["h1"]);
    }

    [Fact]
    public void A_policy_that_keeps_nothing_removes_everything() =>
        EbsSnapshotRetentionEvaluator
            .SelectForRemoval([Artifact("a", DateTimeOffset.UnixEpoch)], new RetentionPolicy(0, 0, 0))
            .Should().HaveCount(1);

    [Fact]
    public void A_negative_keep_count_is_refused()
    {
        var act = () => EbsSnapshotRetentionEvaluator.SelectForRemoval([], new RetentionPolicy(0, -1, 0));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// What an EBS snapshot costs — and the reason this adapter will not claim to know.
/// </summary>
public sealed class EbsSnapshotPricingTests
{
    [Fact]
    public void The_monthly_figure_is_the_published_per_gigabyte_rate_applied_to_the_source_volume_size()
    {
        var estimate = EbsSnapshotPricing.Ceiling(130m);

        estimate.Monthly.Should().Be(6.5m);
        estimate.Currency.Should().Be("USD");
    }

    /// <summary>
    /// The confidence is the honesty: the <em>rate</em> is a list price, but the <em>quantity</em> is an upper
    /// bound this adapter derived, so the product is not a list price and must not claim to be one.
    /// </summary>
    [Fact]
    public void The_confidence_is_estimated_and_never_list_price() =>
        EbsSnapshotPricing.Ceiling(130m).Confidence.Should().Be(CostConfidence.Estimated);

    [Fact]
    public void There_is_no_hourly_figure_because_aws_does_not_charge_one() =>
        EbsSnapshotPricing.Ceiling(130m).Hourly.Should().BeNull();

    [Fact]
    public void An_unsized_snapshot_costs_unknown_rather_than_zero()
    {
        var estimate = EbsSnapshotPricing.Ceiling(null);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Monthly.Should().BeNull();
        estimate.Source.Should().Contain("It is still billing");
    }

    [Fact]
    public void The_source_states_that_snapshots_are_incremental_and_the_figure_is_a_ceiling() =>
        EbsSnapshotPricing.Source.Should().Contain("THIS IS A CEILING, NOT A PRICE")
            .And.Contain("INCREMENTAL")
            .And.Contain("not refreshed at runtime")
            .And.Contain("volumeSize field is the SOURCE VOLUME's");

    /// <summary>
    /// The sentence a later capture gets is the one that matters: quoting a first-snapshot-sized number for the
    /// tenth nightly snapshot is the specific overstatement this whole class exists to avoid.
    /// </summary>
    [Fact]
    public void A_later_capture_is_described_as_costing_a_fraction_of_the_ceiling() =>
        EbsSnapshotPricing.DescribeMonthlyCeiling(130m, isFirstOfChain: false)
            .Should().Contain("Cost ceiling")
            .And.Contain("UPPER BOUND, not a price")
            .And.Contain("only what changed")
            .And.Contain("small fraction");

    [Fact]
    public void The_first_capture_is_described_as_the_closest_one_gets_to_the_ceiling() =>
        EbsSnapshotPricing.DescribeMonthlyCeiling(130m, isFirstOfChain: true)
            .Should().Contain("$6.50 USD per month")
            .And.Contain("closest any capture gets to the ceiling");

    [Fact]
    public void An_unsized_capture_is_described_without_a_fabricated_number() =>
        EbsSnapshotPricing.DescribeMonthlyCeiling(null)
            .Should().Contain("not even a ceiling")
            .And.Contain("billing regardless");
}
