using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// The write-guard interaction, which matters more here than it does for Docker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SshBackupProvider"/>'s mutating step <em>is</em> an exec, which is why
/// <see cref="CommandSpec.Intent"/> exists: <c>tar --create</c> declares <see cref="CommandIntent.Mutating"/>
/// and <see cref="WriteGuardedExecutionTarget"/> refuses it structurally, while <c>tar --list</c> declares
/// <see cref="CommandIntent.ReadOnly"/> so inspecting a backup keeps working on a read-only server. On top of
/// that the provider refuses the whole operation up front, so the refusal arrives before the quiesce rather
/// than partway through. These tests pin the refusal to <em>before</em> the first command, with the reads
/// still working.
/// </para>
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

        scenario.Host.Commands.Should().BeEmpty("the provider refuses up front, ahead of the guard's own refusal at tar");
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

    /// <summary>
    /// Pins the exact exception type a caller (the Backups page, through <c>IBackupDashboard.ApplyRestoreAsync</c>)
    /// must be able to catch: a server with no <see cref="Servyx.Domain.Transport.WriteModeGrant"/> refuses a
    /// restore with <see cref="WritesDisabledException"/>, not some other failure mode, and nothing is written.
    /// </summary>
    [Fact]
    public async Task Restore_is_refused_without_a_write_grant()
    {
        var enabled = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();
        var artifact = await enabled.Provider().CreateAsync(SshBackupScenario.ServerId);

        var readOnly = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout();
        CopyStore(enabled, readOnly);

        var provider = readOnly.Provider();
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain(SshBackupScenario.ServerId);
        readOnly.Host.Commands.Should().NotContain(c => c.Arguments.Contains("--extract"));
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

    [Fact]
    public async Task Every_command_a_read_only_server_does_run_declares_itself_read_only()
    {
        // The other half of the guarantee. Refusing the mutating operations is worth nothing if the operations
        // that remain available only work because the channel is open: they work because each command says what
        // it does, and the guard would refuse any of them that did not.
        // The archive is copied across without its manifest sidecar, so inspecting it cannot be answered from
        // the manifest and has to ask the host's tar to list the entries — the read-only exec this whole
        // design exists to keep working.
        var enabled = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();
        await enabled.Provider().CreateAsync(SshBackupScenario.ServerId);

        var scenario = new SshBackupScenario(WriteMode.ReadOnly).WithGameLayout();
        CopyStore(enabled, scenario, includeManifests: false);

        var provider = scenario.Provider();
        var artifact = (await provider.ListAsync(SshBackupScenario.ServerId)).Should().ContainSingle().Which;
        scenario.Host.Commands.Clear();

        (await provider.InspectAsync(artifact.Id)).Should().NotBeEmpty();

        scenario.Host.Commands.Should().NotBeEmpty("inspecting has to reach the host to be worth anything");
        scenario.Host.Commands.Should().OnlyContain(c => c.Intent == CommandIntent.ReadOnly);
    }

    [Fact]
    public async Task The_archive_command_declares_itself_mutating_so_the_guard_alone_would_refuse_it()
    {
        // Pins the structural half: were the provider's up-front check ever removed, tar --create would still
        // meet a refusal at the transport rather than writing an archive onto a read-only host.
        var scenario = new SshBackupScenario(WriteMode.Enabled).WithGameLayout();

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var archive = scenario.Host.Commands.Should()
            .ContainSingle(c => c.Arguments.Contains("--create")).Which;
        archive.Intent.Should().Be(CommandIntent.Mutating);

        var readOnlyGuard = new WriteGuardedExecutionTarget(
            scenario.Host.Target, WriteMode.ReadOnly, SshBackupScenario.ServerId);
        var act = async () => await readOnlyGuard.ExecuteAsync(archive);

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    private static void CopyStore(SshBackupScenario from, SshBackupScenario to, bool includeManifests = true)
    {
        var prefix = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/";
        foreach (var path in from.Host.Paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (!includeManifests && path.EndsWith(SshBackupProvider.ManifestSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            to.Host.With(path, from.Host.Read(path), new DateTimeOffset(2026, 7, 29, 10, 15, 0, TimeSpan.Zero));
        }
    }
}
