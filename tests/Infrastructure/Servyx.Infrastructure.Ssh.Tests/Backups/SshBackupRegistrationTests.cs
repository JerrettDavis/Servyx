using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Backups;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Ssh.Backups;

namespace Servyx.Infrastructure.Ssh.Tests.Backups;

/// <summary>
/// Registration is opt-in. A composition root that has not said the word "backups" has no backup provider,
/// which is what keeps a flag-off host byte-for-byte read-only.
/// </summary>
public class SshBackupRegistrationTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        return services;
    }

    [Fact]
    public void AddServyxSsh_alone_registers_no_backup_provider()
    {
        var provider = BaseServices().AddServyxSsh().BuildServiceProvider();

        provider.GetService<IBackupProvider>().Should().BeNull(
            "creating, restoring and pruning backups are mutating capabilities and must not arrive with the transport");
    }

    [Fact]
    public void AddServyxSshBackups_registers_the_provider()
    {
        var services = BaseServices().AddServyxSsh();
        services.AddSingleton(Substitute.For<ISshBackupContextSource>());
        services.AddServyxSshBackups();

        var resolved = services.BuildServiceProvider().GetRequiredService<IBackupProvider>();

        resolved.Should().BeOfType<SshBackupProvider>();
    }

    [Fact]
    public void AddServyxSshBackups_registers_no_adopter_of_its_own()
    {
        // Docker registers PalworldCronBackupAdopter because that image genuinely ships a cron job. A generic
        // SSH host has no such convention, and guessing which of a stranger's tarballs are backups would be
        // worse than surfacing none.
        var services = BaseServices().AddServyxSsh();
        services.AddSingleton(Substitute.For<ISshBackupContextSource>());
        services.AddServyxSshBackups();

        services.BuildServiceProvider().GetServices<IBackupAdopter>().Should().BeEmpty();
    }

    [Fact]
    public void AddServyxSshBackups_fails_loudly_when_the_host_registered_no_context_source()
    {
        // A plausible-looking default would silently back up the wrong paths, so there is none.
        var provider = BaseServices().AddServyxSsh().AddServyxSshBackups().BuildServiceProvider();

        var act = () => provider.GetRequiredService<IBackupProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(ISshBackupContextSource));
    }

    [Fact]
    public void The_provider_consults_every_adopter_a_composition_root_registers()
    {
        var services = BaseServices().AddServyxSsh();
        services.AddSingleton(Substitute.For<ISshBackupContextSource>());
        services.AddSingleton<IBackupAdopter>(new StubForeignAdopter(SshBackupScenario.DeploymentKind));
        services.AddServyxSshBackups();

        var built = services.BuildServiceProvider();

        built.GetServices<IBackupAdopter>().Should().ContainSingle();
        built.GetRequiredService<IBackupProvider>().Should().BeOfType<SshBackupProvider>();
    }
}
