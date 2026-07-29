using NSubstitute;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Restores are previewed, then applied by plan id. There is no ad-hoc path: nothing on this provider takes
/// a backup id and starts overwriting files.
/// </summary>
public class SshBackupProviderRestoreTests
{
    [Fact]
    public async Task PlanRestoreAsync_names_every_absolute_path_the_restore_would_overwrite()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);

        var plan = await provider.PlanRestoreAsync(artifact.Id);

        plan.BackupId.Should().Be(artifact.Id);
        plan.AffectedPaths.Should().BeEquivalentTo([
            $"{SshBackupScenario.Root}/config/server.cfg",
            $"{SshBackupScenario.Root}/worlds_local/Dedicated.db",
            $"{SshBackupScenario.Root}/worlds_local/Dedicated.fwl",
        ]);
    }

    [Fact]
    public async Task PlanRestoreAsync_mutates_nothing()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);

        var before = scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var contentBefore = scenario.Host.ReadText($"{SshBackupScenario.Root}/worlds_local/Dedicated.db");
        scenario.Host.Commands.Clear();
        scenario.Host.Target.ClearReceivedCalls();

        await provider.PlanRestoreAsync(artifact.Id);

        scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(before);
        scenario.Host.ReadText($"{SshBackupScenario.Root}/worlds_local/Dedicated.db").Should().Be(contentBefore);
        scenario.Host.Commands.Should().BeEmpty("the plan is answered from the manifest, so no command runs on the host");

        await scenario.Host.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Host.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_puts_the_captured_bytes_back()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Host.With($"{SshBackupScenario.Root}/worlds_local/Dedicated.db", "corrupted");
        scenario.Host.With($"{SshBackupScenario.Root}/config/server.cfg", "name=wrong");

        await provider.RestoreAsync(plan.Id);

        scenario.Host.ReadText($"{SshBackupScenario.Root}/worlds_local/Dedicated.db").Should().Be("world");
        scenario.Host.ReadText($"{SshBackupScenario.Root}/config/server.cfg").Should().Be("name=test");
    }

    [Fact]
    public async Task RestoreAsync_extracts_on_the_host_rather_than_writing_files_over_the_wire()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);
        scenario.Host.Commands.Clear();
        scenario.Host.Target.ClearReceivedCalls();

        await provider.RestoreAsync(plan.Id);

        scenario.Host.Commands.Should().ContainSingle(c => c.Executable == "tar")
            .Which.Arguments.Should().Contain(["--extract", "--directory", SshBackupScenario.Root]);
        await scenario.Host.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_id_it_never_issued()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().RestoreAsync("restore-deadbeef");

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.RestorePlanId.Should().Be("restore-deadbeef");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_that_has_already_been_applied()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("already been applied");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_older_than_its_time_to_live()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider(planTtl: TimeSpan.FromMinutes(5));

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(6);
        scenario.Host.With($"{SshBackupScenario.Root}/worlds_local/Dedicated.db", "corrupted");

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("expired");
        scenario.Host.ReadText($"{SshBackupScenario.Root}/worlds_local/Dedicated.db").Should().Be("corrupted");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_whose_archive_has_changed_since_it_was_previewed()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        // Something replaced the archive between the preview and the apply.
        scenario.Host.With(artifact.Location, "a completely different archive");

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("changed after this plan was computed");
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_whose_archive_has_been_deleted()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);
        scenario.Host.Remove(artifact.Location);

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task RestoreAsync_surfaces_a_failing_tar_rather_than_reporting_success()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Host.ExecOverride = spec => spec.Arguments.Contains("--extract")
            ? new CommandResult(2, string.Empty, "tar: Cannot write: No space left on device", TimeSpan.Zero)
            : null;

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<SshBackupCommandFailedException>())
            .Which.StandardError.Should().Contain("No space left on device");
    }
}
