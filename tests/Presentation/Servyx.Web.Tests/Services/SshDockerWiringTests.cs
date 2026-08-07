using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Connectors;
using Servyx.Domain.Discovery;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes <c>AddServyxSshDocker</c> the way <c>Program.cs</c> does — right after <c>AddServyxDocker()</c>
/// — and asserts the write-guard and replacement-vs-no-op properties the whole registration exists for.
/// </summary>
/// <remarks>
/// None of these tests open a real socket. Resolving <see cref="IServerDiscovery"/>, <see cref="ITransport"/>
/// etc. only constructs objects; the SSH connection <c>SshDockerServiceCollectionExtensions</c>'s
/// <c>LazyConnectingExecutionTarget</c> would eventually open happens only on a real <see cref="IExecutionTarget"/>
/// call, which nothing here makes.
/// </remarks>
public class SshDockerWiringTests
{
    private const string HostName = "testhost";
    private const string Endpoint = "ssh:user@10.0.0.9:22";
    private const string CredentialUrn = "secret://host/testhost/ssh/private-key";

    private static IConfiguration Configured() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Servyx:Hosts:{HostName}:Enabled"] = "true",
                [$"Servyx:Hosts:{HostName}:Transport"] = "ssh+docker",
                [$"Servyx:Hosts:{HostName}:Endpoint"] = Endpoint,
                [$"Servyx:Hosts:{HostName}:CredentialUrn"] = CredentialUrn,
                [$"Servyx:Hosts:{HostName}:TrustPolicy"] = "requirePinned",
                [$"Servyx:Hosts:{HostName}:PinnedFingerprints"] = "SHA256:abc123",
                [$"Servyx:Hosts:{HostName}:Container"] = "palworld-server",
            })
            .Build();

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        return services;
    }

    /// <summary>Composes exactly what <c>Program.cs</c> does: Docker first, then ssh+docker over it.</summary>
    private static ServiceProvider ComposedWithHost()
    {
        var services = BaseServices();
        services.AddServyxDocker();
        services.AddServyxSshDocker(SshDockerWiringOptions.FromConfiguration(Configured(), NullLogger.Instance), NullLogger.Instance);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_transport_resolves_to_a_write_guarded_ssh_docker_transport()
    {
        using var provider = ComposedWithHost();

        var transport = provider.GetRequiredService<ITransport>();

        var guarded = transport.Should().BeOfType<WriteGuardedTransport>().Subject;
        guarded.Inner.Should().BeOfType<SshDockerTransport>();
    }

    [Fact]
    public void No_registration_exposes_a_bare_ssh_docker_transport_under_any_service_type()
    {
        var services = BaseServices();
        services.AddServyxDocker();
        services.AddServyxSshDocker(SshDockerWiringOptions.FromConfiguration(Configured(), NullLogger.Instance), NullLogger.Instance);

        services.Where(d => d.ServiceType == typeof(SshDockerTransport)).Should().BeEmpty(
            "wrapping is worth nothing if the inner transport is also resolvable");
    }

    [Fact]
    public async Task Server_discovery_resolves_to_the_ssh_docker_implementation_and_not_the_docker_one()
    {
        // Async disposal: resolving IServerDiscovery constructs the shared LazyConnectingExecutionTarget
        // singleton, which — like every IExecutionTarget — implements only IAsyncDisposable.
        await using var provider = ComposedWithHost();

        provider.GetRequiredService<IServerDiscovery>().Should().BeOfType<SshDockerServerDiscovery>();
    }

    [Fact]
    public void Zero_write_mode_grants_are_registered_and_the_remote_host_stays_read_only()
    {
        using var provider = ComposedWithHost();

        provider.GetServices<WriteModeGrant>().Should().BeEmpty(
            "viewing a remote host is opt-in and declarative, but writing to it is not — that stays behind " +
            "the same per-server WriteModeGrant every other transport requires");

        var resolver = provider.GetRequiredService<IWriteModeResolver>();
        var remoteTarget = provider.GetRequiredService<TargetDescriptor>();

        remoteTarget.TransportId.Should().Be("ssh+docker");
        resolver.Resolve(remoteTarget).Should().Be(WriteMode.ReadOnly);
    }

    /// <summary>
    /// Restore writes files over SFTP/SSH through this same connector, and without <c>FileWrite</c> in the
    /// declared channel set the connector refuses the write before <c>WriteGuardedExecutionTarget</c> — the
    /// layer that actually decides whether a server is allowed to be written to — is ever consulted. See
    /// <see cref="SshDockerWiringOptions.DeclaredChannels"/>'s remarks.
    /// </summary>
    [Fact]
    public void The_ssh_docker_connector_declares_file_write()
    {
        var options = SshDockerWiringOptions.FromConfiguration(Configured(), NullLogger.Instance);

        options.Hosts.Should().ContainSingle().Which.Target.Options["declaredChannels"]
            .Should().Contain("FileWrite");
    }

    /// <summary>
    /// Nothing on this transport streams interactive input — the game console is reached through the
    /// cataloged RCON control channel, never a shell — so <c>Stdin</c> must stay out of the declared set
    /// even now that <c>FileWrite</c> has been added to it.
    /// </summary>
    [Fact]
    public void The_ssh_docker_connector_does_not_declare_stdin()
    {
        var options = SshDockerWiringOptions.FromConfiguration(Configured(), NullLogger.Instance);

        options.Hosts.Should().ContainSingle().Which.Target.Options["declaredChannels"]
            .Should().NotContain("Stdin");
    }

    [Fact]
    public void With_no_hosts_configured_the_docker_registrations_are_left_untouched()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var transportRegistrationsBefore = services.Count(d => d.ServiceType == typeof(ITransport));
        var discoveryRegistrationsBefore = services.Count(d => d.ServiceType == typeof(IServerDiscovery));

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();

        services.AddServyxSshDocker(options, NullLogger.Instance);

        services.Count(d => d.ServiceType == typeof(ITransport)).Should().Be(transportRegistrationsBefore);
        services.Count(d => d.ServiceType == typeof(IServerDiscovery)).Should().Be(discoveryRegistrationsBefore);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IServerDiscovery>().Should().BeOfType<DockerServerDiscovery>();
    }
}

/// <summary>
/// The operator-deception fix: <see cref="SshDockerWiringOptions.FromConfiguration"/> must never silently
/// drop a malformed <c>Servyx:Hosts</c> entry. Every rejected host is reported — by <see cref="LogLevel.Warning"/>
/// when at least one other host is usable, or by throwing when the whole section yields nothing usable — and
/// a second usable host (accepted, but never wired by <c>AddServyxSshDocker</c>) is called out too.
/// </summary>
public class SshDockerWiringReportingTests
{
    private static IConfiguration ConfigurationFrom(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Absent_hosts_section_is_a_silent_no_op()
    {
        var configuration = new ConfigurationBuilder().Build();
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Any.Should().BeFalse();
        logger.Entries.Should().BeEmpty("an install with no Servyx:Hosts section at all is the normal, " +
            "silent, local-only deployment shape — nothing here was even attempted, let alone misconfigured");
    }

    [Fact]
    public void Host_missing_an_endpoint_is_reported_not_silently_skipped()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:valid-host:Enabled", "true"),
            ("Servyx:Hosts:valid-host:Endpoint", "ssh:user@10.0.0.9:22"),
            ("Servyx:Hosts:valid-host:Container", "palworld-server"),
            ("Servyx:Hosts:broken-host:Enabled", "true"),
            ("Servyx:Hosts:broken-host:Transport", "ssh+docker"),
            ("Servyx:Hosts:broken-host:Container", "palworld-server"));
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Hosts.Should().ContainSingle(h => h.Name == "valid-host");
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("broken-host") && e.Message.Contains("Endpoint"));
    }

    [Fact]
    public void Host_with_a_malformed_enabled_flag_is_reported()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:valid-host:Enabled", "true"),
            ("Servyx:Hosts:valid-host:Endpoint", "ssh:user@10.0.0.9:22"),
            ("Servyx:Hosts:valid-host:Container", "palworld-server"),
            ("Servyx:Hosts:broken-host:Enabled", "Tru"),
            ("Servyx:Hosts:broken-host:Endpoint", "ssh:user@10.0.0.10:22"),
            ("Servyx:Hosts:broken-host:Container", "palworld-server"));
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Hosts.Should().ContainSingle(h => h.Name == "valid-host");
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("broken-host") && e.Message.Contains("Enabled"));
    }

    [Fact]
    public void Host_missing_a_container_name_is_reported()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:valid-host:Enabled", "true"),
            ("Servyx:Hosts:valid-host:Endpoint", "ssh:user@10.0.0.9:22"),
            ("Servyx:Hosts:valid-host:Container", "palworld-server"),
            ("Servyx:Hosts:broken-host:Enabled", "true"),
            ("Servyx:Hosts:broken-host:Transport", "ssh+docker"),
            ("Servyx:Hosts:broken-host:Endpoint", "ssh:user@10.0.0.10:22"));
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Hosts.Should().ContainSingle(h => h.Name == "valid-host");
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("broken-host") && e.Message.Contains("Container"));
    }

    [Fact]
    public void A_section_with_no_usable_hosts_fails_loudly()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:broken-host:Enabled", "true"),
            ("Servyx:Hosts:broken-host:Transport", "ssh+docker"),
            ("Servyx:Hosts:broken-host:Container", "palworld-server"));
        var logger = new RecordingLogger();

        var act = () => SshDockerWiringOptions.FromConfiguration(configuration, logger);

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("broken-host");
        thrown.Message.Should().Contain("Endpoint");
    }

    [Fact]
    public void The_shipped_appsettings_default_does_not_throw()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(path).Should().BeTrue("this test asserts against the real, deployed appsettings.json");

        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var logger = new RecordingLogger();

        var act = () => SshDockerWiringOptions.FromConfiguration(configuration, logger);

        var options = act.Should().NotThrow(
            "the shipped Servyx:Hosts:example-remote entry is Enabled: false — a deliberate, legitimate " +
            "placeholder, not a misconfiguration").Which;
        options.Any.Should().BeFalse();
        logger.Entries.Should().BeEmpty("a deliberately disabled placeholder host is not a rejection");
    }

    [Fact]
    public void A_valid_host_alongside_an_invalid_one_warns_but_still_wires()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:valid-host:Enabled", "true"),
            ("Servyx:Hosts:valid-host:Endpoint", "ssh:user@10.0.0.9:22"),
            ("Servyx:Hosts:valid-host:Container", "palworld-server"),
            ("Servyx:Hosts:broken-host:Enabled", "not-a-bool"));
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Any.Should().BeTrue();
        options.Hosts.Should().ContainSingle(h => h.Name == "valid-host");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("broken-host"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddServyxDocker();
        services.AddServyxSshDocker(options, logger);

        using var provider = services.BuildServiceProvider();
        var target = provider.GetRequiredService<TargetDescriptor>();
        target.Endpoint.Should().Be("ssh:user@10.0.0.9:22", "the one valid host must still be wired despite " +
            "its malformed sibling");
    }

    [Fact]
    public void Configuring_more_than_one_host_warns_that_only_the_first_is_used()
    {
        var configuration = ConfigurationFrom(
            ("Servyx:Hosts:alpha-host:Enabled", "true"),
            ("Servyx:Hosts:alpha-host:Endpoint", "ssh:user@10.0.0.9:22"),
            ("Servyx:Hosts:alpha-host:Container", "palworld-server"),
            ("Servyx:Hosts:beta-host:Enabled", "true"),
            ("Servyx:Hosts:beta-host:Endpoint", "ssh:user@10.0.0.10:22"),
            ("Servyx:Hosts:beta-host:Container", "palworld-server"));
        var logger = new RecordingLogger();

        var options = SshDockerWiringOptions.FromConfiguration(configuration, logger);

        options.Hosts.Should().HaveCount(2);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("2")
            && e.Message.Contains(options.Hosts[0].Name),
            "the warning must name which host actually wins, not just that there is more than one");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddServyxDocker();
        services.AddServyxSshDocker(options, logger);

        using var provider = services.BuildServiceProvider();
        var target = provider.GetRequiredService<TargetDescriptor>();
        target.Endpoint.Should().Be(options.Hosts[0].Target.Endpoint,
            "AddServyxSshDocker wires only options.Hosts[0], whichever host that turned out to be");
    }
}
