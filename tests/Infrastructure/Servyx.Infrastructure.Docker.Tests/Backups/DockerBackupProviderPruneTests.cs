using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

/// <summary>
/// The rule this whole component exists to protect: Servyx may list, inspect, and restore an archive it
/// did not create, and may never remove one.
/// </summary>
public class DockerBackupProviderPruneTests
{
    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PruneAsync_never_removes_a_foreign_artifact(bool dryRun)
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("palworld-2026-07-20.tar.gz", "palworld-2026-07-21.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var result = await provider.PruneAsync(BackupScenario.ServerId, KeepNothing, dryRun);

        // Nothing foreign is even *named* as removable, under either flag.
        result.Removed.Should().NotContain(id => id.Contains("/backups/", StringComparison.Ordinal));
        result.SkippedForeign.Should().Be(2);

        // And nothing foreign left the disk.
        scenario.Data.Has("backups/palworld-2026-07-20.tar.gz").Should().BeTrue();
        scenario.Data.Has("backups/palworld-2026-07-21.tar.gz").Should().BeTrue();

        await scenario.Data.Target.DidNotReceive().DeleteAsync(
            Arg.Is<TargetPath>(p => p.Value.Contains("backups/palworld", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAsync_reports_skipped_foreign_even_when_nothing_is_prunable()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("a.tar.gz", "b.tar.gz", "c.tar.gz");

        var provider = scenario.Provider();

        var result = await provider.PruneAsync(BackupScenario.ServerId, new RetentionPolicy(6, 7, 4), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);
    }

    [Fact]
    public async Task PruneAsync_dry_run_deletes_nothing_at_all()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();
        var before = scenario.Data.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

        var result = await provider.PruneAsync(BackupScenario.ServerId, KeepNothing, dryRun: true);

        result.Removed.Should().HaveCount(2);
        scenario.Data.Paths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(before);
        await scenario.Data.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAsync_removes_servyx_archives_and_their_manifests()
    {
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(stamp);

        var provider = scenario.Provider();

        var result = await provider.PruneAsync(BackupScenario.ServerId, KeepNothing, dryRun: false);

        result.Removed.Should().HaveCount(1);
        result.SkippedForeign.Should().Be(1);
        scenario.Data.Has("servyx-backups/servyx-20260101T000000Z.tar.gz").Should().BeFalse();
        scenario.Data.Has("servyx-backups/servyx-20260101T000000Z.tar.gz.manifest.json").Should().BeFalse();
        scenario.Data.Has("backups/cron.tar.gz").Should().BeTrue();
    }

    [Fact]
    public void Retention_evaluator_refuses_to_even_consider_a_foreign_artifact()
    {
        var artifacts = new List<BackupArtifact>
        {
            new("a", BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "/palworld/servyx-backups/a.tar.gz"),
            new("b", BackupOwnership.Foreign, DateTimeOffset.UnixEpoch, 1, "/palworld/backups/b.tar.gz"),
        };

        var act = () => BackupRetentionEvaluator.SelectForRemoval(artifacts, KeepNothing);

        act.Should().Throw<ForeignBackupProtectedException>()
            .Which.Location.Should().Be("/palworld/backups/b.tar.gz");
    }
}
