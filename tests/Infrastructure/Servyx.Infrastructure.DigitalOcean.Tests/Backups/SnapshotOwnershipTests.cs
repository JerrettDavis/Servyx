using Servyx.Domain.Backups;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Backups;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Backups;

/// <summary>
/// The classification that decides whether a DigitalOcean snapshot is Servyx's or somebody else's.
/// </summary>
/// <remarks>
/// Every one of the four ownership marks is removed in isolation below, and every one of those removals must
/// turn the answer to <see cref="BackupOwnership.Foreign"/>. That is the property the whole prune guarantee
/// rests on: a snapshot Servyx cannot positively prove it created is never a deletion candidate, and there is
/// no single mark whose absence can be shrugged off.
/// </remarks>
public sealed class SnapshotOwnershipTests
{
    private const string ServerId = "srv-0001";
    private const long DropletId = 3164494L;

    private static readonly DateTimeOffset TakenAt = new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);

    private static string ServyxName => SnapshotOwnership.FormatName(ServerId, TakenAt);

    private static string[] ServyxTags =>
        [SnapshotOwnership.ManagedTag, SnapshotOwnership.InstanceTag(ServerId)];

    [Fact]
    public void A_snapshot_carrying_all_four_marks_is_servyx_owned() =>
        SnapshotOwnership.Classify("droplet", "3164494", ServyxName, ServyxTags, ServerId, DropletId)
            .Should().Be(BackupOwnership.Servyx);

    [Fact]
    public void A_volume_snapshot_is_foreign_even_with_servyx_marks() =>
        SnapshotOwnership.Classify("volume", "3164494", ServyxName, ServyxTags, ServerId, DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void A_snapshot_of_a_different_droplet_is_foreign_to_this_server() =>
        SnapshotOwnership.Classify("droplet", "9999999", ServyxName, ServyxTags, ServerId, DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void A_snapshot_with_a_hand_written_name_is_foreign_even_when_tagged() =>
        SnapshotOwnership.Classify("droplet", "3164494", "before-the-update", ServyxTags, ServerId, DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void A_servyx_shaped_name_naming_a_different_server_is_foreign() =>
        SnapshotOwnership.Classify(
            "droplet",
            "3164494",
            SnapshotOwnership.FormatName("srv-0002", TakenAt),
            ServyxTags,
            ServerId,
            DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void An_untagged_snapshot_is_foreign_even_with_a_servyx_name() =>
        SnapshotOwnership.Classify("droplet", "3164494", ServyxName, [], ServerId, DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void The_managed_tag_alone_is_not_enough() =>
        SnapshotOwnership.Classify(
            "droplet",
            "3164494",
            ServyxName,
            [SnapshotOwnership.ManagedTag],
            ServerId,
            DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void The_instance_tag_alone_is_not_enough() =>
        SnapshotOwnership.Classify(
            "droplet",
            "3164494",
            ServyxName,
            [SnapshotOwnership.InstanceTag(ServerId)],
            ServerId,
            DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void Another_servers_instance_tag_does_not_confer_ownership() =>
        SnapshotOwnership.Classify(
            "droplet",
            "3164494",
            ServyxName,
            [SnapshotOwnership.ManagedTag, SnapshotOwnership.InstanceTag("srv-0002")],
            ServerId,
            DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void Tag_matching_is_exact_and_case_sensitive() =>
        SnapshotOwnership.Classify(
            "droplet",
            "3164494",
            ServyxName,
            ["SERVYX_MANAGED:TRUE", SnapshotOwnership.InstanceTag(ServerId)],
            ServerId,
            DropletId)
            .Should().Be(BackupOwnership.Foreign);

    [Fact]
    public void The_managed_tag_is_the_same_literal_the_droplet_sweep_uses() =>
        SnapshotOwnership.ManagedTag.Should().Be("servyx_managed:true");

    [Fact]
    public void The_instance_tag_uses_the_droplet_tag_encoding() =>
        SnapshotOwnership.InstanceTag(ServerId).Should().Be("servyx_instance-id:srv-0001");

    [Fact]
    public void A_snapshot_name_round_trips_through_the_encoding()
    {
        var name = SnapshotOwnership.FormatName(ServerId, TakenAt);

        name.Should().Be("servyx-snapshot-srv-0001-20260727T100000Z");
        SnapshotOwnership.TryParseName(name, out var serverId, out var takenAt).Should().BeTrue();
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
    [InlineData("servyx-snapshot-srv.0001-20260727T100000Z")]
    public void A_name_this_encoding_did_not_produce_does_not_parse(string? name) =>
        SnapshotOwnership.TryParseName(name, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("srv-0001", true)]
    [InlineData("srv_0001", true)]
    [InlineData("SRV0001", true)]
    [InlineData("srv.0001", false)]
    [InlineData("srv:0001", false)]
    [InlineData("srv 0001", false)]
    [InlineData("", false)]
    public void Only_ids_that_survive_both_a_name_and_a_tag_are_supported(string serverId, bool supported) =>
        SnapshotOwnership.IsSupportedServerId(serverId).Should().Be(supported);

    [Fact]
    public void An_unsupported_server_id_cannot_even_be_named()
    {
        var act = () => SnapshotOwnership.FormatName("srv.0001", TakenAt);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*bills forever and is never pruned*");
    }
}

/// <summary>
/// Barrier 2: retention cannot even be <em>computed</em> over a snapshot Servyx does not own.
/// </summary>
public sealed class SnapshotRetentionEvaluatorTests
{
    private static BackupArtifact Artifact(string id, DateTimeOffset at, BackupOwnership ownership = BackupOwnership.Servyx) =>
        new(id, ownership, at, 1024, "digitalocean://snapshots/" + id);

    [Fact]
    public void A_foreign_artifact_reaching_the_evaluator_throws()
    {
        var artifacts = new[]
        {
            Artifact("a", new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero)),
            Artifact("b", new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero), BackupOwnership.Foreign),
        };

        var act = () => SnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 0));

        act.Should().Throw<ForeignSnapshotProtectedException>()
            .WithMessage("*must never be evaluated against a Servyx retention policy*");
    }

    [Fact]
    public void Retention_keeps_the_newest_snapshot_of_each_of_the_most_recent_days()
    {
        var artifacts = new[]
        {
            Artifact("d1-early", new DateTimeOffset(2026, 7, 27, 02, 0, 0, TimeSpan.Zero)),
            Artifact("d1-late", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d2", new DateTimeOffset(2026, 7, 26, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d3", new DateTimeOffset(2026, 7, 25, 22, 0, 0, TimeSpan.Zero)),
            Artifact("d4", new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero)),
        };

        var removed = SnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 3, 0));

        removed.Select(a => a.Id).Should().BeEquivalentTo(["d4", "d1-early"]);
    }

    [Fact]
    public void A_weekly_keep_saves_a_snapshot_the_daily_keep_would_have_dropped()
    {
        var artifacts = new[]
        {
            Artifact("this-week", new DateTimeOffset(2026, 7, 27, 22, 0, 0, TimeSpan.Zero)),
            Artifact("last-week", new DateTimeOffset(2026, 7, 20, 22, 0, 0, TimeSpan.Zero)),
            Artifact("older", new DateTimeOffset(2026, 7, 13, 22, 0, 0, TimeSpan.Zero)),
        };

        SnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 0))
            .Select(a => a.Id).Should().BeEquivalentTo(["older", "last-week"]);

        SnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 2))
            .Select(a => a.Id).Should().BeEquivalentTo(["older"]);
    }

    [Fact]
    public void A_policy_that_keeps_nothing_removes_everything()
    {
        var artifacts = new[] { Artifact("a", DateTimeOffset.UnixEpoch) };

        SnapshotRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 0, 0))
            .Should().HaveCount(1);
    }
}

/// <summary>What a snapshot costs, and that the figure is never fabricated.</summary>
public sealed class DigitalOceanSnapshotPricingTests
{
    [Fact]
    public void The_monthly_figure_is_the_published_per_gigabyte_rate()
    {
        var estimate = DigitalOceanSnapshotPricing.For(20m);

        estimate.Monthly.Should().Be(1.2m);
        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
        estimate.Currency.Should().Be("USD");
    }

    [Fact]
    public void There_is_no_hourly_figure_because_digitalocean_does_not_charge_one() =>
        DigitalOceanSnapshotPricing.For(20m).Hourly.Should().BeNull();

    [Fact]
    public void An_unsized_snapshot_costs_unknown_rather_than_zero()
    {
        var estimate = DigitalOceanSnapshotPricing.For(null);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Monthly.Should().BeNull();
        estimate.Source.Should().Contain("It is still billing");
    }

    [Fact]
    public void The_description_says_the_charge_recurs() =>
        DigitalOceanSnapshotPricing.DescribeMonthlyCost(20m)
            .Should().Contain("per month")
            .And.Contain("recurring for as long as this snapshot exists");

    [Fact]
    public void The_source_records_that_the_figure_is_a_stale_able_snapshot() =>
        DigitalOceanSnapshotPricing.Source.Should().Contain("not refreshed at runtime");
}
