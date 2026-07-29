using System.Text;
using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class DockerBackupProviderRestoreTests
{
    private static async Task<(BackupScenario Scenario, DockerBackupProvider Provider, string BackupId)> BackedUpAsync()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(BackupScenario.ServerId);
        return (scenario, provider, artifact.Id);
    }

    [Fact]
    public async Task PlanRestoreAsync_names_every_affected_path_and_changes_nothing()
    {
        var (scenario, provider, backupId) = await BackedUpAsync();
        scenario.Data.Target.ClearReceivedCalls();
        scenario.Compose.Target.ClearReceivedCalls();
        var before = scenario.Data.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

        var plan = await provider.PlanRestoreAsync(backupId);

        plan.BackupId.Should().Be(backupId);
        plan.AffectedPaths.Should().BeEquivalentTo([
            "/palworld/Pal/Saved/SaveGames/0/Level.sav",
            "/palworld/Pal/Saved/SaveGames/0/LevelMeta.sav",
            "/palworld/Pal/Saved/SaveGames/0/Players/abc.sav",
            "/palworld/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
            "/srv/palworld/.env",
            "/srv/palworld/compose.yaml",
        ]);

        scenario.Data.Paths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(before);
        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Compose.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_writes_each_entry_back_to_the_source_it_came_from()
    {
        var (scenario, provider, backupId) = await BackedUpAsync();
        var plan = await provider.PlanRestoreAsync(backupId);

        scenario.Data.With("Pal/Saved/SaveGames/0/Level.sav", "corrupted");
        scenario.Compose.With(".env", "SERVER_NAME=wrong");

        await provider.RestoreAsync(plan.Id);

        Encoding.UTF8.GetString(scenario.Data.Read("Pal/Saved/SaveGames/0/Level.sav")).Should().Be("level");
        Encoding.UTF8.GetString(scenario.Compose.Read(".env")).Should().Be("SERVER_NAME=test");
    }

    [Fact]
    public async Task RestoreAsync_refuses_an_unknown_plan_id()
    {
        var (_, provider, _) = await BackedUpAsync();

        var act = async () => await provider.RestoreAsync("restore-does-not-exist");

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.RestorePlanId.Should().Be("restore-does-not-exist");
    }

    [Fact]
    public async Task A_restore_plan_is_single_use()
    {
        var (_, provider, backupId) = await BackedUpAsync();
        var plan = await provider.PlanRestoreAsync(backupId);

        await provider.RestoreAsync(plan.Id);
        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<RestorePlanStaleException>();
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_that_has_expired()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider(planTtl: TimeSpan.FromMinutes(5));
        var artifact = await provider.CreateAsync(BackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(6);
        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<RestorePlanStaleException>();
        Encoding.UTF8.GetString(scenario.Data.Read("Pal/Saved/SaveGames/0/Level.sav")).Should().Be("level");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_whose_archive_changed_underneath_it()
    {
        var (scenario, provider, backupId) = await BackedUpAsync();
        var plan = await provider.PlanRestoreAsync(backupId);

        scenario.Data.With(
            "servyx-backups/servyx-20260727T101500Z.tar.gz",
            BackupScenario.ForeignArchiveBytes("data/Pal/Saved/SaveGames/0/Level.sav", "data/compose.yaml"));

        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<RestorePlanStaleException>();
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_whose_backup_was_deleted()
    {
        var (scenario, provider, backupId) = await BackedUpAsync();
        var plan = await provider.PlanRestoreAsync(backupId);

        await scenario.Data.Target.DeleteAsync(scenario.Data.Path("servyx-backups/servyx-20260727T101500Z.tar.gz"));

        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<RestorePlanStaleException>();
    }

    [Fact]
    public async Task PlanRestoreAsync_refuses_a_foreign_archive_that_declares_no_restore_mapping()
    {
        var scenario = new BackupScenario().WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        var provider = scenario.Provider();

        var act = async () => await provider.PlanRestoreAsync(scenario.ForeignBackupId("cron.tar.gz"));

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task A_foreign_archive_with_a_declared_mapping_is_restorable()
    {
        var scenario = new BackupScenario { ForeignRestoreSourceId = "data" }.WithPalworldLayout();
        scenario.Data.With(
            "backups/cron.tar.gz",
            BackupScenario.ForeignArchiveBytes("Pal/Saved/SaveGames/0/Level.sav"),
            new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var plan = await provider.PlanRestoreAsync(scenario.ForeignBackupId("cron.tar.gz"));
        plan.AffectedPaths.Should().Equal("/palworld/Pal/Saved/SaveGames/0/Level.sav");

        await provider.RestoreAsync(plan.Id);

        Encoding.UTF8.GetString(scenario.Data.Read("Pal/Saved/SaveGames/0/Level.sav"))
            .Should().Be("payload:Pal/Saved/SaveGames/0/Level.sav");

        // Restoring from a foreign archive still never touches the foreign archive itself.
        await scenario.Data.Target.DidNotReceive().DeleteAsync(
            Arg.Is<TargetPath>(p => p.Value.StartsWith("backups/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        scenario.Data.Has("backups/cron.tar.gz").Should().BeTrue();
    }
}
