using Microsoft.Extensions.Configuration;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Composes the RCON control channel the way <c>Program.cs</c>'s gated block does, and asserts the one
/// thing it exists for: a backup of a running server flushes first, or produces nothing at all.
/// </summary>
public class RconWiringTests
{
    private const string Container = "palworld-server";

    private static readonly SecretUrn PasswordUrn =
        SecretUrn.Create("server", Container, "rcon", "password");

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
    ]);

    /// <summary>
    /// A single-strategy chain factory that never touches the network, for tests asserting write-guard and
    /// quiesce-wiring behaviour rather than reachability itself — <see cref="RconReachabilityChainWiringTests"/>
    /// covers the real chain composition and strategy fallback.
    /// </summary>
    private static Func<RconChannel, RconReachabilityChain> DirectChain(
        IRconClient client, RconCommandCatalog catalog, ISecretStore secrets) =>
        channel => new RconReachabilityChain(
        [
            new Fakes.AlwaysAvailableRconReachability(endpoint => new RconSession(client, endpoint, catalog, secrets, channel.PasswordUrn)),
        ]);

    [Fact]
    public void A_closed_provisioning_gate_yields_no_control_channel_at_all()
    {
        var configuration = Config(
            ("Servyx:Servers:palworld-server:Rcon:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Port", "25575"));

        var gate = ProvisioningGate.FromConfiguration(configuration);
        gate.Enabled.Should().BeFalse();

        var options = RconWiringOptions.FromConfiguration(configuration, gate);

        options.Should().BeSameAs(RconWiringOptions.Disabled);
        options.Any.Should().BeFalse();
    }

    [Fact]
    public void A_server_that_does_not_opt_in_gets_no_control_channel()
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:palworld-server:WriteMode", "Enabled"));

        var options = RconWiringOptions.FromConfiguration(configuration, ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("TRUE-ish")]
    public void An_unparseable_enabled_flag_is_read_as_no_channel(string raw)
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Enabled", raw));

        var options = RconWiringOptions.FromConfiguration(configuration, ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Fact]
    public void An_opted_in_server_gets_an_endpoint_and_a_credential_locator_never_a_credential()
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Host", "10.0.0.5"),
            ("Servyx:Servers:palworld-server:Rcon:Port", "27015"));

        var options = RconWiringOptions.FromConfiguration(configuration, ProvisioningGate.FromConfiguration(configuration));

        var channel = options.Channels.Should().ContainSingle().Subject;
        channel.ServerKey.Should().Be(Container);
        channel.Endpoint.Should().Be(new RconEndpoint("10.0.0.5", 27015));
        channel.PasswordUrn.Value.Should().Be("secret://server/palworld-server/rcon/password");
    }

    [Fact]
    public void Host_and_port_fall_back_to_the_definitions_defaults()
    {
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Enabled", "true"),
            ("Servyx:Servers:palworld-server:Rcon:Port", "not-a-port"));

        var options = RconWiringOptions.FromConfiguration(configuration, ProvisioningGate.FromConfiguration(configuration));

        options.Channels.Should().ContainSingle().Which.Endpoint
            .Should().Be(new RconEndpoint(RconWiringOptions.DefaultHost, RconWiringOptions.DefaultPort));
    }

    [Fact]
    public void A_server_key_that_cannot_address_a_secret_gets_no_channel()
    {
        // The credential's location is derived from the key, so a key that is not a legal URN segment has
        // nowhere to keep a password and therefore cannot have a channel.
        var configuration = Config(
            ("Servyx:Provisioning:Enabled", "true"),
            ("Servyx:Servers:pal world!:Rcon:Enabled", "true"));

        var options = RconWiringOptions.FromConfiguration(configuration, ProvisioningGate.FromConfiguration(configuration));

        options.Any.Should().BeFalse();
    }

    [Fact]
    public async Task A_channel_takes_the_same_write_posture_the_transport_grant_gives_the_server()
    {
        var client = new SourceRconClient();
        var secrets = new Fakes.RecordingSecretStore();
        var catalog = Palworld();

        var channels = new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            catalog,
            client,
            secrets,
            new WritableServers([Container]),
            chainFactory: DirectChain(client, catalog, secrets));

        var session = (await channels.GetSessionAsync(Container)).Should().BeOfType<WriteGuardedRconSession>().Subject;

        session.Mode.Should().Be(WriteMode.Enabled);
        session.WritesPermitted.Should().BeTrue();
    }

    [Fact]
    public async Task A_server_with_no_write_grant_gets_a_read_only_channel_that_cannot_be_quiesced()
    {
        var client = new SourceRconClient();
        var secrets = new Fakes.RecordingSecretStore();
        var catalog = Palworld();

        var channels = new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            catalog,
            client,
            secrets,
            WritableServers.None,
            chainFactory: DirectChain(client, catalog, secrets));

        var session = (await channels.GetSessionAsync(Container)).Should().BeOfType<WriteGuardedRconSession>().Subject;

        session.Mode.Should().Be(WriteMode.ReadOnly);

        // save is declared readOnly: false, so it is refused — which makes the backup fail rather than
        // quietly archiving an un-flushed world.
        var act = async () => await session.InvokeAsync("save", null);
        await act.Should().ThrowAsync<WritesDisabledException>();
    }

    [Fact]
    public async Task No_channel_means_no_session_for_any_server()
    {
        (await ServyxRconChannels.None.GetSessionAsync(Container)).Should().BeNull();
        (await ServyxRconChannels.None.GetSessionAsync("anything", "anything-else")).Should().BeNull();
    }

    [Fact]
    public void A_channel_configured_against_a_catalogue_with_no_quiesce_command_is_refused_at_startup()
    {
        var act = () => new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            new RconCommandCatalog([new RconCommand("info", "Info", ReadOnly: true)]),
            new SourceRconClient(),
            new Fakes.RecordingSecretStore(),
            new WritableServers([Container]));

        act.Should().Throw<ArgumentException>().WithMessage("*save*");
    }

    [Fact]
    public void A_channel_configured_with_no_chain_factory_is_refused_at_startup()
    {
        var act = () => new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            Palworld(),
            new SourceRconClient(),
            new Fakes.RecordingSecretStore(),
            new WritableServers([Container]));

        act.Should().Throw<ArgumentException>().WithMessage("*chain*");
    }

    [Fact]
    public async Task A_backup_context_gains_the_definitions_quiesce_step_when_a_channel_exists()
    {
        var client = new SourceRconClient();
        var secrets = new Fakes.RecordingSecretStore();
        var catalog = Palworld();

        var channels = new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            catalog,
            client,
            secrets,
            new WritableServers([Container]),
            chainFactory: DirectChain(client, catalog, secrets));

        await using var source = new ServyxBackupContextSource(
            Query(), Transport(), new BackupWiringOptions(include: ["Pal/Saved/**"]), channels);

        var context = await source.GetAsync(Container);

        context.Control.Should().NotBeNull();
        context.Quiesce.Should().NotBeNull();
        context.Quiesce!.CommandId.Should().Be(RconWiringOptions.QuiesceCommandId);
        context.Quiesce.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The other half of the quiesce contract: <c>backup.resume</c> reaches the provider whole, in declared
    /// order, rather than being reduced to a first entry the way the quiesce step is. The undo of a quiesce
    /// is frequently more than one command, and dropping all but the first would leave the server half
    /// restored — saving still disabled — with nothing reporting it.
    /// </summary>
    [Fact]
    public async Task A_backup_context_gains_every_resume_step_the_definition_declares()
    {
        var client = new SourceRconClient();
        var secrets = new Fakes.RecordingSecretStore();
        var catalog = Palworld();

        var channels = new ServyxRconChannels(
            new RconWiringOptions([new RconChannel(Container, new RconEndpoint("127.0.0.1", 25575), PasswordUrn)]),
            catalog,
            client,
            secrets,
            new WritableServers([Container]),
            chainFactory: DirectChain(client, catalog, secrets));

        var definition = DefinitionWithResume(
        [
            new QuiesceStep.Control("rcon", "save", TimeSpan.FromSeconds(30)),
            new QuiesceStep.Control("rcon", "info", TimeSpan.FromSeconds(5)),
        ]);

        await using var source = new ServyxBackupContextSource(
            Query(), Transport(), new BackupWiringOptions(include: ["Pal/Saved/**"]), channels, definition);

        var context = await source.GetAsync(Container);

        context.Resume.Should().HaveCount(2);
        context.Resume[0].CommandId.Should().Be("save");
        context.Resume[0].Timeout.Should().Be(TimeSpan.FromSeconds(30));
        context.Resume[1].CommandId.Should().Be("info");
    }

    /// <summary>
    /// With no channel there is no quiesce either, so there is nothing to undo — and a resume step attached
    /// without a channel to issue it on would be refused by the provider up front.
    /// </summary>
    [Fact]
    public async Task A_backup_context_carries_no_resume_steps_when_no_channel_is_configured()
    {
        var definition = DefinitionWithResume([new QuiesceStep.Control("rcon", "save", TimeSpan.FromSeconds(30))]);

        await using var source = new ServyxBackupContextSource(
            Query(), Transport(), new BackupWiringOptions(include: ["Pal/Saved/**"]), rcon: null, definition);

        var context = await source.GetAsync(Container);

        context.Control.Should().BeNull();
        context.Resume.Should().BeEmpty();
    }

    /// <summary>The real shipped definition declares no resume block, so the context carries none.</summary>
    [Fact]
    public async Task A_backup_context_carries_no_resume_steps_when_the_definition_declares_none()
    {
        await using var source = new ServyxBackupContextSource(
            Query(), Transport(), new BackupWiringOptions(include: ["Pal/Saved/**"]), rcon: null, RealDefinition());

        var context = await source.GetAsync(Container);

        context.Resume.Should().BeEmpty();
    }

    private static GameDefinition RealDefinition()
    {
        var repoRoot = Documentation.RepoRootLocator.Find();
        var yaml = File.ReadAllText(Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml"));
        return new GameDefinitionYamlParser().Parse(yaml).Definition!;
    }

    private static GameDefinition DefinitionWithResume(IReadOnlyList<QuiesceStep> resume)
    {
        var real = RealDefinition();
        return real with { Backup = real.Backup with { Resume = resume } };
    }

    [Fact]
    public async Task A_backup_context_carries_no_quiesce_step_when_no_channel_is_configured()
    {
        // Byte-for-byte the pre-M2 shape: the provider treats a null quiesce as "no flush was asked for"
        // and records the absence in the manifest, rather than pretending one happened.
        await using var source = new ServyxBackupContextSource(Query(), Transport(), new BackupWiringOptions(include: ["Pal/Saved/**"]));

        var context = await source.GetAsync(Container);

        context.Control.Should().BeNull();
        context.Quiesce.Should().BeNull();
    }

    private static IServerQueryService Query()
    {
        var query = Substitute.For<IServerQueryService>();
        query.GetServerDetailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ServerDetail?>(new ServerDetail(
                new ServerSummary(
                    Container,
                    Container,
                    "palworld",
                    ServerState.Running,
                    ServerHealthStatus.Healthy,
                    null,
                    null,
                    "localhost",
                    []),
                "thijsvanloef/palworld-server-docker:latest",
                "/srv/palworld",
                "/palworld",
                null,
                null,
                null,
                null,
                [])));

        return query;
    }

    private static ITransport Transport()
    {
        var transport = Substitute.For<ITransport>();

        // Stands in for the Docker Engine transport, so it must declare the container-scoped file access
        // ServyxBackupContextSource requires before it will open a container-rooted session — an unflagged
        // transport is refused outright (ContainerScopedFilesNotSupportedException).
        transport.Capabilities.Returns(
            TransportCapabilities.FileRead | TransportCapabilities.DirectoryList |
            TransportCapabilities.ContainerApi | TransportCapabilities.ContainerScopedFiles);
        transport.ConnectAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Substitute.For<IExecutionTarget>()));

        return transport;
    }
}
