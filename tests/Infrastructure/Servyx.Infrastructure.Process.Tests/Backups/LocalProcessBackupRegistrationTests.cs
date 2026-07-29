using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Backups;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Process.Backups;
using Servyx.Infrastructure.Process.Provisioning;

namespace Servyx.Infrastructure.Process.Tests.Backups;

/// <summary>
/// Tests for the opt-in
/// <see cref="LocalProcessBackupServiceCollectionExtensions.AddServyxLocalProcessBackups"/> registration.
/// </summary>
/// <remarks>
/// The point of a separate extension method is that a reader of a composition root can tell, without tracing
/// a dependency graph, whether that process can write archives onto its own machine, overwrite live save
/// data, or delete archives. These tests pin that separation.
/// </remarks>
public class LocalProcessBackupRegistrationTests
{
    [Fact]
    public void AddServyxLocalProcess_does_not_make_backups_reachable()
    {
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();

        services.Should().NotContain(d => d.ServiceType == typeof(IBackupProvider));
    }

    [Fact]
    public void AddServyxProcessProvisioning_does_not_make_backups_reachable_either()
    {
        // Provisioning and backups are separate opt-ins: a host that installs servers has not thereby asked
        // for a component that can overwrite their save data.
        var services = new ServiceCollection();
        services.AddServyxLocalProcess();
        services.AddServyxProcessProvisioning();

        services.Should().NotContain(d => d.ServiceType == typeof(IBackupProvider));
    }

    [Fact]
    public void The_registration_publishes_the_provider_under_the_domain_abstraction_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddServyxLocalProcessBackups();

        var descriptor = services.Single(d => d.ServiceType == typeof(IBackupProvider));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationFactory.Should().NotBeNull("the registration must build the provider itself");
    }

    [Fact]
    public void The_registration_resolves_the_context_source_the_composition_root_supplied()
    {
        using var scenario = new LocalBackupScenario();
        var services = new ServiceCollection();
        services.AddSingleton<ILocalBackupContextSource>(scenario.Source());
        services.AddServyxLocalProcessBackups();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBackupProvider>().Should().BeOfType<LocalProcessBackupProvider>();
    }

    [Fact]
    public void The_registration_fails_loudly_when_no_context_source_is_registered()
    {
        // A plausible-looking default would silently back up the wrong paths — or write archives into the
        // directory it is archiving.
        var services = new ServiceCollection();
        services.AddServyxLocalProcessBackups();

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IBackupProvider>();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Registered_adopters_are_handed_to_the_provider()
    {
        using var scenario = new LocalBackupScenario().WithGameLayout().WithForeignArchives("cron.tar.gz");

        var services = new ServiceCollection();
        services.AddSingleton<ILocalBackupContextSource>(scenario.Source());
        services.AddSingleton<IBackupAdopter>(new StubForeignAdopter(
            LocalBackupScenario.DeploymentKind,
            scenario.At(LocalBackupScenario.ForeignDirectoryName, "cron.tar.gz")));
        services.AddServyxLocalProcessBackups();

        using var root = services.BuildServiceProvider();
        var artifacts = await root.GetRequiredService<IBackupProvider>().ListAsync(LocalBackupScenario.ServerId);

        artifacts.Should().ContainSingle().Which.Ownership.Should().Be(BackupOwnership.Foreign);
    }

    [Fact]
    public void The_registration_rejects_a_null_service_collection()
    {
        var act = () => LocalProcessBackupServiceCollectionExtensions.AddServyxLocalProcessBackups(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_backup_id_never_collides_with_a_server_id_that_contains_the_separator()
    {
        var act = () => BackupArtifactId.Format($"a{BackupArtifactId.Separator}b", "somewhere");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-separator-here")]
    [InlineData("::leading")]
    [InlineData("trailing::")]
    public void A_malformed_backup_id_is_rejected_rather_than_half_parsed(string candidate)
    {
        BackupArtifactId.TryGetServerId(candidate, out _).Should().BeFalse();
    }

    [Fact]
    public void A_well_formed_backup_id_round_trips_its_server_id()
    {
        var id = BackupArtifactId.Format("valheim-1", System.IO.Path.Combine("data", "servyx-backups", "a.tar.gz"));

        BackupArtifactId.TryGetServerId(id, out var serverId).Should().BeTrue();
        serverId.Should().Be("valheim-1");
    }

    [Fact]
    public void The_provider_rejects_a_null_context_source()
    {
        var act = () => new LocalProcessBackupProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
