using System.Net;
using System.Net.Sockets;
using Servyx.Domain.Connectors;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The session is where a definition's command catalogue, a secret URN and the protocol client meet. These
/// assertions are end to end: a real <see cref="SourceRconClient"/> against a real loopback server, with
/// the credential resolved through a real <see cref="ISecretStore"/>.
/// </summary>
public class RconSessionTests
{
    private const string Password = "S3cr3t-Palworld-Admin-Password";

    private static readonly SecretUrn PasswordUrn =
        SecretUrn.Create("server", "palworld-server", "rcon", "password");

    private static TimeoutPolicy Fast() => new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(1),
        1);

    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("list", "List", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
        new RconCommand("shutdown", "Shutdown {seconds} \"{message}\"", ReadOnly: false),
    ]);

    private static PlayerListPlan CsvPlan() => new(
        "players",
        new PlayerParserSpec.CsvWithHeader(["name", "playerUid", "steamId"], "name", null),
        "test plan");

    private static PlayerListPlan SummaryPlan() => new(
        "list",
        new PlayerParserSpec.SummaryLine(
            CompiledPattern.TryCompile(
                @"There are (?<count>\d+) of a max(?: of)? (?<max>\d+) players online:?(?<names>.*)", out _)!,
            PlayerParserSpec.SummaryLine.DefaultNameSeparator),
        "test plan");

    private static (RconSession Session, InMemorySecretStore Secrets) Build(
        FakeRconServer server,
        IRconAuditSink? audit = null,
        PlayerListPlan? players = null)
    {
        var secrets = new InMemorySecretStore().With(PasswordUrn, Password);
        var session = new RconSession(
            new SourceRconClient(Fast()),
            server.Endpoint,
            Palworld(),
            secrets,
            PasswordUrn,
            audit,
            players: players);

        return (session, secrets);
    }

    /// <summary>An endpoint nothing is listening on: bound to grab a free ephemeral port, then released.</summary>
    private static RconEndpoint UnusedEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new RconEndpoint("127.0.0.1", port);
    }

    [Fact]
    public async Task Invoking_a_declared_command_resolves_the_credential_at_the_point_of_use()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["Complete Save"]);
        var (session, secrets) = Build(server);

        secrets.GetCalls.Should().Be(0, "no credential is resolved until a command is actually issued");

        var response = await session.InvokeAsync("save", null);

        response.Text.Should().Be("Complete Save");
        secrets.GetCalls.Should().Be(1);
        server.Commands.Should().ContainSingle().Which.Should().Be("Save");
    }

    [Fact]
    public async Task An_undeclared_command_id_is_refused_before_the_credential_is_even_resolved()
    {
        await using var server = new FakeRconServer(password: Password);
        var (session, secrets) = Build(server);

        var act = async () => await session.InvokeAsync("rm-rf", null);

        await act.Should().ThrowAsync<RconUnknownCommandException>();
        secrets.GetCalls.Should().Be(0);
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hostile_argument_never_reaches_the_wire()
    {
        await using var server = new FakeRconServer(password: Password);
        var (session, _) = Build(server);

        var act = async () => await session.InvokeAsync(
            "broadcast",
            new Dictionary<string, string> { ["message"] = "hi\nDoExit" });

        await act.Should().ThrowAsync<RconArgumentException>();

        // The point of the whole exercise: the server saw nothing, so DoExit could not have run.
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_credential_is_an_authentication_failure_naming_the_urn_not_the_value()
    {
        await using var server = new FakeRconServer(password: Password);

        var session = new RconSession(
            new SourceRconClient(Fast()),
            server.Endpoint,
            Palworld(),
            new InMemorySecretStore(),
            PasswordUrn);

        var act = async () => await session.InvokeAsync("save", null);

        (await act.Should().ThrowAsync<RconAuthenticationFailedException>())
            .Which.Message.Should().Contain(PasswordUrn.Value);
    }

    [Fact]
    public async Task The_password_never_appears_in_an_exception_or_on_the_session_surface()
    {
        await using var server = new FakeRconServer(password: "a-completely-different-password");
        var (session, _) = Build(server);

        var act = async () => await session.InvokeAsync("save", null);
        var thrown = (await act.Should().ThrowAsync<RconAuthenticationFailedException>()).Which;

        thrown.ToString().Should().NotContain(Password);

        var surface = session.GetType()
            .GetProperties()
            .Select(p => p.GetValue(session)?.ToString())
            .Concat(session.GetType().GetFields().Select(f => f.GetValue(session)?.ToString()))
            .Where(v => v is not null)
            .ToList();

        surface.Should().NotContain(v => v!.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_raw_escape_hatch_is_unavailable_without_an_audit_sink()
    {
        await using var server = new FakeRconServer(password: Password);
        var (session, _) = Build(server);

        var act = async () => await session.SendRawAsync("Info");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("audit");
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task The_raw_escape_hatch_records_before_it_sends()
    {
        var audit = new RecordingAuditSink();
        await using var server = new FakeRconServer(password: Password, responseFragments: ["done"]);
        var (session, _) = Build(server, audit);

        var response = await session.SendRawAsync("SomeUndocumentedCommand 1");

        response.Text.Should().Be("done");
        audit.Recorded.Should().ContainSingle().Which.Should().Be("SomeUndocumentedCommand 1");
        server.Commands.Should().ContainSingle().Which.Should().Be("SomeUndocumentedCommand 1");
    }

    [Fact]
    public async Task A_raw_command_carrying_a_newline_is_refused_before_it_is_recorded()
    {
        var audit = new RecordingAuditSink();
        await using var server = new FakeRconServer(password: Password);
        var (session, _) = Build(server, audit);

        var act = async () => await session.SendRawAsync("Info\nDoExit");

        await act.Should().ThrowAsync<RconArgumentException>();

        // An audit line naming only the first half of a two-command line would be a false record.
        audit.Recorded.Should().BeEmpty();
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Players_are_parsed_from_the_definitions_csv_with_header_shape()
    {
        var fragments = new[]
        {
            "name,playeruid,steamid\n",
            "Alice,1111,76561190000000001\n",
            "Bob,2222,76561190000000002",
        };

        await using var server = new FakeRconServer(password: Password, responseFragments: fragments);
        var (session, _) = Build(server, players: CsvPlan());

        var snapshot = await session.GetPlayersAsync();

        server.Commands.Should().ContainSingle().Which.Should().Be("ShowPlayers");
        snapshot.Players.Should().HaveCount(2);
        snapshot.Players[0].Should().Be(new PlayerInfo("Alice", "1111", "76561190000000001"));
        snapshot.Players[1].Name.Should().Be("Bob");
    }

    [Fact]
    public async Task An_empty_player_list_yields_no_players_rather_than_a_phantom_one()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["name,playeruid,steamid"]);
        var (session, _) = Build(server, players: CsvPlan());

        var snapshot = await session.GetPlayersAsync();

        snapshot.Players.Should().BeEmpty();
        snapshot.Fidelity.Should().Be(PlayerListFidelity.NamesAndCount, "a well-formed header with no data rows is a genuine, trustworthy zero");
        snapshot.List.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_plan_naming_a_different_command_invokes_that_command_and_reads_its_own_reply_shape()
    {
        await using var server = new FakeRconServer(
            password: Password,
            responseFragments: ["There are 2 of a max of 20 players online: Alice, Bob"]);
        var (session, _) = Build(server, players: SummaryPlan());

        var snapshot = await session.GetPlayersAsync();

        server.Commands.Should().ContainSingle().Which.Should().Be("List");
        snapshot.Fidelity.Should().Be(PlayerListFidelity.NamesAndCount);
        snapshot.Players.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_session_with_no_plan_reports_an_unknown_roster_and_sends_nothing()
    {
        await using var server = new FakeRconServer(password: Password);
        var (session, secrets) = Build(server);

        var snapshot = await session.GetPlayersAsync();

        snapshot.Fidelity.Should().Be(PlayerListFidelity.Unknown);
        snapshot.Players.Should().BeEmpty();
        snapshot.List.Count.Should().BeNull();
        snapshot.List.Diagnostic.Should().NotBeNullOrWhiteSpace();
        server.Commands.Should().BeEmpty();
        secrets.GetCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_reply_that_does_not_match_the_declared_shape_is_unknown_rather_than_a_confident_zero()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["gibberish"]);
        var (session, _) = Build(server, players: SummaryPlan());

        var snapshot = await session.GetPlayersAsync();

        snapshot.Fidelity.Should().Be(PlayerListFidelity.Unknown);
        snapshot.Players.Should().BeEmpty();
        snapshot.List.Count.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_endpoint_propagates_rather_than_reporting_that_nobody_is_connected()
    {
        var endpoint = UnusedEndpoint();
        var secrets = new InMemorySecretStore().With(PasswordUrn, Password);
        var session = new RconSession(
            new SourceRconClient(Fast()),
            endpoint,
            Palworld(),
            secrets,
            PasswordUrn,
            players: CsvPlan());

        var act = async () => await session.GetPlayersAsync();

        await act.Should().ThrowAsync<RconUnreachableException>();
    }

    [Fact]
    public async Task A_plan_naming_a_command_the_catalogue_does_not_declare_is_refused_before_the_socket()
    {
        await using var server = new FakeRconServer(password: Password);
        var plan = new PlayerListPlan("nonexistent", null, "test plan");
        var (session, secrets) = Build(server, players: plan);

        var act = async () => await session.GetPlayersAsync();

        await act.Should().ThrowAsync<RconUnknownCommandException>();
        secrets.GetCalls.Should().Be(0);
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public void A_session_cannot_be_built_without_a_credential_locator()
    {
        var act = () => new RconSession(
            new SourceRconClient(),
            new RconEndpoint("127.0.0.1", 25575),
            Palworld(),
            new InMemorySecretStore(),
            default);

        act.Should().Throw<ArgumentException>().WithMessage("*URN*");
    }

    [Fact]
    public async Task A_read_only_guard_refuses_a_mutating_command_before_a_socket_is_opened()
    {
        await using var server = new FakeRconServer(password: Password);
        var (inner, secrets) = Build(server);
        var guarded = new WriteGuardedRconSession(inner, Palworld(), WriteMode.ReadOnly, "palworld-server");

        var act = async () => await guarded.InvokeAsync("save", null);

        await act.Should().ThrowAsync<WritesDisabledException>();
        secrets.GetCalls.Should().Be(0);
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_only_guard_still_permits_the_definitions_read_only_commands()
    {
        await using var server = new FakeRconServer(password: Password, responseFragments: ["Welcome to Pal Server"]);
        var (inner, _) = Build(server);
        var guarded = new WriteGuardedRconSession(inner, Palworld(), WriteMode.ReadOnly, "palworld-server");

        (await guarded.InvokeAsync("info", null)).Text.Should().Be("Welcome to Pal Server");
        server.Commands.Should().ContainSingle().Which.Should().Be("Info");
    }
}
