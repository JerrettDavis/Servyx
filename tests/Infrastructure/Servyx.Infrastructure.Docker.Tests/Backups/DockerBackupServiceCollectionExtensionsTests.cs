using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Backups;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class DockerBackupServiceCollectionExtensionsTests
{
    [Fact]
    public void AddServyxDocker_alone_registers_no_backup_capability()
    {
        var services = new ServiceCollection();
        services.AddServyxDocker();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IBackupProvider>().Should().BeNull();
        provider.GetService<IBackupAdopter>().Should().BeNull();
    }

    [Fact]
    public void AddServyxDockerBackups_registers_the_provider_and_the_adopter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDockerBackupContextSource>(new BackupScenario().Source());
        services.AddServyxDockerBackups();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBackupProvider>().Should().BeOfType<DockerBackupProvider>();
        provider.GetServices<IBackupAdopter>().Should().ContainSingle()
            .Which.Should().BeOfType<PalworldCronBackupAdopter>();
    }

    [Fact]
    public void AddServyxDockerBackups_is_idempotent_for_the_adopter()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDockerBackupContextSource>(new BackupScenario().Source());
        services.AddServyxDockerBackups();
        services.AddServyxDockerBackups();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IBackupAdopter>().Should().ContainSingle();
    }
}
