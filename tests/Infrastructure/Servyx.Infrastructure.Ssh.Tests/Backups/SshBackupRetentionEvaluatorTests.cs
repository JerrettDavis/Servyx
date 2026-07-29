using Servyx.Domain.Backups;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Retention as pure computation: the same call answers both the dry run and the live run, so the two
/// cannot disagree.
/// </summary>
public class SshBackupRetentionEvaluatorTests
{
    private static BackupArtifact Servyx(string id, DateTimeOffset at) =>
        new(id, BackupOwnership.Servyx, at, 1, $"/srv/valheim/servyx-backups/{id}.tar.gz");

    private static DateTimeOffset At(int day, int hour) => new(2026, 7, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keeping_nothing_releases_everything_oldest_first()
    {
        var artifacts = new List<BackupArtifact> { Servyx("b", At(29, 3)), Servyx("a", At(28, 3)) };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 0, 0));

        removed.Select(a => a.Id).Should().Equal("a", "b");
    }

    [Fact]
    public void Hourly_retention_keeps_the_newest_artifact_in_each_recent_hour()
    {
        var artifacts = new List<BackupArtifact>
        {
            Servyx("h10a", At(29, 10)),
            Servyx("h11", At(29, 11)),
            Servyx("h12", At(29, 12)),
            Servyx("h09", At(29, 9)),
        };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(2, 0, 0));

        removed.Select(a => a.Id).Should().Equal("h09", "h10a");
    }

    [Fact]
    public void One_nightly_backup_counts_as_that_days_daily_and_that_weeks_weekly()
    {
        // 2026-07-20 through 2026-07-29 spans three ISO weeks. Keeping one day and two weeks must not
        // double-charge the newest artifact against both granularities.
        var artifacts = new List<BackupArtifact>
        {
            Servyx("jul13", new DateTimeOffset(2026, 7, 13, 3, 0, 0, TimeSpan.Zero)),
            Servyx("jul20", new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero)),
            Servyx("jul29", new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero)),
        };

        var removed = BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(0, 1, 2));

        removed.Select(a => a.Id).Should().Equal("jul13");
    }

    [Fact]
    public void An_empty_set_releases_nothing()
    {
        BackupRetentionEvaluator.SelectForRemoval([], new RetentionPolicy(0, 0, 0)).Should().BeEmpty();
    }

    [Fact]
    public void A_negative_keep_count_is_rejected_rather_than_treated_as_zero()
    {
        var act = () => BackupRetentionEvaluator.SelectForRemoval([], new RetentionPolicy(-1, 0, 0));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_foreign_artifact_anywhere_in_the_candidate_set_throws_rather_than_being_filtered_out()
    {
        var artifacts = new List<BackupArtifact>
        {
            Servyx("a", At(28, 3)),
            new("cron", BackupOwnership.Foreign, At(29, 3), 1, "/srv/valheim/cron-backups/cron.tar.gz"),
            Servyx("b", At(29, 3)),
        };

        var act = () => BackupRetentionEvaluator.SelectForRemoval(artifacts, new RetentionPolicy(6, 7, 4));

        act.Should().Throw<ForeignBackupProtectedException>()
            .Which.Location.Should().Be("/srv/valheim/cron-backups/cron.tar.gz");
    }
}
