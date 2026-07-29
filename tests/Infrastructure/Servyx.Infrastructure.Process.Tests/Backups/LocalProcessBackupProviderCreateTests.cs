using System.Security.Cryptography;
using System.Text;
using Servyx.Domain.Backups;
using Servyx.Infrastructure.Process.Backups;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// What <see cref="LocalProcessBackupProvider.CreateAsync"/> actually puts on disk, asserted against real
/// files in a real temp directory rather than against an argv array.
/// </summary>
public class LocalProcessBackupProviderCreateTests
{
    [Fact]
    public async Task CreateAsync_writes_an_archive_and_its_sidecar_manifest()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.CreatedAt.Should().Be(scenario.Clock.Now);
        File.Exists(artifact.Location).Should().BeTrue();
        File.Exists(artifact.Location + LocalProcessBackupProvider.ManifestSuffix).Should().BeTrue();
        artifact.SizeBytes.Should().Be(new FileInfo(artifact.Location).Length);
    }

    [Fact]
    public async Task The_archive_carries_root_relative_entry_names_with_no_source_prefix()
    {
        // Docker prefixes every entry with its source id because a Docker context spans two independently
        // rooted filesystems. A local install is one directory, so an entry name is just the path under it.
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().Contain("worlds_local/Dedicated.db");
        entries.Should().Contain("config/server.cfg");
        entries.Should().Contain("saves/world.bin");
    }

    [Fact]
    public async Task The_archive_excludes_the_artifact_directory_so_an_archive_never_contains_an_archive()
    {
        // Without this every backup would be strictly larger than the last until the disk filled.
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().NotContain(name => name.StartsWith(LocalBackupScenario.StoreDirectory, StringComparison.Ordinal));
        entries.Should().NotBeEmpty("the rest of the tree is still captured");
    }

    [Fact]
    public async Task The_archive_excludes_a_declared_foreign_archive_directory_too()
    {
        // Servyx never manages those archives, but it does know they are archives; sweeping them into its own
        // would double the size of every backup while adding nothing recoverable.
        using var scenario = new LocalBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron-2026-07-20.tar.gz");

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().NotContain(name => name.StartsWith(LocalBackupScenario.ForeignDirectoryName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Excluded_paths_are_left_out_of_the_archive()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().NotContain("logs/server.log");
    }

    [Fact]
    public async Task A_wildcard_include_is_expanded_rather_than_taken_literally()
    {
        // This is the one behavioural difference from SshBackupProvider, which rejects a wildcard include
        // outright because its includes become argv members of a remote tar with no shell to expand them.
        // Here the provider walks the tree itself, so the pattern means what its author meant.
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = ["**/*.db"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().Equal("worlds_local/Dedicated.db");
    }

    [Fact]
    public async Task A_directory_include_captures_that_subtree_and_nothing_else()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = ["worlds_local"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().BeEquivalentTo(["worlds_local/Dedicated.db", "worlds_local/Dedicated.fwl"]);
    }

    [Fact]
    public async Task A_single_file_include_captures_exactly_that_file()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = ["config/server.cfg"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().Equal("config/server.cfg");
    }

    [Fact]
    public async Task An_include_naming_something_that_does_not_exist_is_skipped_rather_than_fatal()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = ["config/server.cfg", "not-installed-yet"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        entries.Should().Equal("config/server.cfg");
    }

    [Fact]
    public async Task The_manifest_records_a_hash_of_the_bytes_actually_written()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        var archiveBytes = await File.ReadAllBytesAsync(artifact.Location);
        var manifest = BackupManifest.FromUtf8Json(
            await File.ReadAllBytesAsync(artifact.Location + LocalProcessBackupProvider.ManifestSuffix));

        manifest.Should().NotBeNull();
        manifest!.ArchiveSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(archiveBytes)));
        manifest.ArchiveSizeBytes.Should().Be(archiveBytes.LongLength);
        manifest.ServerId.Should().Be(LocalBackupScenario.ServerId);
        manifest.ArchiveRoot.Should().Be(scenario.Root);
        manifest.SchemaVersion.Should().Be(BackupManifest.CurrentSchemaVersion);
    }

    [Fact]
    public async Task The_manifest_lists_the_same_entries_the_archive_holds()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        var manifest = BackupManifest.FromUtf8Json(
            await File.ReadAllBytesAsync(artifact.Location + LocalProcessBackupProvider.ManifestSuffix));
        var entries = LocalBackupScenario.EntryNamesOf(await File.ReadAllBytesAsync(artifact.Location));

        manifest!.Entries.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task Two_backups_taken_in_the_same_second_do_not_collide()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var first = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var second = await provider.CreateAsync(LocalBackupScenario.ServerId);

        second.Location.Should().NotBe(first.Location);
        File.Exists(first.Location).Should().BeTrue();
        File.Exists(second.Location).Should().BeTrue();
    }

    [Fact]
    public async Task A_created_backup_is_immediately_listable()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var artifact = await provider.CreateAsync(LocalBackupScenario.ServerId);
        var listed = await provider.ListAsync(LocalBackupScenario.ServerId);

        listed.Should().ContainSingle().Which.Id.Should().Be(artifact.Id);
    }

    [Fact]
    public async Task A_context_that_names_no_artifact_directory_is_refused()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.StoreDirectoryName = string.Empty;

        var act = async () => await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("the next archive would then contain the previous one");
    }

    [Fact]
    public async Task A_context_that_declares_no_includes_is_refused()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = [];

        var act = async () => await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("nothing to archive");
    }

    [Fact]
    public async Task An_unknown_server_is_reported_as_such_rather_than_dereferenced()
    {
        var source = new NullContextSource();
        var provider = new LocalProcessBackupProvider(source);

        var act = async () => await provider.CreateAsync("no-such-server");

        await act.Should().ThrowAsync<BackupNotFoundException>();
    }

    [Fact]
    public async Task An_archive_is_a_real_gzip_stream_a_third_party_tool_could_open()
    {
        // The point of building the archive with System.Formats.Tar rather than shelling out to a tar binary
        // is portability, not a private format: what lands on disk is an ordinary .tar.gz.
        using var scenario = new LocalBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var bytes = await File.ReadAllBytesAsync(artifact.Location);

        artifact.Location.Should().EndWith(LocalProcessBackupProvider.ArchiveSuffix);
        bytes.Should().HaveCountGreaterThan(2);
        bytes[0].Should().Be(0x1f);
        bytes[1].Should().Be(0x8b);
    }

    [Fact]
    public async Task The_archive_preserves_binary_content_exactly()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Include = ["saves"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var extracted = ArchiveContentOf(await File.ReadAllBytesAsync(artifact.Location), "saves/world.bin");

        extracted.Should().Equal(LocalBackupScenario.BinaryPayload);
    }

    [Fact]
    public async Task A_utf8_file_survives_the_archive_unchanged()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout();
        scenario.Write("naïve — Ω", "config", "unicode.cfg");
        scenario.Include = ["config"];

        var artifact = await scenario.Provider().CreateAsync(LocalBackupScenario.ServerId);
        var extracted = ArchiveContentOf(await File.ReadAllBytesAsync(artifact.Location), "config/unicode.cfg");

        Encoding.UTF8.GetString(extracted).Should().Be("naïve — Ω");
    }

    private static byte[] ArchiveContentOf(byte[] archive, string entryName)
    {
        using var raw = new MemoryStream(archive, writable: false);
        using var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new System.Formats.Tar.TarReader(gzip, leaveOpen: true);

        System.Formats.Tar.TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            if (!string.Equals(entry.Name, entryName, StringComparison.Ordinal) || entry.DataStream is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            entry.DataStream.CopyTo(buffer);
            return buffer.ToArray();
        }

        throw new InvalidOperationException($"Entry '{entryName}' is not in the archive.");
    }

    private sealed class NullContextSource : ILocalBackupContextSource
    {
        public Task<LocalBackupContext> GetAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<LocalBackupContext>(null!);
    }
}
