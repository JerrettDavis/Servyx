using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon.Tests.Fakes;

namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The write guard classifies control commands by the definition's declared <c>readOnly</c> flag, never by
/// verb — the rule <c>docs/abstractions.md</c> §8's implementer note states, and the rule the read-only
/// safety suite depends on.
/// </summary>
public class WriteGuardedRconSessionTests
{
    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
        new RconCommand("shutdown", "Shutdown {seconds} \"{message}\"", ReadOnly: false),
        new RconCommand("doexit", "DoExit", ReadOnly: false),
    ]);

    private static (WriteGuardedRconSession Guarded, ScriptedRconSession Inner) Build(WriteMode mode)
    {
        var inner = new ScriptedRconSession();
        return (new WriteGuardedRconSession(inner, Palworld(), mode, "palworld-server"), inner);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("players")]
    public async Task A_read_only_command_passes_the_gate_on_a_read_only_server(string commandId)
    {
        var (guarded, inner) = Build(WriteMode.ReadOnly);

        await guarded.InvokeAsync(commandId, null);

        inner.Invoked.Should().ContainSingle().Which.Should().Be(commandId);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("broadcast")]
    [InlineData("shutdown")]
    [InlineData("doexit")]
    public async Task A_mutating_command_is_refused_on_a_read_only_server(string commandId)
    {
        var (guarded, inner) = Build(WriteMode.ReadOnly);

        var act = async () => await guarded.InvokeAsync(commandId, null);

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("palworld-server");

        // Refused before the inner session — and therefore before the secret store and the socket.
        inner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_only_refuses_exactly_what_read_only_refuses()
    {
        var (guarded, inner) = Build(WriteMode.PreviewOnly);

        var act = async () => await guarded.InvokeAsync("save", null);

        await act.Should().ThrowAsync<WritesDisabledException>();
        inner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mutating_command_reaches_the_inner_session_once_writes_are_granted()
    {
        var (guarded, inner) = Build(WriteMode.Enabled);

        await guarded.InvokeAsync("save", null);

        inner.Invoked.Should().ContainSingle().Which.Should().Be("save");
    }

    [Fact]
    public async Task An_undeclared_command_is_refused_in_every_mode_because_it_has_no_classification()
    {
        foreach (var mode in new[] { WriteMode.ReadOnly, WriteMode.PreviewOnly, WriteMode.Enabled })
        {
            var (guarded, inner) = Build(mode);

            var act = async () => await guarded.InvokeAsync("rm-rf", null);

            await act.Should().ThrowAsync<RconUnknownCommandException>();
            inner.Invoked.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task The_raw_escape_hatch_is_treated_as_mutating_because_it_declares_no_intent()
    {
        var (guarded, _) = Build(WriteMode.ReadOnly);

        var act = async () => await guarded.SendRawAsync("Info");

        (await act.Should().ThrowAsync<WritesDisabledException>())
            .Which.Message.Should().Contain("no declared readOnly");
    }

    [Fact]
    public async Task Listing_players_is_never_gated()
    {
        var (guarded, _) = Build(WriteMode.ReadOnly);

        (await guarded.GetPlayersAsync()).Players.Should().BeEmpty();
    }

    [Fact]
    public void The_guard_reports_its_posture_honestly()
    {
        Build(WriteMode.ReadOnly).Guarded.WritesPermitted.Should().BeFalse();
        Build(WriteMode.PreviewOnly).Guarded.WritesPermitted.Should().BeFalse();
        Build(WriteMode.Enabled).Guarded.WritesPermitted.Should().BeTrue();
    }
}
