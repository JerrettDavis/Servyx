using System.Text;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// Drives <see cref="SourceRconClient"/> against a loopback server that speaks the real wire protocol.
/// Nothing here mocks the protocol; every assertion is about bytes that crossed a socket.
/// </summary>
public class SourceRconClientTests
{
    private static TimeoutPolicy Fast(int commandMilliseconds = 5000) => new(
        Connect: TimeSpan.FromSeconds(5),
        Command: TimeSpan.FromMilliseconds(commandMilliseconds),
        FileTransfer: TimeSpan.FromSeconds(5),
        IdleEviction: TimeSpan.FromMinutes(1),
        MaxConcurrentSessions: 1);

    [Fact]
    public async Task A_correct_password_authenticates_and_the_command_reaches_the_server()
    {
        await using var server = new FakeRconServer(responseFragments: ["Complete Save"]);
        var client = new SourceRconClient(Fast());

        var response = await client.SendAsync(server.Endpoint, FakeRconServer.DefaultPassword, "Save");

        response.Success.Should().BeTrue();
        response.Text.Should().Be("Complete Save");
        server.Commands.Should().ContainSingle().Which.Should().Be("Save");
    }

    [Fact]
    public async Task A_junk_preamble_packet_before_the_auth_verdict_is_tolerated()
    {
        // Several real implementations emit an empty SERVERDATA_RESPONSE_VALUE ahead of the verdict.
        await using var server = new FakeRconServer(emitJunkPreamble: true, responseFragments: ["Welcome to Pal Server"]);
        var client = new SourceRconClient(Fast());

        var response = await client.SendAsync(server.Endpoint, FakeRconServer.DefaultPassword, "Info");

        response.Text.Should().Be("Welcome to Pal Server");
    }

    [Fact]
    public async Task A_rejected_password_is_an_authentication_failure_and_never_an_empty_success()
    {
        // THE classic trap: the rejection arrives as a well-formed packet with an EMPTY body and id -1. A
        // client that reads only the body reports a command that "worked" and returned nothing.
        await using var server = new FakeRconServer(rejectAuth: true);
        var client = new SourceRconClient(Fast());

        var act = async () => await client.SendAsync(server.Endpoint, "wrong-password", "Save");

        (await act.Should().ThrowAsync<RconAuthenticationFailedException>())
            .Which.Message.Should().Contain("-1");

        // And nothing was sent: a rejected session must not run the command anyway.
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wrong_password_against_an_otherwise_healthy_server_is_also_an_authentication_failure()
    {
        await using var server = new FakeRconServer(password: "the-real-one");
        var client = new SourceRconClient(Fast());

        var act = async () => await client.SendAsync(server.Endpoint, "not-the-real-one", "Save");

        await act.Should().ThrowAsync<RconAuthenticationFailedException>();
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_response_split_across_several_packets_is_reassembled_in_order()
    {
        var fragments = new[]
        {
            "name,playeruid,steamid\n",
            "Alice,1111,7656119\n",
            "Bob,2222,7656120\n",
            "Carol,3333,7656121",
        };

        await using var server = new FakeRconServer(responseFragments: fragments);
        var client = new SourceRconClient(Fast());

        var response = await client.SendAsync(server.Endpoint, FakeRconServer.DefaultPassword, "ShowPlayers");

        response.Text.Should().Be(string.Concat(fragments));
    }

    [Fact]
    public async Task A_large_multi_packet_response_is_reassembled_without_loss()
    {
        // Each fragment is near the single-packet body budget, so this is genuinely several packets on the
        // wire rather than a fast path that happens to look split.
        var fragments = Enumerable.Range(0, 12)
            .Select(i => new string((char)('a' + i), 3000))
            .ToArray();

        await using var server = new FakeRconServer(responseFragments: fragments);
        var client = new SourceRconClient(Fast());

        var response = await client.SendAsync(server.Endpoint, FakeRconServer.DefaultPassword, "ShowPlayers");

        response.Text.Length.Should().Be(fragments.Sum(f => f.Length));
        response.Text.Should().Be(string.Concat(fragments));
    }

    [Fact]
    public async Task A_server_that_authenticates_and_then_goes_silent_times_out_rather_than_hanging()
    {
        await using var server = new FakeRconServer(silentAfterAuth: true);
        var client = new SourceRconClient(Fast(commandMilliseconds: 250));

        var act = async () => await client.SendAsync(server.Endpoint, FakeRconServer.DefaultPassword, "Save");

        await act.Should().ThrowAsync<RconTimeoutException>();
    }

    [Fact]
    public async Task A_closed_port_is_reported_as_unreachable_rather_than_as_an_empty_response()
    {
        // Bind and immediately release, so the port is almost certainly free and nothing is listening.
        RconEndpoint endpoint;
        await using (var scratch = new FakeRconServer())
        {
            endpoint = scratch.Endpoint;
        }

        var client = new SourceRconClient(new TimeoutPolicy(
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1),
            1));

        var act = async () => await client.SendAsync(endpoint, FakeRconServer.DefaultPassword, "Save");

        await act.Should().ThrowAsync<RconException>();
    }

    [Fact]
    public async Task A_command_line_carrying_a_newline_is_refused_before_a_socket_is_opened()
    {
        await using var server = new FakeRconServer();
        var client = new SourceRconClient(Fast());

        var act = async () => await client.SendAsync(
            server.Endpoint,
            FakeRconServer.DefaultPassword,
            "Broadcast hi\nShutdown 1 \"pwned\"");

        await act.Should().ThrowAsync<RconArgumentException>();
        server.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task The_password_never_appears_in_an_exception_a_failure_produces()
    {
        const string password = "S3cr3t-Palworld-Admin-Password";

        await using var server = new FakeRconServer(password: "a-different-password");
        var client = new SourceRconClient(Fast());

        var act = async () => await client.SendAsync(server.Endpoint, password, "Save");
        var thrown = (await act.Should().ThrowAsync<RconAuthenticationFailedException>()).Which;

        thrown.ToString().Should().NotContain(password);
        thrown.Message.Should().NotContain(password);
    }

    [Fact]
    public async Task The_password_is_not_readable_from_any_public_member_of_the_client()
    {
        const string password = "S3cr3t-Palworld-Admin-Password";

        await using var server = new FakeRconServer(password: password);
        var client = new SourceRconClient(Fast());

        await client.SendAsync(server.Endpoint, password, "Save");

        // The client is a singleton in the composition root, so anything it retained would outlive every
        // call. Nothing on its public surface may echo the credential back.
        var surface = client.GetType()
            .GetProperties()
            .Select(p => p.GetValue(client)?.ToString())
            .Concat(client.GetType().GetFields().Select(f => f.GetValue(client)?.ToString()))
            .Where(v => v is not null)
            .ToList();

        surface.Should().NotContain(v => v!.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public void The_packet_encoder_writes_the_documented_little_endian_layout()
    {
        // Encoded independently by the test double, so a shared sign or endianness error cannot cancel out.
        var expected = SourceRconWire.Encode(7, SourceRconWire.ExecCommand, Encoding.UTF8.GetBytes("Save"));

        expected.Length.Should().Be(4 + 4 + 4 + 4 + 2);
        expected[0].Should().Be(14);      // size low byte: 4 (id) + 4 (type) + 4 (body) + 2 (terminators)
        expected[1].Should().Be(0);
        expected[4].Should().Be(7);       // id, little-endian
        expected[8].Should().Be(2);       // SERVERDATA_EXECCOMMAND
        expected[^1].Should().Be(0);      // packet terminator
        expected[^2].Should().Be(0);      // body terminator
    }
}
