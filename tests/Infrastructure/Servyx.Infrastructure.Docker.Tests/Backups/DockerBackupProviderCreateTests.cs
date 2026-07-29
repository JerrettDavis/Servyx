using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class DockerBackupProviderCreateTests
{
    private const string ArchiveName = "servyx-20260727T101500Z.tar.gz";

    [Fact]
    public async Task CreateAsync_archives_the_include_set_across_both_sources()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(BackupScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.CreatedAt.Should().Be(scenario.Clock.Now);
        artifact.Location.Should().Be($"/palworld/servyx-backups/{ArchiveName}");

        var entries = BackupScenario.EntryNamesOf(scenario.Data.Read($"servyx-backups/{ArchiveName}"));
        entries.Should().BeEquivalentTo([
            "data/Pal/Saved/SaveGames/0/Level.sav",
            "data/Pal/Saved/SaveGames/0/LevelMeta.sav",
            "data/Pal/Saved/SaveGames/0/Players/abc.sav",
            "data/Pal/Saved/Config/LinuxServer/PalWorldSettings.ini",
            "compose/.env",
            "compose/compose.yaml",
        ]);

        BackupScenario.EntryContentOf(scenario.Data.Read($"servyx-backups/{ArchiveName}"), "compose/.env")
            .Should().Be("SERVER_NAME=test");
    }

    [Fact]
    public async Task CreateAsync_never_re_archives_the_images_own_backup_directory()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("palworld-2026-07-20.tar.gz", "palworld-2026-07-21.tar.gz");

        var provider = scenario.Provider();

        await provider.CreateAsync(BackupScenario.ServerId);

        var entries = BackupScenario.EntryNamesOf(scenario.Data.Read($"servyx-backups/{ArchiveName}"));
        entries.Should().NotContain(e => e.Contains("backups/", StringComparison.Ordinal));

        // Stronger than "no entry slipped through": the excluded subtree is pruned before traversal, so the
        // walker never even lists it, and never opens any file inside it.
        scenario.Journal.Should().NotContain("list:backups");
        await scenario.Data.Target.DidNotReceive().OpenReadAsync(
            Arg.Is<TargetPath>(p => p.Value.StartsWith("backups/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_excludes_servyx_own_archive_directory_even_when_the_include_set_is_everything()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        var context = scenario.Build();
        var greedy = context with
        {
            Sources =
            [
                new BackupSource("data", scenario.Data.Target, scenario.Data.Root, ["**"], ["backups/**"]),
            ],
        };

        var source = new StaticContextSource(greedy);
        var provider = new DockerBackupProvider(source, [], scenario.Clock);

        await provider.CreateAsync(BackupScenario.ServerId);

        var entries = BackupScenario.EntryNamesOf(scenario.Data.Read($"servyx-backups/{ArchiveName}"));
        entries.Should().NotContain(e => e.Contains("servyx-backups/", StringComparison.Ordinal));
        entries.Should().NotContain(e => e.Contains("backups/", StringComparison.Ordinal));
        entries.Should().Contain("data/Pal/Saved/Logs/Pal.log"); // "**" really did mean everything else.
    }

    [Fact]
    public async Task CreateAsync_writes_a_manifest_recording_what_was_captured_and_the_archive_hash()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();

        await provider.CreateAsync(BackupScenario.ServerId);

        var archive = scenario.Data.Read($"servyx-backups/{ArchiveName}");
        var manifest = BackupManifest.FromUtf8Json(scenario.Data.Read($"servyx-backups/{ArchiveName}.manifest.json"));

        manifest.Should().NotBeNull();
        manifest!.ServerId.Should().Be(BackupScenario.ServerId);
        manifest.CreatedAt.Should().Be(scenario.Clock.Now);
        manifest.ArchiveFileName.Should().Be(ArchiveName);
        manifest.ArchiveSizeBytes.Should().Be(archive.LongLength);
        manifest.ArchiveSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(archive)));
        manifest.Entries.Should().BeEquivalentTo(BackupScenario.EntryNamesOf(archive));
    }

    [Fact]
    public async Task CreateAsync_does_not_overwrite_an_archive_taken_in_the_same_second()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var provider = scenario.Provider();

        var first = await provider.CreateAsync(BackupScenario.ServerId);
        var second = await provider.CreateAsync(BackupScenario.ServerId);

        first.Location.Should().NotBe(second.Location);
        scenario.Data.Has($"servyx-backups/{ArchiveName}").Should().BeTrue();
        scenario.Data.Has("servyx-backups/servyx-20260727T101500Z-2.tar.gz").Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_quiesces_before_it_reads_a_single_file()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                scenario.Journal.Add("quiesce:save");
                return Task.FromResult(new RconResponse("Complete Save", true));
            });

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep("save", null, TimeSpan.FromSeconds(30));

        var provider = scenario.Provider();
        await provider.CreateAsync(BackupScenario.ServerId);

        var quiesceIndex = scenario.Journal.IndexOf("quiesce:save");
        var firstRead = scenario.Journal.FindIndex(e => e.StartsWith("read:", StringComparison.Ordinal));
        var firstWrite = scenario.Journal.FindIndex(e => e.StartsWith("write:", StringComparison.Ordinal));

        quiesceIndex.Should().BeGreaterThanOrEqualTo(0);
        firstRead.Should().BeGreaterThan(quiesceIndex);
        firstWrite.Should().BeGreaterThan(quiesceIndex);

        var manifest = BackupManifest.FromUtf8Json(scenario.Data.Read($"servyx-backups/{ArchiveName}.manifest.json"));
        manifest!.QuiescedWith.Should().Be("save");
    }

    [Fact]
    public async Task CreateAsync_surfaces_a_failed_quiesce_and_writes_nothing()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RconResponse("Unknown command", false)));

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep("save", null, TimeSpan.FromSeconds(30));

        var provider = scenario.Provider();

        var act = async () => await provider.CreateAsync(BackupScenario.ServerId);

        (await act.Should().ThrowAsync<BackupQuiesceFailedException>())
            .Which.CommandId.Should().Be("save");

        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_surfaces_a_throwing_quiesce_and_writes_nothing()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var control = Substitute.For<IRconSession>();
        control.InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("rcon socket closed"));

        scenario.Control = control;
        scenario.Quiesce = new QuiesceStep("save", null, TimeSpan.FromSeconds(30));

        var provider = scenario.Provider();

        var act = async () => await provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_declared_quiesce_with_no_control_channel_is_refused_rather_than_skipped()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Quiesce = new QuiesceStep("save", null, TimeSpan.FromSeconds(30));
        scenario.Control = null;

        var provider = scenario.Provider();

        var act = async () => await provider.CreateAsync(BackupScenario.ServerId);

        await act.Should().ThrowAsync<BackupQuiesceFailedException>();
        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_without_a_declared_quiesce_touches_no_control_channel()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var control = Substitute.For<IRconSession>();
        scenario.Control = control;

        var provider = scenario.Provider();
        await provider.CreateAsync(BackupScenario.ServerId);

        await control.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_captures_file_content_byte_for_byte()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        scenario.Data.With("Pal/Saved/SaveGames/0/Level.sav", Encoding.UTF8.GetBytes("binary\0payload"));

        var provider = scenario.Provider();
        await provider.CreateAsync(BackupScenario.ServerId);

        BackupScenario.EntryContentOf(
                scenario.Data.Read($"servyx-backups/{ArchiveName}"),
                "data/Pal/Saved/SaveGames/0/Level.sav")
            .Should().Be("binary\0payload");
    }
}
