using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Listing and inspecting: read-only questions, answered from the sidecar manifest rather than by opening
/// the archive.
/// </summary>
public class SshBackupProviderInspectTests
{
    [Fact]
    public async Task ListAsync_reports_servyx_archives_and_adopted_ones_with_their_own_ownership()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithForeignArchives("cron.tar.gz")
            .WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));

        var artifacts = await scenario.ProviderWithForeign("cron.tar.gz").ListAsync(SshBackupScenario.ServerId);

        artifacts.Should().HaveCount(2);
        artifacts.Should().ContainSingle(a => a.Ownership == BackupOwnership.Servyx)
            .Which.Location.Should().Be($"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260728T030000Z.tar.gz");
        artifacts.Should().ContainSingle(a => a.Ownership == BackupOwnership.Foreign)
            .Which.Location.Should().Be($"{SshBackupScenario.ForeignDirectory}/cron.tar.gz");
    }

    [Fact]
    public async Task ListAsync_ignores_the_sidecar_manifests_themselves()
    {
        var scenario = new SshBackupScenario()
            .WithGameLayout()
            .WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));

        var artifacts = await scenario.Provider().ListAsync(SshBackupScenario.ServerId);

        artifacts.Should().ContainSingle();
        artifacts.Should().NotContain(a => a.Location.EndsWith(SshBackupProvider.ManifestSuffix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_is_empty_rather_than_failing_when_the_artifact_directory_does_not_exist_yet()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        (await scenario.Provider().ListAsync(SshBackupScenario.ServerId)).Should().BeEmpty();
    }

    [Fact]
    public async Task InspectAsync_reads_the_manifest_and_never_opens_the_archive()
    {
        // The seeded "archive" is not a tarball at all — it is nine bytes of nonsense. Inspect still answers,
        // which is only possible if it read the sidecar and never touched the archive.
        var at = new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);
        var scenario = new SshBackupScenario().WithGameLayout().WithServyxArchives(at);
        var archivePath = $"{SshBackupScenario.Root}/{SshBackupScenario.StoreDirectory}/servyx-20260728T030000Z.tar.gz";

        var entries = await scenario.Provider().InspectAsync(SshBackupScenario.ServyxBackupId("servyx-20260728T030000Z.tar.gz"));

        entries.Should().Equal("worlds_local/Dedicated.db");

        await scenario.Host.Target.DidNotReceive().OpenReadAsync(
            Arg.Is<TargetPath>(p => ("/" + p.Value) == archivePath),
            Arg.Any<CancellationToken>());
        scenario.Host.Commands.Should().NotContain(c => c.Executable == "tar");
    }

    [Fact]
    public async Task InspectAsync_extracts_nothing_and_writes_nothing()
    {
        var scenario = new SshBackupScenario().WithGameLayout().WithServyxArchives(new DateTimeOffset(2026, 7, 28, 3, 0, 0, TimeSpan.Zero));
        var before = scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).ToList();

        await scenario.Provider().InspectAsync(SshBackupScenario.ServyxBackupId("servyx-20260728T030000Z.tar.gz"));

        scenario.Host.Paths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal(before);
        await scenario.Host.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Host.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InspectAsync_falls_back_to_the_archives_own_headers_when_the_manifest_is_missing()
    {
        // A real archive, written by the provider, whose sidecar has since been removed by something outside
        // Servyx. The manifest is an index, not the authority.
        var scenario = new SshBackupScenario().WithGameLayout();
        var provider = scenario.Provider();
        var artifact = await provider.CreateAsync(SshBackupScenario.ServerId);

        scenario.Host.Remove(artifact.Location + SshBackupProvider.ManifestSuffix);
        scenario.Host.Commands.Clear();

        var entries = await provider.InspectAsync(artifact.Id);

        entries.Should().Contain("worlds_local/Dedicated.db");
        entries.Should().NotContain(name => name.EndsWith('/'), "directory members carry nothing a restore would overwrite");
        scenario.Host.Commands.Should().ContainSingle(c => c.Executable == "tar")
            .Which.Arguments.Should().Contain("--list").And.NotContain("--extract");
    }

    [Fact]
    public async Task InspectAsync_refuses_an_id_this_provider_never_issued()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().InspectAsync("not-an-id");

        await act.Should().ThrowAsync<BackupNotFoundException>();
    }

    [Fact]
    public async Task InspectAsync_refuses_an_id_whose_archive_no_longer_exists()
    {
        var scenario = new SshBackupScenario().WithGameLayout();

        var act = async () => await scenario.Provider().InspectAsync(SshBackupScenario.ServyxBackupId("servyx-19700101T000000Z.tar.gz"));

        (await act.Should().ThrowAsync<BackupNotFoundException>())
            .Which.BackupId.Should().Be(SshBackupScenario.ServyxBackupId("servyx-19700101T000000Z.tar.gz"));
    }
}
