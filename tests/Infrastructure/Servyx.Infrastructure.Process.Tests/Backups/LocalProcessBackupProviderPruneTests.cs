using Servyx.Domain.Backups;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// The rule this whole component exists to protect: Servyx may list and inspect an archive it did not
/// create, and may never remove, move, or rename one — under either value of <c>dryRun</c>.
/// </summary>
public class LocalProcessBackupProviderPruneTests
{
    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PruneAsync_never_removes_a_foreign_artifact(bool dryRun)
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron-2026-07-20.tar.gz", "cron-2026-07-21.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.ProviderWithForeign("cron-2026-07-20.tar.gz", "cron-2026-07-21.tar.gz");
        var foreignFirst = scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron-2026-07-20.tar.gz");
        var foreignSecond = scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron-2026-07-21.tar.gz");
        var foreignBytes = await File.ReadAllBytesAsync(foreignFirst);

        var result = await provider.PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun);

        // Nothing foreign is even *named* as removable, under either flag.
        result.Removed.Should().NotContain(scenario.ForeignBackupId("cron-2026-07-20.tar.gz"));
        result.Removed.Should().NotContain(scenario.ForeignBackupId("cron-2026-07-21.tar.gz"));
        result.SkippedForeign.Should().Be(2);

        // And nothing foreign left the disk, changed name, or changed content.
        File.Exists(foreignFirst).Should().BeTrue();
        File.Exists(foreignSecond).Should().BeTrue();
        (await File.ReadAllBytesAsync(foreignFirst)).Should().Equal(foreignBytes);

        Directory.EnumerateFileSystemEntries(scenario.At(LocalBackupScenario.ForeignDirectoryName))
            .Select(System.IO.Path.GetFileName)
            .Should().BeEquivalentTo(["cron-2026-07-20.tar.gz", "cron-2026-07-21.tar.gz"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_foreign_directory_is_byte_for_byte_unchanged_by_a_prune(bool dryRun)
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("a.tar.gz", "b.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.ProviderWithForeign("a.tar.gz", "b.tar.gz");
        var before = SnapshotOf(scenario.At(LocalBackupScenario.ForeignDirectoryName));

        await provider.PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun);

        SnapshotOf(scenario.At(LocalBackupScenario.ForeignDirectoryName)).Should().Equal(before);
    }

    [Fact]
    public async Task PruneAsync_reports_skipped_foreign_even_when_nothing_is_prunable()
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("a.tar.gz", "b.tar.gz", "c.tar.gz");

        var provider = scenario.ProviderWithForeign("a.tar.gz", "b.tar.gz", "c.tar.gz");

        var result = await provider.PruneAsync(LocalBackupScenario.ServerId, new RetentionPolicy(6, 7, 4), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);
    }

    [Fact]
    public async Task PruneAsync_dry_run_touches_nothing_at_all()
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.ProviderWithForeign("cron.tar.gz");
        var before = scenario.Snapshot();

        var result = await provider.PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: true);

        result.Removed.Should().HaveCount(2);
        result.SkippedForeign.Should().Be(1);
        scenario.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task PruneAsync_removes_servyx_archives_and_their_manifests()
    {
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(stamp);

        var provider = scenario.ProviderWithForeign("cron.tar.gz");
        var name = LocalBackupScenario.ArchiveNameFor(stamp);

        var result = await provider.PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: false);

        result.Removed.Should().HaveCount(1);
        result.SkippedForeign.Should().Be(1);
        File.Exists(scenario.At(LocalBackupScenario.StoreDirectory, name)).Should().BeFalse();
        File.Exists(scenario.At(LocalBackupScenario.StoreDirectory, name + LocalProcessBackupProvider.ManifestSuffix))
            .Should().BeFalse();
        File.Exists(scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron.tar.gz")).Should().BeTrue();
    }

    [Fact]
    public async Task An_archive_whose_manifest_is_already_gone_is_still_pruned()
    {
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var scenario = new LocalBackupScenario().WithGameLayout().WithServyxArchives(stamp);
        var name = LocalBackupScenario.ArchiveNameFor(stamp);
        File.Delete(scenario.At(LocalBackupScenario.StoreDirectory, name + LocalProcessBackupProvider.ManifestSuffix));

        await scenario.Provider().PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: false);

        File.Exists(scenario.At(LocalBackupScenario.StoreDirectory, name)).Should().BeFalse();
    }

    [Fact]
    public async Task PruneAsync_keeps_the_set_retention_asks_for_and_releases_the_rest()
    {
        // Four daily archives, keep two days: the two newest survive, the two oldest are released, and the
        // dry run's answer is computed by the same call the live run uses.
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(
                new DateTimeOffset(2026, 7, 26, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero));

        var result = await scenario.Provider()
            .PruneAsync(LocalBackupScenario.ServerId, new RetentionPolicy(0, 2, 0), dryRun: false);

        result.Removed.Should().Equal(
            scenario.ServyxBackupId(LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 26, 3, 0, 0, TimeSpan.Zero))),
            scenario.ServyxBackupId(LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero))));

        File.Exists(scenario.At(
            LocalBackupScenario.StoreDirectory,
            LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero)))).Should().BeTrue();
        File.Exists(scenario.At(
            LocalBackupScenario.StoreDirectory,
            LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero)))).Should().BeTrue();
    }

    [Fact]
    public async Task A_daily_backup_also_counts_as_that_weeks_weekly()
    {
        // An artifact survives if any granularity keeps it, so one nightly backup is not double-charged.
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(
                new DateTimeOffset(2026, 7, 13, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero));

        var result = await scenario.Provider()
            .PruneAsync(LocalBackupScenario.ServerId, new RetentionPolicy(0, 1, 3), dryRun: true);

        result.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task PruneAsync_falls_back_to_the_definitions_default_retention_when_given_none()
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(
                new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero));
        scenario.Retention = new RetentionPolicy(0, 1, 0);

        var result = await scenario.Provider().PruneAsync(LocalBackupScenario.ServerId, null!, dryRun: true);

        result.Removed.Should().Equal(
            scenario.ServyxBackupId(LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void Barrier_two_refuses_to_even_consider_a_foreign_artifact()
    {
        var artifacts = new List<BackupArtifact>
        {
            new("a", BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "a.tar.gz"),
            new("b", BackupOwnership.Foreign, DateTimeOffset.UnixEpoch, 1, "b.tar.gz"),
        };

        var act = () => BackupRetentionEvaluator.SelectForRemoval(artifacts, KeepNothing);

        act.Should().Throw<ForeignBackupProtectedException>().Which.Location.Should().Be("b.tar.gz");
    }

    [Fact]
    public void Barrier_two_rejects_a_negative_keep_count_rather_than_treating_it_as_zero()
    {
        var act = () => BackupRetentionEvaluator.SelectForRemoval([], new RetentionPolicy(-1, 0, 0));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task An_adopter_that_reports_a_servyx_owned_artifact_is_refused_outright()
    {
        // The partition is only as good as the labels it partitions on, so an adopter is never allowed to
        // hand back anything but Foreign — otherwise it could talk its way into the prunable half.
        using var scenario = new LocalBackupScenario().WithGameLayout().WithForeignArchives("cron.tar.gz");
        var adopter = new StubForeignAdopter(
            LocalBackupScenario.DeploymentKind,
            scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron.tar.gz"))
        {
            Ownership = BackupOwnership.Servyx,
        };

        var act = async () => await scenario.Provider([adopter])
            .PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: false);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("adopters may only report Foreign artifacts");
        File.Exists(scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron.tar.gz")).Should().BeTrue();
    }

    [Fact]
    public async Task An_adopter_naming_a_directory_nobody_declared_is_ignored_rather_than_trusted()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var stray = scenario.Write("not ours", "somewhere-else", "mystery.tar.gz");
        var adopter = new StubForeignAdopter(LocalBackupScenario.DeploymentKind, stray);

        var artifacts = await scenario.Provider([adopter]).ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_adopter_for_a_different_deployment_kind_is_never_called()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout().WithForeignArchives("cron.tar.gz");
        var adopter = new StubForeignAdopter(
            "docker",
            scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron.tar.gz"));

        var result = await scenario.Provider([adopter])
            .PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: true);

        result.SkippedForeign.Should().Be(0);
    }

    [Fact]
    public async Task A_prune_leaves_the_game_data_itself_untouched()
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var before = SnapshotOf(scenario.At("worlds_local"));

        await scenario.Provider().PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: false);

        SnapshotOf(scenario.At("worlds_local")).Should().Equal(before);
    }

    private static IReadOnlyList<string> SnapshotOf(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var entries = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => $"{System.IO.Path.GetFileName(path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}")
            .ToList();

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }
}
