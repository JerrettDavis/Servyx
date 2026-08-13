using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Domain.Common;
using Servyx.Domain.Connectors;
using Servyx.Domain.Discovery;
using Servyx.Domain.Entities;
using Servyx.Domain.Hosts;
using Servyx.Domain.Observability;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Docker;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Docker;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Shared across every <c>AddServyxSshDocker</c> wiring test below (<see cref="SshDockerWiringTests"/> and
/// <see cref="SshDockerWiringReportingTests"/>), which each compose their own minimal
/// <see cref="IServiceCollection"/> rather than the full composition root.
/// </summary>
internal static class SshDockerWiringTestSupport
{
    /// <summary>
    /// A substituted <see cref="IHostRepository"/> that answers with zero rows unless a test explicitly
    /// configures otherwise — <see cref="HostConnectionRegistry"/> (the production <see cref="IHostConnectionSource"/>
    /// <c>AddServyxSshDocker</c> now registers alongside discovery) needs one resolvable in the container for
    /// <see cref="IServerDiscovery"/> to resolve at all, even in tests that never call <c>DiscoverAsync</c>.
    /// </summary>
    internal static IHostRepository EmptyHostRepository()
    {
        var repository = Substitute.For<IHostRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([]));
        return repository;
    }
}

/// <summary>
/// Composes <c>AddServyxSshDocker</c> the way <c>Program.cs</c> does — right after <c>AddServyxDocker()</c>
/// — and asserts the write-guard and replacement-vs-no-op properties the whole registration exists for.
/// </summary>
/// <remarks>
/// None of these tests open a real socket. Resolving <see cref="IServerDiscovery"/>, <see cref="ITransport"/>
/// etc. only constructs objects; the SSH connection <c>SshDockerServiceCollectionExtensions</c>'s
/// <see cref="LazyConnectingExecutionTarget"/> would eventually open happens only on a real
/// <see cref="IExecutionTarget"/> call, which nothing here makes.
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
        services.AddSingleton(SshDockerWiringTestSupport.EmptyHostRepository());
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
    public async Task Server_discovery_resolves_to_the_composite_ssh_docker_implementation_and_not_the_docker_one()
    {
        // Async disposal: resolving IServerDiscovery constructs the shared LazyConnectingExecutionTarget
        // singleton (via HostConnectionRegistry), which — like every IExecutionTarget — implements only
        // IAsyncDisposable.
        await using var provider = ComposedWithHost();

        provider.GetRequiredService<IServerDiscovery>().Should().BeOfType<CompositeServerDiscovery>();
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

    /// <summary>
    /// The ITransport half of "nothing configured": AddServyxSshDocker's single-target surfaces
    /// (ITransport/TargetDescriptor/IExecutionTarget) genuinely stay untouched with zero hosts declared.
    /// IServerDiscovery, ILogStream, and IMetricsSource are NOT among them any more — see
    /// <see cref="With_no_hosts_configured_and_no_host_ever_registered_discovery_still_resolves_to_local_docker_through_the_wrapper"/>,
    /// <see cref="Registering_a_database_only_host_switches_the_wrapper_to_the_composite_fan_out_without_a_restart"/>,
    /// <see cref="Log_stream_resolves_to_the_host_aware_wrapper_even_with_zero_hosts_declared"/>, and
    /// <see cref="Metrics_source_resolves_to_the_host_aware_wrapper_even_with_zero_hosts_declared"/> below for
    /// how those three now behave in this case.
    /// </summary>
    [Fact]
    public void With_no_hosts_configured_the_single_target_docker_registrations_are_left_untouched()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var transportRegistrationsBefore = services.Count(d => d.ServiceType == typeof(ITransport));

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();

        services.AddServyxSshDocker(options, NullLogger.Instance);

        services.Count(d => d.ServiceType == typeof(ITransport)).Should().Be(transportRegistrationsBefore);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ITransport>().Should().NotBeOfType<SshDockerTransport>(
            "with no host declared, the local Docker ITransport AddServyxDocker registered must still be the one resolved");
    }

    /// <summary>
    /// The fix for the bug this whole file's sibling suite (<see cref="HostAwareServerDiscoveryCompositionTests"/>
    /// in <c>Servyx.Infrastructure.Ssh.Tests</c>) proves end-to-end: <c>AddServyxSshDocker</c> used to leave
    /// <see cref="IServerDiscovery"/> bound to plain local Docker discovery for the process's whole lifetime
    /// whenever <see cref="SshDockerWiringOptions.Any"/> was <see langword="false"/> — even after an operator
    /// registered a host purely through the UI, which only ever invalidated
    /// <see cref="HostConnectionRegistry"/>'s cache, a type nothing resolved <see cref="IServerDiscovery"/> to
    /// any more. <see cref="IServerDiscovery"/> is now unconditionally re-bound to
    /// <see cref="HostAwareServerDiscovery"/> in this case, which — with zero hosts, configured or
    /// database-registered — defers to exactly the local Docker discovery instance <c>AddServyxDocker</c>
    /// registered, so a plain local-docker-only install (the common case) is provably unaffected.
    /// </summary>
    [Fact]
    public void With_no_hosts_configured_and_no_host_ever_registered_discovery_still_resolves_to_local_docker_through_the_wrapper()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();
        services.AddServyxSshDocker(options, NullLogger.Instance);

        using var provider = services.BuildServiceProvider();

        var discovery = provider.GetRequiredService<IServerDiscovery>();
        discovery.Should().BeOfType<HostAwareServerDiscovery>(
            "IServerDiscovery is now always re-bound, even with zero hosts declared — see this type's own remarks");
    }

    /// <summary>
    /// The per-server execution-target routing seam (<see cref="IServerExecutionTargetResolver"/>) is
    /// registered unconditionally, same as <see cref="HostConnectionRegistry"/>/<see cref="IHostConnectionSource"/>/
    /// <see cref="CompositeServerDiscovery"/> just above it — a zero-config install still needs it resolvable
    /// so a server discovered later (through a database-registered host, without a process restart) has
    /// something to route through. Resolving a local (null host key) server here proves it reaches the exact
    /// local Docker <see cref="ITransport"/> <c>AddServyxDocker</c> registered, not the ssh+docker one — there
    /// is no statically-declared host in this composition to have swapped it.
    /// </summary>
    [Fact]
    public async Task With_no_hosts_configured_the_execution_target_resolver_is_registered_and_resolves_the_local_transport()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();
        services.AddServyxSshDocker(options, NullLogger.Instance);

        await using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<IServerExecutionTargetResolver>();

        await using var target = await resolver.ResolveAsync("container-123", hostKey: null);
        target.Should().NotBeNull();
    }

    /// <summary>
    /// With a statically-declared host, <c>AddServyxSshDocker</c> swaps the process-wide <see cref="ITransport"/>
    /// service type to the ssh+docker one — but <see cref="IServerExecutionTargetResolver"/>'s local branch
    /// must still reach the ORIGINAL local Docker transport <c>AddServyxDocker</c> registered (captured before
    /// that swap), never the now-current ssh+docker one, since a null host key always means "the local Docker
    /// daemon" regardless of what else this process has statically configured.
    /// </summary>
    [Fact]
    public async Task With_a_declared_host_the_execution_target_resolvers_local_branch_still_reaches_local_docker_not_ssh_docker()
    {
        await using var provider = ComposedWithHost();

        provider.GetRequiredService<ITransport>().Should().BeOfType<WriteGuardedTransport>()
            .Which.Inner.Should().BeOfType<SshDockerTransport>(
                "the process-wide ITransport is displaced to ssh+docker once a host is statically declared");

        var resolver = provider.GetRequiredService<IServerExecutionTargetResolver>();

        await using var target = await resolver.ResolveAsync("container-123", hostKey: null);
        target.Should().NotBeNull();
    }

    /// <summary>
    /// Increment 4b / the fix under test: <c>AddServyxSshDocker</c> used to no-op entirely whenever
    /// <see cref="SshDockerWiringOptions.Any"/> was <see langword="false"/>, so a fresh, zero-config install
    /// never registered <see cref="HostConnectionRegistry"/>/<see cref="IHostConnectionSource"/>/
    /// <see cref="CompositeServerDiscovery"/> at all — there was nothing in the container for a
    /// database-registered host (added later, through the UI, with no process restart) to be discovered
    /// through. This proves the fix at the DI-composition level: with zero config hosts, those three are still
    /// resolvable and the registry still reports a database-registered host, and — the part that used to stay
    /// broken — <see cref="IServerDiscovery"/> resolves to the host-aware wrapper that will route to that
    /// composite fan-out the moment it is asked to discover (see <c>HostAwareServerDiscoveryCompositionTests</c>
    /// in <c>Servyx.Infrastructure.Ssh.Tests</c> for the behavioral proof that it actually does, since a real
    /// SSH connection is not something this DI-level suite opens).
    /// </summary>
    [Fact]
    public async Task Registering_a_database_only_host_switches_the_wrapper_to_the_composite_fan_out_without_a_restart()
    {
        var dbHost = new Host
        {
            Id = HostId.New(),
            Name = "db-only-host",
            ConnectorId = "ssh:db-only-host",
            Endpoint = "db-host.example.com:22",
            TrustPolicy = "trustOnFirstUse",
            Enabled = true,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var repository = Substitute.For<IHostRepository>();
        repository.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Host>>([dbHost]));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddSingleton(repository);
        services.AddServyxDocker();

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();
        services.AddServyxSshDocker(options, NullLogger.Instance);

        await using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IHostConnectionSource>();
        var connections = await source.GetConnectionsAsync();

        connections.Should().ContainSingle().Which.HostKey.Should().Be("db-only-host");

        // Resolvable directly (the seam a later host-registration flow attaches to), and now — unlike before
        // this fix — the general-purpose IServerDiscovery slot itself is also wired to react to exactly this.
        provider.GetRequiredService<CompositeServerDiscovery>().Should().NotBeNull();
        provider.GetRequiredService<IServerDiscovery>().Should().BeOfType<HostAwareServerDiscovery>(
            "the database-registered host must be reachable through the SAME IServerDiscovery every other " +
            "caller (ServerAdoptionService included) resolves, not merely through a side-channel type nothing else uses");
    }

    /// <summary>
    /// Increment 4 / the fix under test: <see cref="ILogStream"/> is now re-bound unconditionally, exactly
    /// like <see cref="IServerDiscovery"/> — a zero-<c>Servyx:Hosts</c>, zero-database-row install (the common
    /// case) must still resolve <see cref="HostAwareLogStream"/>, not the bare local
    /// <c>Servyx.Infrastructure.Docker.DockerLogStream</c> <c>AddServyxDocker</c> registered, so a host
    /// registered later through the UI has something host-aware already wired to route its console reads
    /// through — without a process restart, matching <see cref="IServerDiscovery"/>'s own contract.
    /// </summary>
    [Fact]
    public void Log_stream_resolves_to_the_host_aware_wrapper_even_with_zero_hosts_declared()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();
        services.AddServyxSshDocker(options, NullLogger.Instance);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ILogStream>().Should().BeOfType<HostAwareLogStream>(
            "ILogStream is now always re-bound, even with zero hosts declared — see HostAwareLogStream's own remarks");
    }

    /// <summary>
    /// With a statically-declared host, <see cref="ILogStream"/> used to be swapped straight to a bare
    /// <see cref="SshDockerLogStream"/> scoped to that one host only — which is exactly the gap this
    /// increment closes: a server discovered on a SECOND host (static or database-registered) had no route to
    /// its own console at all. <see cref="ILogStream"/> now resolves to <see cref="HostAwareLogStream"/> here
    /// too, which routes each server to whichever host it actually matches (see that type's own remarks and
    /// <c>HostAwareLogStreamTests</c> for the routing behavior itself).
    /// </summary>
    [Fact]
    public void Log_stream_resolves_to_the_host_aware_wrapper_with_a_statically_declared_host()
    {
        using var provider = ComposedWithHost();

        provider.GetRequiredService<ILogStream>().Should().BeOfType<HostAwareLogStream>();
    }

    /// <summary>
    /// Mirrors <see cref="Log_stream_resolves_to_the_host_aware_wrapper_even_with_zero_hosts_declared"/>:
    /// <see cref="IMetricsSource"/> is now re-bound unconditionally too, so a zero-<c>Servyx:Hosts</c>,
    /// zero-database-row install still resolves <see cref="HostAwareMetricsSource"/>, not the bare local
    /// <c>Servyx.Infrastructure.Docker.DockerMetricsSource</c> <c>AddServyxDocker</c> registered.
    /// </summary>
    [Fact]
    public void Metrics_source_resolves_to_the_host_aware_wrapper_even_with_zero_hosts_declared()
    {
        var services = BaseServices();
        services.AddServyxDocker();

        var options = SshDockerWiringOptions.FromConfiguration(new ConfigurationBuilder().Build(), NullLogger.Instance);
        options.Any.Should().BeFalse();
        services.AddServyxSshDocker(options, NullLogger.Instance);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMetricsSource>().Should().BeOfType<HostAwareMetricsSource>(
            "IMetricsSource is now always re-bound, even with zero hosts declared — see HostAwareMetricsSource's own remarks");
    }

    /// <summary>
    /// With a statically-declared host, <see cref="IMetricsSource"/> used to be swapped straight to a bare
    /// <see cref="SshDockerMetricsSource"/> scoped to that one host only — the same single-host gap
    /// <see cref="Log_stream_resolves_to_the_host_aware_wrapper_with_a_statically_declared_host"/> closed for
    /// console reads. <see cref="IMetricsSource"/> now resolves to <see cref="HostAwareMetricsSource"/> here
    /// too, which routes each server to whichever host it actually matches (see that type's own remarks and
    /// <c>HostAwareMetricsSourceTests</c> for the routing behavior itself).
    /// </summary>
    [Fact]
    public void Metrics_source_resolves_to_the_host_aware_wrapper_with_a_statically_declared_host()
    {
        using var provider = ComposedWithHost();

        provider.GetRequiredService<IMetricsSource>().Should().BeOfType<HostAwareMetricsSource>();
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

    /// <summary>
    /// Configuring more than one host used to log a warning that only <c>Hosts[0]</c> would ever be wired to
    /// anything. That is no longer true for discovery, log streaming, or metrics —
    /// <see cref="CompositeServerDiscovery"/>, <see cref="HostAwareLogStream"/>, and
    /// <see cref="HostAwareMetricsSource"/> all fan out across every configured host — so the warning is gone;
    /// <see cref="TargetDescriptor"/> (and <see cref="ITransport"/>/<see cref="IExecutionTarget"/>, the
    /// remaining Hosts[0]-scoped surfaces) is still wired from the first host only, which is a real,
    /// still-current limitation this test keeps pinned.
    /// </summary>
    [Fact]
    public async Task Configuring_more_than_one_host_no_longer_warns_but_still_scopes_TargetDescriptor_to_the_first()
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
        logger.Entries.Should().BeEmpty(
            "configuring more than one host is fully supported now that discovery fans out across all of "
            + "them — there is nothing left to warn about");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ISecretStore>());
        services.AddSingleton(Substitute.For<IHostKeyVerifier>());
        services.AddSingleton(SshDockerWiringTestSupport.EmptyHostRepository());
        services.AddServyxDocker();
        services.AddServyxSshDocker(options, logger);

        await using var provider = services.BuildServiceProvider();

        var target = provider.GetRequiredService<TargetDescriptor>();
        target.Endpoint.Should().Be(options.Hosts[0].Target.Endpoint,
            "TargetDescriptor/ITransport/IExecutionTarget are still wired only from options.Hosts[0] — multi-host " +
            "support is scoped to discovery, log streaming, and metrics only");

        provider.GetRequiredService<IServerDiscovery>().Should().BeOfType<CompositeServerDiscovery>(
            "discovery, unlike the single-target surfaces above, is wired to fan out across every configured host");
        provider.GetRequiredService<ILogStream>().Should().BeOfType<HostAwareLogStream>(
            "log streaming, like discovery, is wired to route per-server across every configured host rather " +
            "than staying fixed to options.Hosts[0]");
        provider.GetRequiredService<IMetricsSource>().Should().BeOfType<HostAwareMetricsSource>(
            "metrics, like discovery and log streaming, is wired to route per-server across every configured " +
            "host rather than staying fixed to options.Hosts[0]");
    }
}
