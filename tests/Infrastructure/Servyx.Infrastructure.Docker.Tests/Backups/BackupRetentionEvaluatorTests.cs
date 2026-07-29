using Servyx.Domain.Backups;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class BackupRetentionEvaluatorTests
{
    private static BackupArtifact At(string id, DateTimeOffset when) =>
        new(id, BackupOwnership.Servyx, when, 1, $"/palworld/servyx-backups/{id}.tar.gz");

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keeps_the_newest_artifact_in_each_of_the_most_recent_hours()
    {
        var artifacts = new List<BackupArtifact>
        {
            At("h10", Utc(2026, 7, 27, 10)),
            At("h09", Utc(2026, 7, 27, 9)),
            At("h08", Utc(2026, 7, 27, 8)),
            At("h07", Utc(2026, 7, 27, 7)),
        };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(2, 0, 0));

        removed.Select(a => a.Id).Should().Equal("h07", "h08");
    }

    [Fact]
    public void Keeps_only_the_newest_artifact_within_a_single_bucket()
    {
        var artifacts = new List<BackupArtifact>
        {
            At("early", new DateTimeOffset(2026, 7, 27, 10, 5, 0, TimeSpan.Zero)),
            At("late", new DateTimeOffset(2026, 7, 27, 10, 55, 0, TimeSpan.Zero)),
        };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(1, 0, 0));

        removed.Select(a => a.Id).Should().Equal("early");
    }

    [Fact]
    public void Daily_and_weekly_grants_stack_rather_than_competing()
    {
        // One backup a day for two weeks; keep 2 daily and 2 weekly.
        var artifacts = Enumerable.Range(0, 14)
            .Select(i => At($"d{i:00}", Utc(2026, 7, 27).AddDays(-i)))
            .ToList();

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 2, 2));
        var kept = artifacts.Select(a => a.Id).Except(removed.Select(a => a.Id)).ToList();

        // The two most recent days, plus the newest backup of each of the two most recent ISO weeks.
        // 2026-07-27 is a Monday, so d00 alone occupies its week and d01 opens the previous one.
        kept.Should().BeEquivalentTo(["d00", "d01"]);
    }

    [Fact]
    public void Weekly_retention_reaches_further_back_than_daily()
    {
        var artifacts = Enumerable.Range(0, 28)
            .Select(i => At($"d{i:00}", Utc(2026, 7, 27).AddDays(-i)))
            .ToList();

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 0, 3));
        var kept = artifacts.Select(a => a.Id).Except(removed.Select(a => a.Id)).ToList();

        kept.Should().HaveCount(3);
        kept.Should().Contain("d00");
    }

    [Fact]
    public void A_zero_policy_releases_everything()
    {
        var artifacts = new List<BackupArtifact> { At("a", Utc(2026, 7, 27)), At("b", Utc(2026, 7, 26)) };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 0, 0));

        removed.Should().HaveCount(2);
    }

    [Fact]
    public void A_generous_policy_releases_nothing()
    {
        var artifacts = new List<BackupArtifact> { At("a", Utc(2026, 7, 27)), At("b", Utc(2026, 7, 26)) };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(6, 7, 4));

        removed.Should().BeEmpty();
    }

    [Fact]
    public void Negative_keep_counts_are_rejected()
    {
        var act = () => BackupRetentionEvaluator.SelectForRemoval([], new RetentionPolicy(-1, 0, 0));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
