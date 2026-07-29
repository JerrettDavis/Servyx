using System.Text;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// <c>IBackupProvider.InspectAsync</c> is specified as "reads an archive's index/manifest <em>without
/// extracting its content</em>". These tests make that literal rather than approximate.
/// </summary>
public class LocalProcessBackupProviderInspectTests
{
    [Fact]
    public async Task InspectAsync_reads_the_manifest_without_opening_the_archive_at_all()
    {
        // The proof: replace the archive's bytes with something that is not a gzip stream. If Inspect were
        // decompressing it, this would throw; because it reads the sidecar, it answers as before.
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var expected = await provider.InspectAsync(artifact.Id);
        await File.WriteAllBytesAsync(artifact.Location, Encoding.UTF8.GetBytes("not a gzip stream at all"));

        var entries = await provider.InspectAsync(artifact.Id);

        entries.Should().Equal(expected);
        entries.Should().Contain("worlds_local/Dedicated.db");
    }

    [Fact]
    public async Task InspectAsync_extracts_nothing_onto_the_filesystem()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var before = scenario.Snapshot();
        await provider.InspectAsync(artifact.Id);

        scenario.Snapshot().Should().Equal(before);
    }

    [Fact]
    public async Task InspectAsync_falls_back_to_the_archives_own_headers_when_the_manifest_is_gone()
    {
        // The manifest is an index, not the authority: something moving files around outside Servyx must not
        // make a perfectly good archive unreadable.
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        var expected = await provider.InspectAsync(artifact.Id);
        File.Delete(artifact.Location + LocalProcessBackupProvider.ManifestSuffix);

        var before = scenario.Snapshot();
        var entries = await provider.InspectAsync(artifact.Id);

        entries.Should().BeEquivalentTo(expected);
        scenario.Snapshot().Should().Equal(before, "reading tar headers writes nothing");
    }

    [Fact]
    public async Task InspectAsync_falls_back_when_the_manifest_is_not_a_manifest()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);

        await File.WriteAllTextAsync(artifact.Location + LocalProcessBackupProvider.ManifestSuffix, "{ not: valid");

        var entries = await provider.InspectAsync(artifact.Id);

        entries.Should().Contain("worlds_local/Dedicated.db");
    }

    [Fact]
    public async Task InspectAsync_reports_an_unknown_backup_rather_than_guessing()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().InspectAsync(scenario.ServyxBackupId("servyx-19700101T000000Z.tar.gz"));

        (await act.Should().ThrowAsync<BackupNotFoundException>())
            .Which.BackupId.Should().NotBeNull();
    }

    [Fact]
    public async Task InspectAsync_refuses_an_id_this_provider_never_issued()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().InspectAsync("just-a-path-with-no-server");

        (await act.Should().ThrowAsync<BackupNotFoundException>())
            .Which.Message.Should().Contain("not in a form this provider issued");
    }

    [Fact]
    public async Task ListAsync_surfaces_both_halves_with_the_ownership_each_was_discovered_with()
    {
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));

        var artifacts = await scenario.ProviderWithForeign("cron.tar.gz").ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().HaveCount(2);
        artifacts.Should().ContainSingle(a => a.Ownership == Domain.Backups.BackupOwnership.Servyx);
        artifacts.Should().ContainSingle(a => a.Ownership == Domain.Backups.BackupOwnership.Foreign);
    }

    [Fact]
    public async Task ListAsync_ignores_files_in_the_store_that_are_not_servyx_archives()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Write("someone else's file", LocalBackupScenario.StoreDirectory, "notes.txt");
        scenario.Write("wrong prefix", LocalBackupScenario.StoreDirectory, "backup-2026.tar.gz");

        var artifacts = await scenario.Provider().ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_on_a_server_that_has_never_been_backed_up_is_empty_rather_than_an_error()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifacts = await scenario.Provider().ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().BeEmpty();
        Directory.Exists(scenario.At(LocalBackupScenario.StoreDirectory))
            .Should().BeFalse("listing must not create the directory it lists");
    }

    [Fact]
    public async Task ListAsync_dates_an_archive_from_its_name_rather_than_its_mtime()
    {
        // A file copied off a backup drive gets a new mtime; the name is what says when the backup was taken,
        // and retention buckets by that.
        var stamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        using var scenario = new LocalBackupScenario().WithGameLayout().WithServyxArchives(stamp);

        var artifacts = await scenario.Provider().ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().ContainSingle().Which.CreatedAt.Should().Be(stamp);
    }
}
