using System.Security.Cryptography;
using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Creating a backup: the archive is produced by the host's own <c>tar</c>, written straight into the
/// artifact directory, and described by a sidecar manifest.
/// </summary>
public class SshBackupProviderCreateTests
{
    private const string ArchiveName = "servyx-20260729T101500Z.tar.gz";
    private const string ArchivePath = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/{ArchiveName}";

    [Fact]
    public async Task CreateAsync_writes_a_servyx_owned_archive_and_its_manifest()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        artifact.Ownership.Should().Be(BackupOwnership.Servyx);
        artifact.Location.Should().Be(ArchivePath);
        artifact.Id.Should().Be(SshBackupScenario.ServyxBackupId(ArchiveName));
        artifact.CreatedAt.Should().Be(scenario.Clock.Now);

        scenario.Host.Has(ArchivePath).Should().BeTrue();
        scenario.Host.Has(ArchivePath + SshBackupProvider.ManifestSuffix).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_captures_the_include_set_minus_the_excludes()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var entries = SshBackupScenario.EntryNamesOf(scenario.Host.Read(ArchivePath));

        entries.Should().Contain([
            "./worlds_local/Dedicated.db",
            "./worlds_local/Dedicated.fwl",
            "./config/server.cfg",
        ]);
        entries.Should().NotContain("./logs/server.log", "the definition excludes the log directory");
    }

    [Fact]
    public async Task CreateAsync_never_archives_the_artifact_directory_so_archives_are_not_re_archived()
    {
        // A previous archive is already sitting in the artifact directory, and the include set is the whole
        // root. Without the store-directory exclusion, every backup would contain every backup before it.
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var entries = SshBackupScenario.EntryNamesOf(scenario.Host.Read(ArchivePath));

        entries.Should().NotContain(name => name.Contains(SshBackupScenario.StoreDirectory, StringComparison.Ordinal));
        entries.Should().NotContain(name => name.Contains("servyx-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_never_archives_a_declared_foreign_archive_directory_either()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron-2026-07-20.tar.gz");

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var entries = SshBackupScenario.EntryNamesOf(scenario.Host.Read(ArchivePath));

        entries.Should().NotContain(name => name.Contains("cron-backups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_excludes_the_artifact_directory_on_the_command_line_not_after_the_fact()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var create = scenario.Host.Commands.Single(c =>
            c.Executable == "tar" && c.Arguments.Contains("--create"));

        create.Arguments.Should().Contain($"--exclude={SshBackupScenario.StoreDirectory}");
        create.Arguments.Should().Contain($"--exclude={SshBackupScenario.StoreDirectory}/*");
        create.Arguments.Should().Contain("--exclude=cron-backups");
        create.Arguments.Should().Contain(["--directory", SshBackupScenario.Root]);
    }

    [Fact]
    public async Task CreateAsync_produces_the_archive_on_the_host_and_never_pulls_it_across_the_wire()
    {
        // The whole point of the remote-tar design: the archive's bytes are written by the host, so the only
        // file the provider ever reads back is nothing at all, and the only one it writes is the manifest.
        var scenario = new SshBackupScenario().WithGameLayout();

        await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        scenario.Host.Journal.Should().NotContain(entry => entry.StartsWith("read:", StringComparison.Ordinal));
        scenario.Host.Journal.Where(e => e.StartsWith("write:", StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.Should().EndWith(SshBackupProvider.ManifestSuffix);
    }

    [Fact]
    public async Task CreateAsync_records_the_archives_real_content_hash_and_size_in_the_manifest()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        var artifact = await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        var archive = scenario.Host.Read(ArchivePath);
        var manifest = BackupManifest.FromUtf8Json(scenario.Host.Read(ArchivePath + SshBackupProvider.ManifestSuffix));

        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().Be(BackupManifest.CurrentSchemaVersion);
        manifest.ServerId.Should().Be(SshBackupScenario.ServerId);
        manifest.ArchiveFileName.Should().Be(ArchiveName);
        manifest.ArchiveRoot.Should().Be(SshBackupScenario.Root);
        manifest.ArchiveSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(archive)));
        manifest.ArchiveSizeBytes.Should().Be(archive.LongLength);
        artifact.SizeBytes.Should().Be(archive.LongLength);

        manifest.Entries.Should().BeEquivalentTo([
            "config/server.cfg",
            "worlds_local/Dedicated.db",
            "worlds_local/Dedicated.fwl",
        ]);
    }

    [Fact]
    public async Task CreateAsync_twice_in_the_same_second_does_not_overwrite_the_first_archive()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();

        var first = await provider.CreateAsync(SshBackupScenario.ServerId);
        var second = await provider.CreateAsync(SshBackupScenario.ServerId);

        first.Location.Should().NotBe(second.Location);
        scenario.Host.Has(first.Location).Should().BeTrue();
        scenario.Host.Has(second.Location).Should().BeTrue();

        SshBackupScenario.EntryNamesOf(scenario.Host.Read(second.Location))
            .Should().NotContain(name => name.Contains("servyx-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_removes_the_partial_archive_and_reports_the_failure_when_tar_fails()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var host = scenario.Host;

        host.ExecOverride = spec =>
        {
            if (spec.Executable != "tar" || !spec.Arguments.Contains("--create"))
            {
                return null;
            }

            // Model a tar that got far enough to touch the file before dying, which is the case that matters:
            // a truncated tarball with a manifest beside it is a backup that lies.
            host.With(ArchivePath, "truncated");
            return new CommandResult(2, string.Empty, "tar: /srv/valheim/worlds_local: Cannot open: Permission denied", TimeSpan.Zero);
        };

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<SshBackupCommandFailedException>())
            .Which.StandardError.Should().Contain("Permission denied");

        host.Has(ArchivePath).Should().BeFalse("a failed archive is removed, not left to look like a backup");
        host.Has(ArchivePath + SshBackupProvider.ManifestSuffix).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_refuses_a_wildcard_include_rather_than_silently_capturing_nothing()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        scenario.Include = ["worlds_local/*.db"];

        var act = async () => await scenario.Provider().CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no shell to expand them");

        scenario.Host.Commands.Should().BeEmpty("the include set is validated before anything runs on the host");
        await scenario.Host.Target.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_refuses_a_context_whose_artifact_directory_is_the_root_it_backs_up()
    {
        var scenario = new SshBackupScenario().WithGameLayout();
        var source = new StaticSshContextSource(scenario.Build() with { StoreDirectory = "/" });
        var provider = new SshBackupProvider(source, null, scenario.Clock);

        var act = async () => await provider.CreateAsync(SshBackupScenario.ServerId);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("the next archive would then contain the previous one");
    }
}
