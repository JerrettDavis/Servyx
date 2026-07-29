using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes backups the way <c>Program.cs</c>'s gated block does, on both sides of the gate.
/// </summary>
/// <remarks>
/// The failures guarded against here appear for the first time when an operator sets
/// <c>Servyx:Provisioning:Enabled</c> to <c>true</c> — or, worse, when they do not and something is
/// registered anyway.
/// </remarks>
public class BackupCompositionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void The_gated_block_resolves_a_provider_backed_dashboard_and_a_scheduler()
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:palworld-server:WriteMode", "Enabled"),
            ("Servyx:Servers:palworld-server:Backup:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Backup:IntervalMinutes", "60"));

        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeTrue();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(WritableServers.FromConfiguration(configuration, gate));

        // Stands in for AddServyxDockerBackups(), which needs a Docker daemon and a context source.
        // Everything below it is the real registration.
        services.AddSingleton<IBackupProvider>(new ScriptedBackupProvider());
        services.AddServyxBackupDashboard();

        var schedule = BackupScheduleOptions.FromConfiguration(configuration, gate);
        services.AddSingleton(schedule);
        services.AddHostedService(sp => new ScheduledBackupService(
            schedule,
            sp.GetRequiredService<ILogger<ScheduledBackupService>>(),
            sp.GetService<IBackupDashboard>(),
            sp.GetService<TimeProvider>()));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetRequiredService<IBackupDashboard>().ProviderConfigured.Should().BeTrue();
        provider.GetRequiredService<WritableServers>().IsWritable("palworld-server").Should().BeTrue();

        var scheduler = provider.GetServices<IHostedService>().OfType<ScheduledBackupService>().Should().ContainSingle().Subject;
        scheduler.WillRun.Should().BeTrue();
    }

    [Fact]
    public void The_read_only_composition_registers_no_backup_capability_at_all()
    {
        // Exactly what Program.cs composes when the flag is absent: the gate and the writable-server
        // label, and nothing from inside the `if`.
        var configuration = Config(
            ("Servyx:Servers:palworld-server:WriteMode", "Enabled"),
            ("Servyx:Servers:palworld-server:Backup:Enabled", "true"));

        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeFalse();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(gate);
        services.AddSingleton(WritableServers.FromConfiguration(configuration, gate));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        // No provider, no dashboard, no scheduler — not a disabled one, not one that refuses: none.
        provider.GetService<IBackupProvider>().Should().BeNull();
        provider.GetService<IBackupDashboard>().Should().BeNull();
        provider.GetServices<IHostedService>().Should().BeEmpty();

        // And the write grant the configuration asks for is not honoured either, so nothing on the page
        // can claim a server is writable.
        provider.GetRequiredService<WritableServers>().Any.Should().BeFalse();
    }

    [Fact]
    public void A_write_grant_is_matched_by_either_the_server_id_or_its_container_name()
    {
        var writable = new WritableServers(["palworld-server"]);

        writable.IsWritable("palworld-server").Should().BeTrue();
        writable.IsWritable("c0ffee123456", "palworld-server").Should().BeTrue();
        writable.IsWritable("some-other-server").Should().BeFalse();
        writable.IsWritable(null, null).Should().BeFalse();
    }

    [Fact]
    public void The_servyx_artifact_directory_may_never_be_the_directory_another_mechanism_owns()
    {
        var act = () => new BackupWiringOptions(storeDirectory: "backups", foreignDirectory: "backups");

        act.Should().Throw<ArgumentException>().WithMessage("*must differ*");
    }

    [Fact]
    public void The_wiring_defaults_keep_servyx_archives_out_of_the_capture_set()
    {
        var options = BackupWiringOptions.FromConfiguration(Config());

        options.StoreDirectory.Should().Be(BackupWiringOptions.DefaultStoreDirectory);
        options.ForeignDirectory.Should().Be(BackupWiringOptions.DefaultForeignDirectory);
        options.StoreDirectory.Should().NotBe(options.ForeignDirectory);
        options.Include.Should().NotContain(p => p == "**");
    }
}
