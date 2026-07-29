using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// The write-guard interaction, which matters more here than it does for Docker.
/// </summary>
/// <remarks>
/// <see cref="WriteGuardedExecutionTarget"/> gates <c>WriteFileAsync</c> and <c>DeleteAsync</c> but
/// deliberately not <c>ExecuteAsync</c> — and <see cref="SshBackupProvider"/>'s mutating step <em>is</em> an
/// exec. Left to the guard alone, a read-only server would run <c>tar --create</c>, write a real archive onto
/// the host, and only then fail on the manifest. These tests pin the refusal to <em>before</em> the first
/// command, with the reads still working.
/// </remarks>
public class SshBackupProviderWriteGuardTests
{
    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task CreateAsync_is_refused_before_a_single_command_runs(WriteMode mode)
    {
        var scenario = new SshBackupScenario(mode).WithGameLayout();

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain(SshBackupScenario.ServerId).And.Contain(mode.ToString());

        scenario.Host.Commands.Should().BeEmpty("tar is an exec, and the guard does not gate exec — so the provider must");
        scenario.Host.Paths.Should().NotContain(p => p.Contains(SshBackupScenario.StoreDirectory, StringComparison.Ordinal));
        await scenario.Host.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_refusal_names_what_still_works()
    {
        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout();

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("Listing, inspecting, previewing a restore, and a dry-run prune all remain available");
    }

    [Fact]
    public async Task CreateAsync_succeeds_against_the_same_target_once_writes_are_enabled()
    {
        var scenario = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        scenario.Host.Has(artifact.Location).Should().BeTrue();
        scenario.Host.Has(artifact.Location + SshBackupProvider.ManifestSuffix).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreAsync_is_refused_before_extracting_and_the_plan_survives_the_refusal()
    {
        // Planning is a read, so it works on a read-only server. Applying is where the refusal lands — and it
        // must not spend the plan, or an operator who enables writes has to preview all over again.
        var enabled = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();
        var artifact = await enabled.Provider().CreateAsync(SshBackupScenario.ServerId);

        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout();
        CopyStore(enabled, scenario);

        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(artifact.Id);
        plan.AffectedPaths.Should().NotBeEmpty();

        scenario.Host.With($"{SshBackupScenario.Root}/worlds_local/Dedicated.db", "corrupted");
        scenario.Host.Commands.Clear();

        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Host.Commands.Should().NotContain(c => c.Arguments.Contains("--extract"));
        scenario.Host.ReadText($"{SshBackupScenario.Root}/worlds_local/Dedicated.db").Should().Be("corrupted");

        // Same plan id, still applicable — the refusal cost the operator nothing.
        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Listing_inspecting_and_a_dry_run_prune_all_still_work_on_a_read_only_server()
    {
        var enabled = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();
        await enabled.Provider().CreateAsync(SshBackupScenario.ServerId);

        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout().WithForeignArchives("cron.tar.gz");
        CopyStore(enabled, scenario);

        var provider = scenario.ProviderWithForeign("cron.tar.gz");

        var artifacts = await provider.ListAsync(SshBackupScenario.ServerId);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Servyx);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Foreign);

        var servyxOwned = artifacts.First(a => a.Ownership == BackupOwnership.Servyx);
        (await provider.InspectAsync(servyxOwned.Id)).Should().NotBeEmpty();

        var dryRun = await provider.PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun: true);
        dryRun.Removed.Should().ContainSingle();
        dryRun.SkippedForeign.Should().Be(1);
        scenario.Host.Has(servyxOwned.Location).Should().BeTrue();
    }

    [Fact]
    public async Task A_live_prune_is_refused_and_deletes_nothing()
    {
        var enabled = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();
        await enabled.Provider().CreateAsync(SshBackupScenario.ServerId);

        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout().WithForeignArchives("cron.tar.gz");
        CopyStore(enabled, scenario);

        var act = async () => await scenario.ProviderWithForeign("cron.tar.gz")
            .PruneAsync(SshBackupScenario.ServerId, KeepNothing, dryRun: false);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Host.Paths.Should().Contain(p => p.Contains(SshBackupScenario.StoreDirectory, StringComparison.Ordinal));
        await scenario.Host.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_composite_target_whose_file_half_is_read_only_is_refused_too()
    {
        // The shape a real SSH connector hands out: exec and file are independent channels. A composite whose
        // file half cannot write cannot take a backup, however permissive its exec half looks.
        var host = new SshBackupHost();
        host.With($"{SshBackupScenario.Root}/worlds_local/Dedicated.db", "world");

        var composite = new CompositeExecutionTarget(
            host.Target,
            new WriteGuardedExecutionTarget(host.Target, WriteMode.ReadOnly, SshBackupScenario.ServerId));

        var context = new SshBackupContext(
            SshBackupScenario.ServerId,
            SshBackupScenario.DeploymentKind,
            composite,
            SshBackupScenario.Root,
            ["."],
            [],
            SshBackupScenario.StoreDirectory,
            [],
            new RetentionPolicy(6, 7, 4));

        var provider = new SshBackupProvider(new StaticSshContextSource(context));

        var act = async () => await provider.CreateAsync(SshBackupScenario.ServerId);

        await act.Should().ThrowAsync<WritesDisabledException>();
        host.Commands.Should().BeEmpty();
    }

    private static void CopyStore(SshBackupScenario from, SshBackupScenario to)
    {
        var prefix = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/";
        foreach (var path in from.Host.Paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            to.Host.With(path, from.Host.Read(path), new DateTimeOffset(2026, 7, 29, 10, 15, 0, TimeSpan.Zero));
        }
    }
}
