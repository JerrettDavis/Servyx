using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// Restoring overwrites live save data on the machine the panel itself runs on, so it is planned first and
/// applied only by plan id. These tests pin both halves.
/// </summary>
public class LocalProcessBackupProviderRestoreTests
{
    [Fact]
    public async Task PlanRestoreAsync_names_every_path_a_restore_would_overwrite()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var plan = await provider.PlanRestoreAsync(artifact.Id);

        plan.BackupId.Should().Be(artifact.Id);
        plan.AffectedPaths.Should().Contain(scenario.At("worlds_local", "Dedicated.db"));
        plan.AffectedPaths.Should().Contain(scenario.At("saves", "world.bin"));
        plan.AffectedPaths.Should().NotContain(scenario.At("logs", "server.log"));
    }

    [Fact]
    public async Task PlanRestoreAsync_mutates_nothing()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var before = scenario.Snapshot();
        await provider.PlanRestoreAsync(artifact.Id);

        scenario.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task Two_previews_of_the_same_backup_produce_two_independent_plans()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var first = await provider.PlanRestoreAsync(artifact.Id);
        var second = await provider.PlanRestoreAsync(artifact.Id);

        second.Id.Should().NotBe(first.Id);
    }

    [Fact]
    public async Task RestoreAsync_refuses_a_plan_id_it_never_issued()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().RestoreAsync("restore-deadbeef");

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("unknown or has already been applied");
    }

    [Fact]
    public async Task A_plan_is_single_use()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        await provider.RestoreAsync(plan.Id);

        var act = async () => await provider.RestoreAsync(plan.Id);
        await act.Should().ThrowAsync<RestorePlanStaleException>();
    }

    [Fact]
    public async Task A_plan_expires()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider(planTtl: TimeSpan.FromMinutes(15));
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(16);

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task A_plan_whose_archive_changed_since_it_was_computed_is_refused()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        await File.WriteAllBytesAsync(artifact.Location, Encoding.UTF8.GetBytes("replaced, and a different length"));

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("changed after this plan was computed");
    }

    [Fact]
    public async Task A_plan_whose_archive_was_deleted_since_it_was_computed_is_refused()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var plan = await provider.PlanRestoreAsync(artifact.Id);

        File.Delete(artifact.Location);

        var act = async () => await provider.RestoreAsync(plan.Id);

        (await act.Should().ThrowAsync<RestorePlanStaleException>())
            .Which.Message.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task A_round_trip_restores_every_captured_file_byte_for_byte()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var original = await ReadAllAsync(scenario, "worlds_local/Dedicated.db", "worlds_local/Dedicated.fwl", "config/server.cfg", "saves/world.bin");
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        // Corrupt one file, truncate another, and delete a third along with its directory.
        await File.WriteAllTextAsync(scenario.At("worlds_local", "Dedicated.db"), "corrupted");
        await File.WriteAllBytesAsync(scenario.At("saves", "world.bin"), []);
        Directory.Delete(scenario.At("config"), recursive: true);

        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        var restored = await ReadAllAsync(scenario, "worlds_local/Dedicated.db", "worlds_local/Dedicated.fwl", "config/server.cfg", "saves/world.bin");

        restored.Keys.Should().BeEquivalentTo(original.Keys);
        foreach (var (name, bytes) in original)
        {
            restored[name].Should().Equal(bytes, $"'{name}' must come back byte-for-byte");
        }
    }

    [Fact]
    public async Task A_restore_recreates_a_directory_that_was_removed()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        Directory.Delete(scenario.At("worlds_local"), recursive: true);

        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        File.Exists(scenario.At("worlds_local", "Dedicated.db")).Should().BeTrue();
    }

    [Fact]
    public async Task A_restore_does_not_delete_files_the_archive_never_captured()
    {
        // A restore is an overwrite of what the archive holds, not a sync. A log file written since the
        // backup is not the restore's business.
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        scenario.Write("written after the backup", "logs", "later.log");

        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        File.Exists(scenario.At("logs", "later.log")).Should().BeTrue();
    }

    [Fact]
    public async Task A_restore_leaves_the_archive_it_restored_from_intact()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var archiveBytes = await File.ReadAllBytesAsync(artifact.Location);

        var plan = await provider.PlanRestoreAsync(artifact.Id);
        await provider.RestoreAsync(plan.Id);

        (await File.ReadAllBytesAsync(artifact.Location)).Should().Equal(archiveBytes);
    }

    [Fact]
    public async Task An_archive_whose_entry_name_escapes_the_data_directory_is_refused_at_planning()
    {
        // A hand-made archive is the only way this arises, and it is exactly the shape that would otherwise
        // write over something outside the server's own directory.
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var name = LocalBackupScenario.ArchiveNameFor(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        scenario.Write(HostileArchive("../escaped.txt"), LocalBackupScenario.StoreDirectory, name);

        var provider = scenario.Provider();
        var id = scenario.ServyxBackupId(name);

        var act = async () => await provider.PlanRestoreAsync(id);

        await act.Should().ThrowAsync<PathEscapesSandboxException>();
        File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(scenario.Root)!, "escaped.txt")).Should().BeFalse();
    }

    private static byte[] HostileArchive(string entryName)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var payload = new MemoryStream(Encoding.UTF8.GetBytes("owned"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = payload });
        }

        return buffer.ToArray();
    }

    private static async Task<Dictionary<string, byte[]>> ReadAllAsync(LocalBackupScenario scenario, params string[] relativePaths)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relative in relativePaths)
        {
            var full = scenario.At(relative.Split('/'));
            result[relative] = await File.ReadAllBytesAsync(full);
        }

        return result;
    }
}
