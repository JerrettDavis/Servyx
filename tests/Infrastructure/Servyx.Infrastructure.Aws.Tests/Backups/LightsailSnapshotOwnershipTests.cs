using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Backups;

namespace Servyx.Infrastructure.Aws.Tests.Backups;

/// <summary>
/// The classifier on its own: the four marks, and every way of failing one of them.
/// </summary>
/// <remarks>
/// The prune suite exercises the same marks end to end, which is the assertion that matters for data safety.
/// These are the unit-level counterpart, and they exist because a classifier is cheap to test exhaustively and
/// expensive to get wrong: everything it does not positively recognise is protected forever, and everything it
/// recognises wrongly can be deleted.
/// </remarks>
public sealed class LightsailSnapshotOwnershipTests
{
    private const string ServerId = "srv-0001";
    private const string InstanceName = "palworld-01";

    private static readonly DateTimeOffset TakenAt = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, string> AllTags(string serverId = ServerId) =>
        new(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = serverId,
            ["servyx.job-id"] = "job-42",
            ["servyx.connector-id"] = "conn-1",
        };

    private static string Name(string serverId = ServerId) =>
        LightsailSnapshotOwnership.FormatSnapshotName(serverId, TakenAt);

    [Fact]
    public void All_four_marks_together_are_the_only_thing_that_makes_a_snapshot_servyxs()
    {
        LightsailSnapshotOwnership
            .Classify(InstanceName, Name(), AllTags(), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Servyx);
    }

    [Fact]
    public void A_snapshot_of_a_different_instance_is_foreign_even_with_every_tag_and_the_right_name()
    {
        LightsailSnapshotOwnership
            .Classify("someone-elses-box", Name(), AllTags(), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void A_snapshot_lightsail_reports_no_source_instance_for_is_foreign()
    {
        LightsailSnapshotOwnership
            .Classify(null, Name(), AllTags(), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    [Theory]
    [InlineData("servyx.managed")]
    [InlineData("servyx.instance-id")]
    public void A_snapshot_missing_an_ownership_tag_is_foreign(string missing)
    {
        var tags = AllTags();
        tags.Remove(missing);

        LightsailSnapshotOwnership
            .Classify(InstanceName, Name(), tags, ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    /// <summary>
    /// The managed marker is an exact ordinal match, not a truthiness test — the output of this classifier feeds
    /// a delete list, and "TRUE" is not something Servyx wrote.
    /// </summary>
    [Theory]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    public void The_managed_marker_is_matched_exactly(string value)
    {
        var tags = AllTags();
        tags["servyx.managed"] = value;

        LightsailSnapshotOwnership
            .Classify(InstanceName, Name(), tags, ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void A_snapshot_belonging_to_a_different_servyx_server_is_foreign_to_this_one()
    {
        LightsailSnapshotOwnership
            .Classify(InstanceName, Name("srv-0002"), AllTags("srv-0002"), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    [Theory]
    [InlineData("renamed-by-hand")]
    [InlineData("servyx-snapshot-srv-0001")]
    [InlineData("servyx-snapshot-srv-0001-not-a-timestamp")]
    [InlineData("servyx-snapshot-srv-0001-20260727T100000")]
    public void A_snapshot_whose_name_this_adapter_did_not_write_is_foreign(string name)
    {
        LightsailSnapshotOwnership
            .Classify(InstanceName, name, AllTags(), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    /// <summary>
    /// A Lightsail automatic snapshot cannot carry a tag at all — AWS does not allow it — so it is foreign by
    /// construction rather than by policy. This pins the consequence: an untagged snapshot of this very instance,
    /// however plausibly named, is never Servyx's.
    /// </summary>
    [Fact]
    public void An_untagged_snapshot_of_this_instance_is_foreign_however_it_is_named()
    {
        LightsailSnapshotOwnership
            .Classify(InstanceName, Name(), new Dictionary<string, string>(StringComparer.Ordinal), ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);

        LightsailSnapshotOwnership
            .Classify(InstanceName, Name(), null, ServerId, InstanceName)
            .Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void A_name_round_trips_through_its_own_parser()
    {
        var name = LightsailSnapshotOwnership.FormatSnapshotName(ServerId, TakenAt);

        name.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");

        LightsailSnapshotOwnership.TryParseSnapshotName(name, out var serverId, out var takenAt).Should().BeTrue();
        serverId.Should().Be(ServerId);
        takenAt.Should().Be(TakenAt);
    }

    /// <summary>
    /// The one real divergence from the EBS adapter's charset, and it is an API constraint rather than a style
    /// choice: an EBS backup set name lives in a tag value, where EC2 permits <c>.</c>, but a Lightsail snapshot's
    /// identity is its resource name, whose published pattern is <c>\w[\w\-]*\w</c>.
    /// </summary>
    [Fact]
    public void A_dot_is_legal_in_an_ebs_set_name_and_illegal_in_a_lightsail_snapshot_name()
    {
        EbsSnapshotOwnership.IsSupportedServerId("srv.0001").Should().BeTrue();
        LightsailSnapshotOwnership.IsSupportedServerId("srv.0001").Should().BeFalse();
    }

    [Theory]
    [InlineData("srv-0001")]
    [InlineData("srv_0001")]
    [InlineData("SRV0001")]
    public void A_supported_server_id_is_letters_digits_hyphen_and_underscore(string serverId) =>
        LightsailSnapshotOwnership.IsSupportedServerId(serverId).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("srv 0001")]
    [InlineData("srv:0001")]
    [InlineData("srv/0001")]
    [InlineData("srv+0001")]
    public void An_unsupported_server_id_is_refused_rather_than_encoded(string? serverId)
    {
        LightsailSnapshotOwnership.IsSupportedServerId(serverId).Should().BeFalse();

        var act = () => LightsailSnapshotOwnership.FormatSnapshotName(serverId!, TakenAt);
        act.Should().Throw<ArgumentException>().WithMessage("*bills forever*");
    }

    [Fact]
    public void A_server_id_longer_than_the_name_budget_is_refused() =>
        LightsailSnapshotOwnership
            .IsSupportedServerId(new string('a', LightsailSnapshotOwnership.MaxServerIdLength + 1))
            .Should().BeFalse();

    /// <summary>
    /// The tag set the create call applies is exactly the canonical Servyx identity — no source-instance tag,
    /// because Lightsail records the source instance itself, and no synthetic name tag, because a snapshot's
    /// identity is already its name.
    /// </summary>
    [Fact]
    public void The_create_tags_are_the_canonical_identity_and_nothing_else()
    {
        var tags = LightsailSnapshotOwnership.BuildTags(ServerId, "job-42", "conn-1");

        tags.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = ServerId,
            ["servyx.job-id"] = "job-42",
            ["servyx.connector-id"] = "conn-1",
        });
    }

    [Fact]
    public void Tags_built_for_an_unsupported_server_id_are_refused_before_any_call_could_be_made()
    {
        var act = () => LightsailSnapshotOwnership.BuildTags("srv 0001", "job-42", "conn-1");
        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// The retention evaluator on its own, and the pinned claim that it keeps the same set the sibling adapters'
/// evaluators would.
/// </summary>
public sealed class LightsailSnapshotRetentionEvaluatorTests
{
    private static readonly DateTimeOffset Day = new(2026, 7, 27, 22, 0, 0, TimeSpan.Zero);

    private static BackupArtifact Servyx(DateTimeOffset at) =>
        new(
            LightsailSnapshotScenario.BackupIdOf(at),
            BackupOwnership.Servyx,
            at,
            120L * 1024 * 1024 * 1024,
            "aws://lightsail/us-east-1/instance-snapshots/x");

    /// <summary>
    /// Barrier 2, asserted directly: a foreign artifact cannot even be <em>evaluated</em> against a retention
    /// policy. Filtering it out quietly would make this function tolerant of a caller that had already lost track
    /// of ownership; throwing means any future path that forgets to partition fails in a test.
    /// </summary>
    [Fact]
    public void A_foreign_artifact_is_refused_at_the_door_rather_than_filtered_out()
    {
        var foreign = new BackupArtifact(
            "srv-0001::taken-by-hand",
            BackupOwnership.Foreign,
            Day,
            0,
            "aws://lightsail/us-east-1/instance-snapshots/taken-by-hand");

        var act = () => LightsailSnapshotRetentionEvaluator.SelectForRemoval(
            [foreign],
            new RetentionPolicy(0, 3, 0));

        act.Should().Throw<ForeignLightsailSnapshotProtectedException>()
            .Which.Location.Should().Be("aws://lightsail/us-east-1/instance-snapshots/taken-by-hand");
    }

    [Fact]
    public void A_daily_policy_keeps_the_newest_capture_of_each_of_the_most_recent_days()
    {
        IReadOnlyList<BackupArtifact> artifacts =
        [
            Servyx(Day.AddDays(-3)),
            Servyx(Day.AddDays(-2)),
            Servyx(Day.AddDays(-1)),
            Servyx(Day.AddHours(-2)),
            Servyx(Day),
        ];

        var removed = LightsailSnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 3, 0));

        removed.Select(a => a.Id).Should().BeEquivalentTo(
        [
            LightsailSnapshotScenario.BackupIdOf(Day.AddDays(-3)),
            LightsailSnapshotScenario.BackupIdOf(Day.AddHours(-2)),
        ]);
    }

    /// <summary>
    /// The pinned cross-adapter claim: this evaluator, the EBS one and the DigitalOcean one bucket identically.
    /// They are separate implementations because infrastructure projects reference <c>Servyx.Domain</c> and
    /// nothing else, so sharing one would need a cross-adapter reference; this is what keeps them from drifting.
    /// </summary>
    [Fact]
    public void It_keeps_the_same_set_the_ebs_evaluator_would()
    {
        IReadOnlyList<BackupArtifact> artifacts =
        [
            Servyx(Day.AddDays(-9)),
            Servyx(Day.AddDays(-3)),
            Servyx(Day.AddHours(-5)),
            Servyx(Day),
        ];

        var policy = new RetentionPolicy(1, 2, 1);

        LightsailSnapshotRetentionEvaluator.SelectForRemoval(artifacts, policy).Select(a => a.Id)
            .Should().BeEquivalentTo(
                EbsSnapshotRetentionEvaluator.SelectForRemoval(artifacts, policy).Select(a => a.Id));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void A_negative_keep_count_is_refused(int hourly, int daily, int weekly)
    {
        var act = () => LightsailSnapshotRetentionEvaluator.SelectForRemoval(
            [Servyx(Day)],
            new RetentionPolicy(hourly, daily, weekly));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// The pricing snapshot: a ceiling that says so, never a price, and never a fabricated hourly rate.
/// </summary>
public sealed class LightsailSnapshotPricingTests
{
    [Fact]
    public void A_ceiling_is_the_source_size_times_the_published_rate_and_carries_the_caveat()
    {
        var ceiling = LightsailSnapshotPricing.Ceiling(120m);

        ceiling.Monthly.Should().Be(6.00m);
        ceiling.Currency.Should().Be("USD");
        ceiling.Confidence.Should().Be(CostConfidence.Estimated, "the quantity is an upper bound the adapter derived");
        ceiling.Hourly.Should().BeNull("AWS bills snapshot storage per GB-month and there is no hourly rate to quote");
        ceiling.Source.Should().Contain("CEILING, NOT A PRICE").And.Contain("INCREMENTAL");
    }

    [Fact]
    public void An_unreported_size_answers_unknown_rather_than_zero()
    {
        var ceiling = LightsailSnapshotPricing.Ceiling(null);

        ceiling.Monthly.Should().BeNull();
        ceiling.Confidence.Should().Be(CostConfidence.Unknown);
        ceiling.Source.Should().Contain("It is still billing");
    }

    /// <summary>
    /// The second and later captures are where quoting a ceiling alone would mislead most, so the sentence says
    /// so in words rather than leaving the reader to infer it from the type name.
    /// </summary>
    [Fact]
    public void A_later_capture_is_described_as_costing_a_fraction_of_the_ceiling()
    {
        LightsailSnapshotPricing.DescribeMonthlyCeiling(120m, isFirstOfChain: false)
            .Should().Contain("small fraction");

        LightsailSnapshotPricing.DescribeMonthlyCeiling(120m, isFirstOfChain: true)
            .Should().Contain("closest any capture gets to the ceiling");
    }

    [Fact]
    public void The_description_says_the_ceiling_counts_attached_disks() =>
        LightsailSnapshotPricing.DescribeMonthlyCeiling(120m)
            .Should().Contain("system disk AND every attached block storage disk");
}
