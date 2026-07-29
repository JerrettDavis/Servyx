using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// The rule this whole component exists to protect: Servyx may list and inspect an archive it did not
/// create, and may never remove one — under either value of <c>dryRun</c>.
/// </summary>
public class SshBackupProviderPruneTests
{
    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PruneAsync_never_removes_a_foreign_artifact(bool dryRun)
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron-2026-07-20.tar.gz", "cron-2026-07-21.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.ProviderWithForeign("cron-2026-07-20.tar.gz", "cron-2026-07-21.tar.gz");

        var result = await provider.PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun);

        // Nothing foreign is even *named* as removable, under either flag.
        result.Removed.Should().NotContain(id => id.Contains("/cron-backups/", StringComparison.Ordinal));
        result.SkippedForeign.Should().Be(2);

        // And nothing foreign left the disk.
        scenario.Host.Has($"{SshBackupScenario.ForeignDirectory}/cron-2026-07-20.tar.gz").Should().BeTrue();
        scenario.Host.Has($"{SshBackupScenario.ForeignDirectory}/cron-2026-07-21.tar.gz").Should().BeTrue();

        await scenario.Host.Target.DidNotReceive().DeleteAsync(
            Arg.Is<TargetPath>(p => p.Value.Contains("cron-backups", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAsync_reports_skipped_foreign_even_when_nothing_is_prunable()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("a.tar.gz", "b.tar.gz", "c.tar.gz");

        var provider = scenario.ProviderWithForeign("a.tar.gz", "b.tar.gz", "c.tar.gz");

        var result = await provider.PruneAsync(SshBackupScenario.ServerId, new RetentionPolicy(6, 7, 4), dryRun: false);

        result.Removed.Should().BeEmpty();
        result.SkippedForeign.Should().Be(3);
    }

    [Fact]
    public async Task PruneAsync_dry_run_deletes_nothing_at_all()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.ProviderWithForeign("cron.tar.gz");
        var before = scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

        var result = await provider.PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun: true);

        result.Removed.Should().HaveCount(2);
        result.SkippedForeign.Should().Be(1);
        scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(before);
        await scenario.Host.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PruneAsync_removes_servyx_archives_and_their_manifests()
    {
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(stamp);

        var provider = scenario.ProviderWithForeign("cron.tar.gz");

        var result = await provider.PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun: false);

        result.Removed.Should().HaveCount(1);
        result.SkippedForeign.Should().Be(1);
        scenario.Host.Has($"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260101T000000Z.tar.gz")
            .Should().BeFalse();
        scenario.Host.Has($"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260101T000000Z.tar.gz.manifest.json")
            .Should().BeFalse();
        scenario.Host.Has($"{SshBackupScenario.ForeignDirectory}/cron.tar.gz").Should().BeTrue();
    }

    [Fact]
    public async Task PruneAsync_keeps_the_set_retention_asks_for_and_releases_the_rest()
    {
        // Four daily archives, keep two days: the two newest survive, the two oldest are released, and the
        // dry run's answer is computed by the same call the live run uses.
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(
                new DateTimeOffset(2026, 7, 26, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 27, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var result = await provider.PruneAsync(SshBackupScenario.ServerId, new RetentionPolicy(0, 2, 0), dryRun: false);

        result.Removed.Should().Equal(
            SshBackupScenario.ServyxBackupId("servyx-20260726T030000Z.tar.gz"),
            SshBackupScenario.ServyxBackupId("servyx-20260727T030000Z.tar.gz"));

        scenario.Host.Has($"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260728T030000Z.tar.gz").Should().BeTrue();
        scenario.Host.Has($"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260729T030000Z.tar.gz").Should().BeTrue();
    }

    [Fact]
    public async Task PruneAsync_falls_back_to_the_definitions_default_retention_when_given_none()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(
                new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 29, 3, 0, 0, TimeSpan.Zero));
        scenario.Retention = new RetentionPolicy(0, 1, 0);

        var result = await scenario.Provider().PruneAsync(SshBackupScenario.ServerId, null!, dryRun: true);

        result.Removed.Should().Equal(SshBackupScenario.ServyxBackupId("servyx-20260728T030000Z.tar.gz"));
    }

    [Fact]
    public void Retention_evaluator_refuses_to_even_consider_a_foreign_artifact()
    {
        var artifacts = new List<BackupArtifact>
        {
            new("a", BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "/srv/valheim/servyx-backups/a.tar.gz"),
            new("b", BackupOwnership.Foreign, DateTimeOffset.UnixEpoch, 1, "/srv/valheim/cron-backups/b.tar.gz"),
        };

        var act = () => BackupRetentionEvaluator.SelectForRemoval(artifacts, KeepNothing);

        act.Should().Throw<ForeignBackupProtectedException>()
            .Which.Location.Should().Be("/srv/valheim/cron-backups/b.tar.gz");
    }

    [Fact]
    public async Task An_adopter_that_reports_a_servyx_owned_artifact_is_refused_outright()
    {
        // The partition is only as good as the labels it partitions on, so an adopter is never allowed to
        // hand back anything but Foreign — otherwise it could talk its way into the prunable half.
        var scenario = new SshBackupScenario().WithGameLayout().WithForeignArchives("cron.tar.gz");
        var adopter = new StubForeignAdopter(SshBackupScenario.DeploymentKind, $"{SshBackupScenario.ForeignDirectory}/cron.tar.gz")
        {
            Ownership = BackupOwnership.Servyx,
        };

        var act = async () => await scenario.Provider([adopter]).PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun: false);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("adopters may only report Foreign artifacts");
        scenario.Host.Has($"{SshBackupScenario.ForeignDirectory}/cron.tar.gz").Should().BeTrue();
    }

    [Fact]
    public async Task An_adopter_naming_a_directory_nobody_declared_is_ignored_rather_than_trusted()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Host.With("/somewhere/else/mystery.tar.gz", "not ours");
        var adopter = new StubForeignAdopter(SshBackupScenario.DeploymentKind, "/somewhere/else/mystery.tar.gz");

        var artifacts = await scenario.Provider([adopter]).ListAsync(SshBackupScenario.ServerId);

        artifacts.Should().BeEmpty();
    }
}
