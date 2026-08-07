using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// The ssh+docker half of the per-server write mode: <see cref="ServerWriteModes"/> emits grants keyed on the
/// docker-transport container-option spellings, and this is the sibling that emits grants keyed on the single
/// option spelling (<c>containerName</c>) and transport id (<c>ssh+docker</c>) a remote-container descriptor
/// actually carries.
/// </summary>
/// <remarks>
/// <see cref="The_grant_actually_matches_the_hosts_real_descriptor"/> is the load-bearing test in this file:
/// it builds the exact <see cref="TargetDescriptor"/> <see cref="SshDockerWiringOptions.FromConfiguration"/>
/// produces — the same one <c>AddServyxSshDocker</c> registers and <see cref="WriteGuardedTransport"/>
/// resolves against — and proves a grant actually matches it. A grant that type-checks but is keyed on the
/// wrong option name would pass every other test here while never matching anything at runtime.
/// </remarks>
public class SshDockerWriteModesTests
{
    private const string HostKey = "testhost";
    private const string Endpoint = "ssh:user@10.0.0.9:22";
    private const string ContainerName = "palworld-server";

    private static IConfiguration Configuration(params (string Key, string Value)[] extra)
    {
        var entries = new Dictionary<string, string?>
        {
            [$"Servyx:Hosts:{HostKey}:Enabled"] = "true",
            [$"Servyx:Hosts:{HostKey}:Transport"] = "ssh+docker",
            [$"Servyx:Hosts:{HostKey}:Endpoint"] = Endpoint,
            [$"Servyx:Hosts:{HostKey}:Container"] = ContainerName,
        };

        foreach (var (key, value) in extra)
        {
            entries[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private static SshDockerWiringOptions Hosts(IConfiguration configuration) =>
        SshDockerWiringOptions.FromConfiguration(configuration, NullLogger.Instance);

    [Fact]
    public void No_grant_is_emitted_when_the_provisioning_gate_is_closed()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "Enabled"));

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, ProvisioningGate.Closed, Hosts(configuration), NullLogger.Instance);

        grants.Should().BeEmpty();
    }

    [Fact]
    public void A_writable_server_matching_a_configured_host_emits_one_narrow_grant()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "Enabled"));

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), NullLogger.Instance);

        var grant = grants.Should().ContainSingle().Subject;

        grant.Mode.Should().Be(WriteMode.Enabled);
        grant.TransportId.Should().Be("ssh+docker",
            "WriteGuardedTransport resolves against the outer descriptor before SshDockerTransport rewrites " +
            "TransportId to \"ssh\" one layer further in");
        grant.Endpoint.Should().Be(Endpoint);
        grant.RequiredOptions.Should().ContainKey("containerName")
            .WhoseValue.Should().Be(ContainerName);
    }

    [Fact]
    public void The_grant_actually_matches_the_hosts_real_descriptor()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "Enabled"));
        var hosts = Hosts(configuration);

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), hosts, NullLogger.Instance);

        // The exact descriptor AddServyxSshDocker registers as TargetDescriptor and WriteGuardedTransport
        // resolves against — not a hand-built stand-in.
        var realDescriptor = hosts.Hosts.Should().ContainSingle().Subject.Target;
        realDescriptor.TransportId.Should().Be("ssh+docker");

        var resolver = new GrantedWriteModeResolver(grants);

        resolver.Resolve(realDescriptor).Should().Be(WriteMode.Enabled,
            "an option-key mismatch between SshDockerWriteModes and SshDockerWiringOptions would make the " +
            "grant compile and pass every other test while silently never applying at runtime");
    }

    [Fact]
    public void A_grant_does_not_match_a_different_container_on_the_same_host()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "Enabled"));
        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), NullLogger.Instance);

        var differentContainer = new TargetDescriptor(
            "ssh+docker",
            Endpoint,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = "some-other-server" });

        new GrantedWriteModeResolver(grants).Resolve(differentContainer).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void A_grant_does_not_match_the_same_container_on_a_different_endpoint()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "Enabled"));
        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), NullLogger.Instance);

        var differentEndpoint = new TargetDescriptor(
            "ssh+docker",
            "ssh:user@10.0.0.99:22",
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = ContainerName });

        new GrantedWriteModeResolver(grants).Resolve(differentEndpoint).Should().Be(WriteMode.ReadOnly);
    }

    [Fact]
    public void Preview_only_still_emits_a_preview_grant()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "PreviewOnly"));

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), NullLogger.Instance);

        var grant = grants.Should().ContainSingle().Subject;
        grant.Mode.Should().Be(WriteMode.PreviewOnly);
    }

    [Fact]
    public void An_unparseable_write_mode_fails_closed_and_warns()
    {
        var configuration = Configuration(($"Servyx:Servers:{ContainerName}:WriteMode", "yes"));
        var logger = new RecordingLogger();

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), logger);

        grants.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains(ContainerName)
            && e.Message.Contains("WriteMode")
            && e.Message.Contains("yes"));
    }

    [Fact]
    public void A_writable_server_matching_no_host_warns()
    {
        // A host is configured, but for a different container than the one granted a write mode — the
        // "operator typo'd the container name" shape this warning exists to catch.
        var configuration = Configuration(("Servyx:Servers:some-other-server:WriteMode", "Enabled"));
        var logger = new RecordingLogger();

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), Hosts(configuration), logger);

        grants.Should().BeEmpty();
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("some-other-server")
            && e.Message.Contains("Servyx:Hosts"));
    }

    [Fact]
    public void No_configured_host_yields_no_grants_and_no_warnings_even_with_a_writable_server()
    {
        // With no ssh+docker host configured at all, this is a pure local-Docker deployment: a writable
        // server here almost certainly targets the "docker" transport via ServerWriteModes, not this one, so
        // staying silent is correct rather than a missed warning.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Servyx:Servers:{ContainerName}:WriteMode"] = "Enabled",
            })
            .Build();
        var logger = new RecordingLogger();

        var grants = SshDockerWriteModes.ReadGrants(
            configuration, new ProvisioningGate(enabled: true), SshDockerWiringOptions.None, logger);

        grants.Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void The_shipped_appsettings_grants_no_write_modes()
    {
        // The production-safety regression guard: whatever ServerWriteModes, SshDockerWriteModes and
        // SshBackupWiringOptions.WriteGrants derive from the REAL, deployed appsettings.json must be empty,
        // and a resolver built over all three together must refuse to write anywhere at all.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(path).Should().BeTrue("this test asserts against the real, deployed appsettings.json");

        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var gate = ProvisioningGate.FromConfiguration(configuration);
        var logger = new RecordingLogger();

        var hosts = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        var serverGrants = ServerWriteModes.ReadGrants(configuration, gate, logger);
        var sshDockerGrants = SshDockerWriteModes.ReadGrants(configuration, gate, hosts, logger);
        var sshBackupGrants = SshBackupWiringOptions.FromConfiguration(configuration, gate).WriteGrants;

        serverGrants.Should().BeEmpty();
        sshDockerGrants.Should().BeEmpty();
        sshBackupGrants.Should().BeEmpty();

        var services = new ServiceCollection();
        foreach (var grant in serverGrants.Concat(sshDockerGrants).Concat(sshBackupGrants))
        {
            services.AddSingleton(grant);
        }
        services.AddSingleton<IWriteModeResolver>(
            sp => new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IWriteModeResolver>();

        var anyDescriptor = new TargetDescriptor(
            "ssh+docker",
            Endpoint,
            null,
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["containerName"] = ContainerName });

        resolver.Resolve(anyDescriptor).Should().Be(WriteMode.ReadOnly);
    }
}
