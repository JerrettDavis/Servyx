using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class PalworldCronBackupAdopterTests
{
    [Fact]
    public void AdapterId_matches_the_definitions_adopt_adapter_value()
    {
        var adopter = new PalworldCronBackupAdopter(new BackupScenario().Source());

        adopter.AdapterId.Should().Be("palworld-docker-cron");
    }

    [Theory]
    [InlineData("docker", true)]
    [InlineData("Docker", true)]
    [InlineData("process", false)]
    [InlineData("", false)]
    public void Supports_only_the_container_deployment_profile(string kind, bool expected)
    {
        var adopter = new PalworldCronBackupAdopter(new BackupScenario().Source());

        adopter.Supports(kind).Should().Be(expected);
    }

    [Fact]
    public async Task DiscoverAsync_finds_the_cron_archives_and_marks_every_one_Foreign()
    {
        var scenario = new BackupScenario()
            .WithPalworldLayout()
            .WithForeignArchives("palworld-2026-07-20.tar.gz", "palworld-2026-07-21.tar.gz");

        var adopter = new PalworldCronBackupAdopter(scenario.Source());

        var artifacts = await adopter.DiscoverAsync(BackupScenario.ServerId);

        artifacts.Should().HaveCount(2);
        artifacts.Should().OnlyContain(a => a.Ownership == BackupOwnership.Foreign);
        artifacts.Select(a => a.Location).Should().BeEquivalentTo([
            "/palworld/backups/palworld-2026-07-20.tar.gz",
            "/palworld/backups/palworld-2026-07-21.tar.gz",
        ]);
    }

    [Fact]
    public async Task DiscoverAsync_is_read_only()
    {
        var scenario = new BackupScenario().WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        var adopter = new PalworldCronBackupAdopter(scenario.Source());

        await adopter.DiscoverAsync(BackupScenario.ServerId);

        await scenario.Data.Target.DidNotReceive().WriteFileAsync(
            Arg.Any<TargetPath>(), Arg.Any<Stream>(), Arg.Any<FileWriteOptions>(), Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().DeleteAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().OpenReadAsync(Arg.Any<TargetPath>(), Arg.Any<CancellationToken>());
        await scenario.Data.Target.DidNotReceive().ExecuteAsync(Arg.Any<CommandSpec>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_ignores_files_that_do_not_match_the_declared_pattern()
    {
        var scenario = new BackupScenario().WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        scenario.Data.With("backups/README.txt", "not an archive");
        scenario.Data.With("backups/partial.tar.gz.tmp", "in progress");

        var adopter = new PalworldCronBackupAdopter(scenario.Source());

        var artifacts = await adopter.DiscoverAsync(BackupScenario.ServerId);

        artifacts.Should().ContainSingle().Which.Location.Should().Be("/palworld/backups/cron.tar.gz");
    }

    [Fact]
    public async Task DiscoverAsync_returns_nothing_when_the_cron_has_never_run()
    {
        var scenario = new BackupScenario().WithPalworldLayout();
        var adopter = new PalworldCronBackupAdopter(scenario.Source());

        var artifacts = await adopter.DiscoverAsync(BackupScenario.ServerId);

        artifacts.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_declines_a_deployment_kind_it_does_not_support()
    {
        var scenario = new BackupScenario().WithPalworldLayout().WithForeignArchives("cron.tar.gz");
        var source = new StaticContextSource(scenario.Build() with { DeploymentKind = "process" });
        var adopter = new PalworldCronBackupAdopter(source);

        var artifacts = await adopter.DiscoverAsync(BackupScenario.ServerId);

        artifacts.Should().BeEmpty();
    }
}
