using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// The write-guard interaction.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WriteGuardedExecutionTarget"/> gates <c>WriteFileAsync</c> and <c>DeleteAsync</c> but
/// deliberately not <c>ExecuteAsync</c>. <see cref="LocalProcessBackupProvider"/> archives in-process and
/// therefore runs no command at all, so the archive write itself is behind the guard — but creating the
/// artifact directory is a bare <see cref="Directory.CreateDirectory(string)"/> that no target mediates.
/// Left to the guard alone, a read-only server would have a directory created for it and only then be
/// refused. These tests pin the refusal to <em>before</em> anything exists, with the reads still working.
/// </para>
/// <para>
/// The snapshot assertions are the load-bearing ones: they compare the whole tree before and after, so
/// "nothing was written" includes directories, temp files, and mtimes, not just the archive.
/// </para>
/// </remarks>
public class LocalProcessBackupProviderWriteGuardTests
{
    private static readonly RetentionPolicy KeepNothing = new(0, 0, 0);

    [Theory]
    [InlineData(WriteMode.ReadOnly)]
    [InlineData(WriteMode.PreviewOnly)]
    public async Task CreateAsync_is_refused_before_a_single_byte_or_directory_exists(WriteMode mode)
    {
        using var scenario = new LocalBackupScenario(mode).WithGameLayout();
        var before = scenario.Snapshot();

        var act = async () => await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain(LocalBackupScenario.ServerId).And.Contain(mode.ToString());

        scenario.Snapshot().Should().Equal(before);
        Directory.Exists(scenario.At(LocalBackupScenario.StoreDirectory))
            .Should().BeFalse("the artifact directory is created by a call the guard cannot see, so the provider must refuse first");
    }

    [Fact]
    public async Task CreateAsync_refusal_names_what_still_works()
    {
        using var scenario = new LocalBackupScenario(WriteMode.ReadOnly).WithGameLayout();

        var act = async () => await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("Listing, inspecting, previewing a restore, and a dry-run prune all remain available");
    }

    [Fact]
    public async Task CreateAsync_succeeds_against_the_same_shape_once_writes_are_enabled()
    {
        using var scenario = new LocalBackupScenario(WriteMode.Enabled).WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        File.Exists(artifact.Location).Should().BeTrue();
        File.Exists(artifact.Location + LocalProcessBackupProvider.ManifestSuffix).Should().BeTrue();
    }

    [Fact]
    public async Task An_unguarded_target_is_allowed_through_because_this_is_not_a_second_policy()
    {
        // The provider's job is to surface a refusal the guard would make anyway, earlier and with a better
        // message — not to invent an independent rule for targets nobody guarded.
        using var scenario = new LocalBackupScenario(writeMode: null).WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        File.Exists(artifact.Location).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreAsync_is_refused_before_extracting_and_the_plan_survives_the_refusal()
    {
        // Planning is a read, so it works on a read-only server. Applying is where the refusal lands — and it
        // must not spend the plan, or an operator who enables writes has to preview all over again.
        using var enabled = new LocalBackupScenario(WriteMode.Enabled).WithGameLayout();
        var artifact = await enabled.Provider().CreateAsync(LocalBackupScenario.ServerId);

        using var scenario = new LocalBackupScenario(WriteMode.ReadOnly).WithGameLayout();
        CopyStore(enabled, scenario);

        var provider = scenario.Provider();
        var plan = await provider.PlanRestoreAsync(scenario.ServyxBackupId(System.IO.Path.GetFileName(artifact.Location)));
        plan.AffectedPaths.Should().NotBeEmpty();

        await File.WriteAllTextAsync(scenario.At("worlds_local", "Dedicated.db"), "corrupted");
        var before = scenario.Snapshot();

        var act = async () => await provider.RestoreAsync(plan.Id);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Snapshot().Should().Equal(before);
        (await File.ReadAllTextAsync(scenario.At("worlds_local", "Dedicated.db"))).Should().Be("corrupted");

        // Same plan id, still applicable — the refusal cost the operator nothing.
        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task Listing_inspecting_and_a_dry_run_prune_all_still_work_on_a_read_only_server()
    {
        using var enabled = new LocalBackupScenario(WriteMode.Enabled).WithGameLayout();
        await enabled.Provider().CreateAsync(LocalBackupScenario.ServerId);

        using var scenario = new LocalBackupScenario(WriteMode.ReadOnly).WithGameLayout().WithForeignArchives("cron.tar.gz");
        CopyStore(enabled, scenario);

        var provider = scenario.ProviderWithForeign("cron.tar.gz");

        var artifacts = await provider.ListAsync(LocalBackupScenario.ServerId);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Servyx);
        artifacts.Should().Contain(a => a.Ownership == BackupOwnership.Foreign);

        var servyxOwned = artifacts.First(a => a.Ownership == BackupOwnership.Servyx);
        (await provider.InspectAsync(servyxOwned.Id)).Should().NotBeEmpty();

        var dryRun = await provider.PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: true);
        dryRun.Removed.Should().ContainSingle();
        dryRun.SkippedForeign.Should().Be(1);
        File.Exists(servyxOwned.Location).Should().BeTrue();
    }

    [Fact]
    public async Task A_live_prune_is_refused_and_deletes_nothing()
    {
        using var enabled = new LocalBackupScenario(WriteMode.Enabled).WithGameLayout();
        await enabled.Provider().CreateAsync(LocalBackupScenario.ServerId);

        using var scenario = new LocalBackupScenario(WriteMode.ReadOnly).WithGameLayout().WithForeignArchives("cron.tar.gz");
        CopyStore(enabled, scenario);
        var before = scenario.Snapshot();

        var act = async () => await scenario.ProviderWithForeign("cron.tar.gz")
            .PruneAsync(LocalBackupScenario.ServerId, KeepNothing, dryRun: false);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task A_composite_target_whose_file_half_is_read_only_is_refused_too()
    {
        using var scenario = new LocalBackupScenario(writeMode: null).WithGameLayout();

        var composite = new CompositeTargetDouble(
            scenario.BareTarget,
            new WriteGuardedExecutionTarget(scenario.BareTarget, WriteMode.ReadOnly, LocalBackupScenario.ServerId));

        var context = scenario.Build() with { Target = composite };
        var provider = new LocalProcessBackupProvider(new StaticLocalContextSource(context));
        var before = scenario.Snapshot();

        var act = async () => await provider.CreateAsync(LocalBackupScenario.ServerId);

        await act.Should().ThrowAsync<WritesDisabledException>();
        scenario.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task A_composite_target_whose_exec_half_is_read_only_is_refused_too()
    {
        // This provider runs no command, so the exec half is not one it uses — but a caller who guarded only
        // that half still said "this server does not mutate", and it is not this type's place to
        // second-guess them.
        using var scenario = new LocalBackupScenario(writeMode: null).WithGameLayout();

        var composite = new CompositeTargetDouble(
            new WriteGuardedExecutionTarget(scenario.BareTarget, WriteMode.ReadOnly, LocalBackupScenario.ServerId),
            scenario.BareTarget);

        var context = scenario.Build() with { Target = composite };
        var provider = new LocalProcessBackupProvider(new StaticLocalContextSource(context));

        var act = async () => await provider.CreateAsync(LocalBackupScenario.ServerId);

        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    /// <summary>Copies one scenario's artifact directory into another's, so a read-only run has something to read.</summary>
    private static void CopyStore(LocalBackupScenario from, LocalBackupScenario to)
    {
        var source = from.At(LocalBackupScenario.StoreDirectory);
        var destination = to.At(LocalBackupScenario.StoreDirectory);
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), overwrite: true);
        }
    }
}
