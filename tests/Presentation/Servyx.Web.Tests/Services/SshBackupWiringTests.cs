using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Infrastructure.Ssh.Backups;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes SSH-hosted backups the way <c>Program.cs</c>'s gated block does, on both sides of the gate, and
/// asserts the one property the whole exercise is for: adding a second backup provider must not move where
/// a Docker-hosted server's backups go.
/// </summary>
public class SshBackupWiringTests
{
    private const string SshServer = "valheim-host";
    private const string DockerServer = "palworld-server";

    private static readonly SecretUrn RconPasswordUrn =
        SecretUrn.Create("server", SshServer, "rcon", "password");

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    /// <summary>A fully-specified SSH server, plus whatever the caller adds.</summary>
    private static IConfiguration SshConfigured(params (string Key, string Value)[] extra) => Config(
    [
        ("Servyx:Provisioning:Enabled", "true"),
        ($"Servyx:Servers:{SshServer}:Ssh:Enabled", "true"),
        ($"Servyx:Servers:{SshServer}:Ssh:Host", "steam@10.0.0.4:22"),
        ($"Servyx:Servers:{SshServer}:Ssh:Root", "/srv/valheim/"),
        ($"Servyx:Servers:{SshServer}:Ssh:Include:0", "saves"),
        ($"Servyx:Servers:{SshServer}:Ssh:Include:1", "config"),
        ($"Servyx:Servers:{SshServer}:Ssh:Exclude:0", "**/*.tmp"),
        .. extra,
    ]);

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    private static ServyxRconChannels ChannelFor(string serverKey) => new(
        new RconWiringOptions([new RconChannel(serverKey, new RconEndpoint("127.0.0.1", 25575), RconPasswordUrn)]),
        Palworld(),
        new SourceRconClient(),
        new RecordingSecretStore(),
        new WritableServers([serverKey]));

    private static ITransport Transport()
    {
        var transport = Substitute.For<ITransport>();
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return transport;
    }

    // ── The gate ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_closed_provisioning_gate_yields_no_ssh_hosted_server_however_complete_the_configuration()
    {
        // Everything an SSH server needs is present except the one flag that lets any of it be read.
        var configuration = Config(
            ($"Servyx:Servers:{SshServer}:Ssh:Enabled", "true"),
            ($"Servyx:Servers:{SshServer}:Ssh:Host", "steam@10.0.0.4:22"),
            ($"Servyx:Servers:{SshServer}:Ssh:Root", "/srv/valheim"),
            ($"Servyx:Servers:{SshServer}:WriteMode", "Enabled"));

        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeFalse();

        var options = SshBackupWiringOptions.FromConfiguration(configuration, gate);

        options.Should().BeSameAs(SshBackupWiringOptions.None);
        options.Any.Should().BeFalse();
        options.WriteGrants.Should().BeEmpty();
    }

    [Fact]
    public void The_read_only_composition_holds_no_ssh_backup_service_of_any_kind()
    {
        // Exactly what Program.cs composes when the flag is absent: nothing from inside the `if`.
        var configuration = Config(($"Servyx:Servers:{SshServer}:Ssh:Enabled", "true"));
        var gate = ProvisioningGate.FromConfiguration(configuration);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(gate);
        services.AddSingleton(WritableServers.FromConfiguration(configuration, gate));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetService<ISshBackupContextSource>().Should().BeNull();
        provider.GetService<ServyxSshBackupContextSource>().Should().BeNull();
        provider.GetService<SshBackupWiringOptions>().Should().BeNull();
        provider.GetService<IBackupProvider>().Should().BeNull();
        provider.GetService<IBackupDashboard>().Should().BeNull();
        provider.GetServices<WriteModeGrant>().Should().BeEmpty();
    }

    [Fact]
    public void An_open_gate_with_no_ssh_server_configured_registers_nothing_for_ssh_and_leaves_docker_alone()
    {
        // The provisioning flag is on and a Docker server is fully configured — the shape every existing
        // host already has. Nothing about SSH may appear in it.
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ($"Servyx:Servers:{DockerServer}:WriteMode", "Enabled"));

        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeTrue();

        var sshBackups = SshBackupWiringOptions.FromConfiguration(configuration, gate);
        sshBackups.Should().BeSameAs(SshBackupWiringOptions.None);
        sshBackups.Any.Should().BeFalse();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sshBackups);
        services.AddSingleton<IBackupProvider>(new ScriptedBackupProvider());

        // Program.cs takes the `else` branch when sshBackups.Any is false: the unchanged single-provider
        // registration, with no router in the path at all.
        services.AddServyxBackupDashboard();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetService<ISshBackupContextSource>().Should().BeNull();
        provider.GetServices<IBackupProvider>().Should().ContainSingle().Which.Should().BeOfType<ScriptedBackupProvider>();
        provider.GetRequiredService<IBackupDashboard>().ProviderConfigured.Should().BeTrue();
    }

    // ── The configuration surface ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_opted_in_server_carries_its_endpoint_root_and_capture_set_and_a_credential_locator_never_a_credential()
    {
        var configuration = SshConfigured();

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        var server = options.Servers.Should().ContainSingle().Subject;
        server.ServerKey.Should().Be(SshServer);
        server.Endpoint.Should().Be("steam@10.0.0.4:22");
        server.Root.Should().Be("/srv/valheim");
        server.Include.Should().Equal("saves", "config");
        server.Exclude.Should().Equal("**/*.tmp");
        server.StoreDirectory.Should().Be(SshBackupWiringOptions.DefaultStoreDirectory);
        server.DeploymentKind.Should().Be(SshBackupWiringOptions.DefaultDeploymentKind);
        server.CredentialUrn?.Value.Should().Be($"secret://server/{SshServer}/ssh/password");
        server.Writable.Should().BeFalse();
    }

    [Fact]
    public void An_explicit_credential_locator_is_honoured_and_an_unparseable_one_is_never_silently_replaced()
    {
        var honoured = SshBackupWiringOptions.FromConfiguration(
            SshConfigured(($"Servyx:Servers:{SshServer}:Ssh:CredentialUrn", "secret://host/backup-box/ssh/privatekey")),
            new ProvisioningGate(enabled: true));

        honoured.Servers.Should().ContainSingle()
            .Which.CredentialUrn?.Value.Should().Be("secret://host/backup-box/ssh/privatekey");

        // An operator who named a locator meant a specific one. Falling back to the convention here would
        // authenticate as somebody else; a null locator makes the connection fail instead.
        var unparseable = SshBackupWiringOptions.FromConfiguration(
            SshConfigured(($"Servyx:Servers:{SshServer}:Ssh:CredentialUrn", "not-a-urn")),
            new ProvisioningGate(enabled: true));

        unparseable.Servers.Should().ContainSingle().Which.CredentialUrn.Should().BeNull();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("TRUE-ish")]
    public void An_unparseable_enabled_flag_is_read_as_not_an_ssh_hosted_server(string raw)
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ($"Servyx:Servers:{SshServer}:Ssh:Enabled", raw),
            ($"Servyx:Servers:{SshServer}:Ssh:Host", "steam@10.0.0.4"),
            ($"Servyx:Servers:{SshServer}:Ssh:Root", "/srv/valheim"));

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Root")]
    public void A_server_missing_a_value_nothing_can_be_inferred_from_is_skipped_rather_than_defaulted(string omitted)
    {
        var entries = new List<(string, string)>
        {
            ("Servyx:Provisioning:Enabled", "true"),
            ($"Servyx:Servers:{SshServer}:Ssh:Enabled", "true"),
        };

        if (omitted != "Host")
        {
            entries.Add(($"Servyx:Servers:{SshServer}:Ssh:Host", "steam@10.0.0.4"));
        }

        if (omitted != "Root")
        {
            entries.Add(($"Servyx:Servers:{SshServer}:Ssh:Root", "/srv/valheim"));
        }

        var configuration = Config([.. entries]);

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Fact]
    public void A_server_key_that_cannot_address_a_secret_gets_no_ssh_wiring()
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:val heim!:Ssh:Enabled", "true"),
            ("Servyx:Servers:val heim!:Ssh:Host", "steam@10.0.0.4"),
            ("Servyx:Servers:val heim!:Ssh:Root", "/srv/valheim"));

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Fact]
    public void A_write_grant_is_emitted_only_for_a_server_the_operator_enabled_and_only_for_its_own_endpoint()
    {
        var configuration = SshConfigured(($"Servyx:Servers:{SshServer}:WriteMode", "Enabled"));

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        var grant = options.WriteGrants.Should().ContainSingle().Subject;
        grant.Mode.Should().Be(WriteMode.Enabled);
        grant.TransportId.Should().Be(SshBackupWiringOptions.TransportId);
        grant.Endpoint.Should().Be("steam@10.0.0.4:22");

        // Scoped to this host and no other, and to SSH rather than to whatever else the process can reach.
        grant.Matches(Descriptor("ssh", "steam@10.0.0.4:22")).Should().BeTrue();
        grant.Matches(Descriptor("ssh", "steam@10.0.0.9:22")).Should().BeFalse();
        grant.Matches(Descriptor("docker", "steam@10.0.0.4:22")).Should().BeFalse();

        static TargetDescriptor Descriptor(string transportId, string endpoint) =>
            new(transportId, endpoint, null, null, new Dictionary<string, string>(StringComparer.Ordinal));
    }

    [Fact]
    public void A_configured_server_with_no_write_mode_gets_no_grant_at_all()
    {
        var configuration = SshConfigured();

        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeTrue();
        options.WriteGrants.Should().BeEmpty();
    }

    // ── The context source ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_configured_server_resolves_a_context_rooted_and_scoped_where_the_operator_said()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        await using var source = new ServyxSshBackupContextSource(options, Transport());

        var context = await source.GetAsync(SshServer);

        context.ServerId.Should().Be(SshServer);
        context.DeploymentKind.Should().Be(SshBackupWiringOptions.DefaultDeploymentKind);
        context.Root.Should().Be("/srv/valheim");
        context.Include.Should().Equal("saves", "config");
        context.Exclude.Should().Equal("**/*.tmp");
        context.StoreDirectory.Should().Be(SshBackupWiringOptions.DefaultStoreDirectory);

        // No adopter ships for a generic SSH host, so nothing may be asserted to be a foreign archive.
        context.Foreign.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_the_operator_never_configured_is_refused_rather_than_guessed_at()
    {
        await using var source = new ServyxSshBackupContextSource(SshBackupWiringOptions.None, Transport());

        var act = async () => await source.GetAsync(DockerServer);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured as an SSH-hosted server*");
    }

    [Fact]
    public async Task The_session_is_opened_against_the_configured_endpoint_and_credential_locator()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        var transport = Transport();
        await using var source = new ServyxSshBackupContextSource(options, transport);

        await source.GetAsync(SshServer);

        // The endpoint the grant is scoped to and the endpoint the session connects to are the same string;
        // if they ever diverge, every write is silently refused.
        await transport.Received(1).ConnectAsync(
            Arg.Is<TargetDescriptor>(d =>
                d != null
                && d.TransportId == SshBackupWiringOptions.TransportId
                && d.Endpoint == "steam@10.0.0.4:22"
                && d.CredentialUrn == $"secret://server/{SshServer}/ssh/password"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_session_is_opened_per_server_however_many_contexts_are_asked_for()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        var transport = Transport();
        await using var source = new ServyxSshBackupContextSource(options, transport);

        await source.GetAsync(SshServer);
        await source.GetAsync(SshServer);
        await source.GetAsync(SshServer);

        await transport.Received(1).ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>());
    }

    // ── Quiesce ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_ssh_server_with_an_rcon_channel_gains_the_definitions_quiesce_step()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        await using var source = new ServyxSshBackupContextSource(options, Transport(), ChannelFor(SshServer));

        var context = await source.GetAsync(SshServer);

        context.Control.Should().NotBeNull();
        context.Quiesce.Should().NotBeNull();
        context.Quiesce!.CommandId.Should().Be(RconWiringOptions.QuiesceCommandId);
        context.Quiesce.Timeout.Should().Be(RconWiringOptions.QuiesceTimeout);
    }

    [Fact]
    public async Task An_ssh_server_without_an_rcon_channel_gets_no_quiesce_step_and_no_control_session()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        // A channel exists in this process — for a different server. Presence is per server, never per host.
        await using var source = new ServyxSshBackupContextSource(options, Transport(), ChannelFor(DockerServer));

        var context = await source.GetAsync(SshServer);

        context.Control.Should().BeNull();
        context.Quiesce.Should().BeNull();
    }

    [Fact]
    public async Task No_rcon_wiring_at_all_leaves_the_context_exactly_as_it_was_before_control_channels_existed()
    {
        var configuration = SshConfigured();
        var options = SshBackupWiringOptions.FromConfiguration(
            configuration,
            ProvisioningGate.FromConfiguration(configuration));

        await using var source = new ServyxSshBackupContextSource(options, Transport(), ServyxRconChannels.None);

        var context = await source.GetAsync(SshServer);

        context.Control.Should().BeNull();
        context.Quiesce.Should().BeNull();
    }

    // ── Two providers, one dashboard ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_docker_hosted_server_still_reaches_the_docker_provider_with_both_registered()
    {
        var docker = new ScriptedBackupProvider();
        var ssh = new ScriptedBackupProvider();
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        await router.CreateAsync(DockerServer);
        await router.ListAsync(DockerServer);
        await router.PruneAsync(DockerServer, BackupWiringOptions.FallbackRetention, dryRun: false);

        docker.CreateCalls.Should().Be(1);
        docker.LivePruneCalls.Should().Be(1);
        ssh.CreateCalls.Should().Be(0);
        ssh.LivePruneCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_ssh_hosted_server_reaches_the_ssh_provider()
    {
        var docker = new ScriptedBackupProvider();
        var ssh = new ScriptedBackupProvider();
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        await router.CreateAsync(SshServer);
        await router.PruneAsync(SshServer, BackupWiringOptions.FallbackRetention, dryRun: false);

        ssh.CreateCalls.Should().Be(1);
        ssh.LivePruneCalls.Should().Be(1);
        docker.CreateCalls.Should().Be(0);
        docker.LivePruneCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_opaque_backup_id_routes_by_the_server_it_encodes_and_falls_back_when_it_encodes_none()
    {
        var docker = new RecordingBackupProvider();
        var ssh = new RecordingBackupProvider();
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        await router.InspectAsync(BackupArtifactId.Format(SshServer, "/srv/valheim/servyx-backups/a.tar.gz"));
        await router.InspectAsync(BackupArtifactId.Format(DockerServer, "/palworld/servyx-backups/b.tar.gz"));

        // Not this provider's encoding at all — the default already answers an unknown id with "not found".
        await router.InspectAsync("a-bare-id-with-no-server");

        ssh.Inspected.Should().ContainSingle();
        docker.Inspected.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_restore_plan_is_applied_by_the_provider_that_issued_it()
    {
        var docker = new RecordingBackupProvider { PlanId = "restore-docker" };
        var ssh = new RecordingBackupProvider { PlanId = "restore-ssh" };
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        // A restore-plan id names no server, so the routing has to be remembered at preview time.
        var sshPlan = await router.PlanRestoreAsync(BackupArtifactId.Format(SshServer, "/srv/valheim/a.tar.gz"));
        var dockerPlan = await router.PlanRestoreAsync(BackupArtifactId.Format(DockerServer, "/palworld/b.tar.gz"));

        await router.RestoreAsync(sshPlan.Id);
        await router.RestoreAsync(dockerPlan.Id);

        ssh.Restored.Should().Equal("restore-ssh");
        docker.Restored.Should().Equal("restore-docker");
    }

    [Fact]
    public async Task A_plan_id_this_router_never_issued_goes_to_the_default_provider_which_refuses_it_as_it_always_has()
    {
        var docker = new RecordingBackupProvider();
        var ssh = new RecordingBackupProvider();
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        await router.RestoreAsync("restore-fabricated");

        docker.Restored.Should().Equal("restore-fabricated");
        ssh.Restored.Should().BeEmpty();
    }

    [Fact]
    public async Task A_spent_plan_id_is_not_routed_to_the_ssh_provider_a_second_time()
    {
        var docker = new RecordingBackupProvider();
        var ssh = new RecordingBackupProvider { PlanId = "restore-ssh" };
        var router = new ServyxBackupProviderRouter(docker, ssh, [SshServer]);

        var plan = await router.PlanRestoreAsync(BackupArtifactId.Format(SshServer, "/srv/valheim/a.tar.gz"));

        await router.RestoreAsync(plan.Id);
        await router.RestoreAsync(plan.Id);

        ssh.Restored.Should().Equal("restore-ssh");
        docker.Restored.Should().Equal("restore-ssh");
    }

    [Fact]
    public void The_router_selects_the_ssh_provider_by_type_never_by_registration_order()
    {
        var docker = new ScriptedBackupProvider();
        var ssh = new SshBackupProvider(Substitute.For<ISshBackupContextSource>(), []);

        ServyxBackupProviderRouter.FromRegistered([docker, ssh], [SshServer])
            .SshServerIds.Should().Equal(SshServer);

        // The same set in the other order composes the same router.
        ServyxBackupProviderRouter.FromRegistered([ssh, docker], [SshServer])
            .SshServerIds.Should().Equal(SshServer);
    }

    [Fact]
    public void An_ambiguous_provider_set_is_refused_rather_than_resolved_by_position()
    {
        var act = () => ServyxBackupProviderRouter.FromRegistered([new ScriptedBackupProvider()], [SshServer]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly one*");
    }

    [Fact]
    public void The_gated_block_composes_a_router_backed_dashboard_when_an_ssh_server_is_configured()
    {
        var configuration = SshConfigured(($"Servyx:Servers:{SshServer}:WriteMode", "Enabled"));
        var gate = ProvisioningGate.FromConfiguration(configuration);
        var sshBackups = SshBackupWiringOptions.FromConfiguration(configuration, gate);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sshBackups);

        foreach (var grant in sshBackups.WriteGrants)
        {
            services.AddSingleton(grant);
        }

        // Stands in for AddServyxDockerBackups(), which needs a Docker daemon and a context source.
        // Everything below it is the real registration.
        services.AddSingleton<IBackupProvider>(new ScriptedBackupProvider());
        services.AddSingleton(Substitute.For<ISshBackupContextSource>());
        services.AddServyxSshBackups();
        services.AddSingleton<IBackupDashboard>(sp => new BackupDashboardService(
            ServyxBackupProviderRouter.FromRegistered(sp.GetServices<IBackupProvider>(), sshBackups.ServerKeys)));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        provider.GetServices<IBackupProvider>().Should().HaveCount(2);
        provider.GetRequiredService<IBackupDashboard>().ProviderConfigured.Should().BeTrue();
        provider.GetServices<WriteModeGrant>().Should().ContainSingle()
            .Which.TransportId.Should().Be(SshBackupWiringOptions.TransportId);
    }

    /// <summary>
    /// An <see cref="IBackupProvider"/> that records which of the id-taking members it was asked for, so a
    /// routing decision is observable without a filesystem underneath either provider.
    /// </summary>
    private sealed class RecordingBackupProvider : IBackupProvider
    {
        public string PlanId { get; init; } = "restore-1";

        public List<string> Inspected { get; } = [];

        public List<string> Restored { get; } = [];

        public Task<BackupArtifact> CreateAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult(new BackupArtifact("id", BackupOwnership.Servyx, DateTimeOffset.UnixEpoch, 1, "/loc"));

        public Task<IReadOnlyList<BackupArtifact>> ListAsync(string serverId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BackupArtifact>>([]);

        public Task<IReadOnlyList<string>> InspectAsync(string backupId, CancellationToken ct = default)
        {
            Inspected.Add(backupId);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<RestorePlan> PlanRestoreAsync(string backupId, CancellationToken ct = default) =>
            Task.FromResult(new RestorePlan(PlanId, backupId, []));

        public Task RestoreAsync(string restorePlanId, CancellationToken ct = default)
        {
            Restored.Add(restorePlanId);
            return Task.CompletedTask;
        }

        public Task<PruneResult> PruneAsync(string serverId, RetentionPolicy policy, bool dryRun, CancellationToken ct = default) =>
            Task.FromResult(new PruneResult([], 0));
    }
}
