using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class DockerBackupProviderInspectTests
{
    [Fact]
    public async Task InspectAsync_reads_the_manifest_and_never_opens_the_archive()
    {
        var stamp = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var scenario = new BackupScenario().WithPalworldLayout().WithServyxArchives(stamp);
        var provider = scenario.Provider();

        var entries = await provider.InspectAsync(
            scenario.ServyxBackupId("servyx-20260701T000000Z.tar.gz"));

        entries.Should().Equal("data/Pal/Saved/SaveGames/0/Level.sav");

        // The point of the sidecar: answering "what's in this backup?" costs one small JSON read, and the
        // tarball itself is never opened, let alone decompressed or extracted.
        await scenario.Data.Target.Received(1).OpenReadAsync(
            Arg.Is<TargetPath>(p => p.Value.EndsWith(".manifest.json", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().OpenReadAsync(
            Arg.Is<TargetPath>(p => p.Value.EndsWith(".tar.gz", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InspectAsync_reads_a_foreign_archives_headers_without_extracting_it()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Data.With(
            "backups/palworld-2026-07-20.tar.gz",
            BackupScenario.ForeignArchiveBytes("Pal/Saved/SaveGames/0/Level.sav", "Pal/Saved/SaveGames/0/LevelMeta.sav"),
            new DateTimeOffset(2026, 7, 20, 3, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var entries = await provider.InspectAsync(scenario.ForeignBackupId("palworld-2026-07-20.tar.gz"));

        entries.Should().Equal("Pal/Saved/SaveGames/0/Level.sav", "Pal/Saved/SaveGames/0/LevelMeta.sav");

        // Reading is all it did: nothing was written anywhere, and nothing was deleted.
        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InspectAsync_falls_back_to_tar_headers_when_the_sidecar_is_missing()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Data.With(
            "servyx-backups/servyx-20260701T000000Z.tar.gz",
            BackupScenario.ForeignArchiveBytes("data/Pal/Saved/SaveGames/0/Level.sav"),
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var entries = await provider.InspectAsync(scenario.ServyxBackupId("servyx-20260701T000000Z.tar.gz"));

        entries.Should().Equal("data/Pal/Saved/SaveGames/0/Level.sav");
    }

    [Fact]
    public async Task InspectAsync_rejects_an_id_it_did_not_issue()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();

        var act = async () => await provider.InspectAsync("not-an-id");

        await act.Should().ThrowAsync<BackupNotFoundException>();
    }

    [Fact]
    public async Task InspectAsync_rejects_a_backup_that_no_longer_exists()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();

        var act = async () => await provider.InspectAsync(scenario.ServyxBackupId("servyx-19990101T000000Z.tar.gz"));

        await act.Should().ThrowAsync<BackupNotFoundException>();
    }

    [Fact]
    public async Task ListAsync_returns_both_halves_tagged_with_their_own_ownership()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("palworld-2026-07-20.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var artifacts = await provider.ListAsync(BackupScenario.ServerId);

        artifacts.Should().HaveCount(2);
        artifacts.Should().ContainSingle(a => a.Ownership == BackupOwnership.Servyx)
            .Which.Location.Should().Be("/palworld/servyx-backups/servyx-20260726T040000Z.tar.gz");
        artifacts.Should().ContainSingle(a => a.Ownership == BackupOwnership.Foreign)
            .Which.Location.Should().Be("/palworld/backups/palworld-2026-07-20.tar.gz");
    }

    [Fact]
    public async Task ListAsync_reads_created_at_from_the_archive_name_not_the_file_timestamp()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Data.With(
            "servyx-backups/servyx-20260315T081500Z.tar.gz",
            BackupScenario.ForeignArchiveBytes(),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var provider = scenario.Provider();

        var artifacts = await provider.ListAsync(BackupScenario.ServerId);

        artifacts.Should().ContainSingle()
            .Which.CreatedAt.Should().Be(new DateTimeOffset(2026, 3, 15, 8, 15, 0, TimeSpan.Zero));
    }
}
