namespace Servyx.Infrastructure.Rcon.Tests;

/// <summary>
/// The command catalogue is what turns "invoke a command id" into "send exactly this line", and it is the
/// only place an attacker-supplied value meets a command template. These are the rules that make that safe.
/// </summary>
public class RconCommandCatalogTests
{
    /// <summary>The definition's own <c>control.channels[rcon].commands</c> block, verbatim.</summary>
    private static RconCommandCatalog Palworld() => new(
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
        new RconCommand("kick", "KickPlayer {playerUid}", ReadOnly: false),
        new RconCommand("ban", "BanPlayer {playerUid}", ReadOnly: false),
        new RconCommand("shutdown", "Shutdown {seconds} \"{message}\"", ReadOnly: false),
        new RconCommand("doexit", "DoExit", ReadOnly: false),
    ]);

    [Fact]
    public void A_command_id_the_definition_does_not_declare_is_refused()
    {
        var act = () => Palworld().Render("rm-rf", null);

        var thrown = act.Should().Throw<RconUnknownCommandException>().Which;
        thrown.CommandId.Should().Be("rm-rf");
        thrown.Message.Should().Contain("save");
    }

    [Fact]
    public void An_empty_catalogue_refuses_everything_including_the_quiesce_command()
    {
        var act = () => RconCommandCatalog.Empty.Render("save", null);

        act.Should().Throw<RconUnknownCommandException>();
    }

    [Fact]
    public void The_read_only_classification_survives_into_the_catalogue()
    {
        var catalog = Palworld();

        catalog.Get("info").ReadOnly.Should().BeTrue();
        catalog.Get("players").ReadOnly.Should().BeTrue();
        catalog.Get("save").ReadOnly.Should().BeFalse();
        catalog.Get("broadcast").ReadOnly.Should().BeFalse();
        catalog.Get("shutdown").ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void A_parameterless_command_renders_to_its_bare_template()
    {
        Palworld().Render("save", null).Should().Be("Save");
        Palworld().Render("players", null).Should().Be("ShowPlayers");
    }

    [Fact]
    public void A_well_formed_argument_fills_exactly_its_slot()
    {
        Palworld().Render("broadcast", new Dictionary<string, string> { ["message"] = "server restarting" })
            .Should().Be("Broadcast server restarting");

        Palworld().Render("shutdown", new Dictionary<string, string>
        {
            ["seconds"] = "30",
            ["message"] = "nightly restart",
        }).Should().Be("Shutdown 30 \"nightly restart\"");
    }

    [Theory]
    [InlineData("hi\nShutdown 1 \"pwned\"")]
    [InlineData("hi\rShutdown 1 \"pwned\"")]
    [InlineData("hi\r\nDoExit")]
    [InlineData("hi\0DoExit")]
    public void A_hostile_argument_cannot_append_a_second_command(string hostile)
    {
        // The whole attack: Broadcast is a message the operator controls, DoExit stops the server. If the
        // renderer let a line break through, "say something" would become "say something and shut down".
        var act = () => Palworld().Render("broadcast", new Dictionary<string, string> { ["message"] = hostile });

        var thrown = act.Should().Throw<RconArgumentException>().Which;
        thrown.CommandId.Should().Be("broadcast");
        thrown.ParameterName.Should().Be("message");
    }

    [Fact]
    public void A_hostile_argument_cannot_escape_a_quoted_parameter()
    {
        // Shutdown's template embeds {message} inside quotes. A quote in the value would close the literal
        // and hand the remainder to the game's own parser as further tokens.
        var act = () => Palworld().Render("shutdown", new Dictionary<string, string>
        {
            ["seconds"] = "1",
            ["message"] = "bye\" ; BanPlayer everyone",
        });

        act.Should().Throw<RconArgumentException>().Which.ParameterName.Should().Be("message");
    }

    [Fact]
    public void A_hostile_argument_cannot_smuggle_a_second_placeholder_through_substitution()
    {
        // Single-pass substitution: text that came OUT of an argument is never scanned for placeholders, so
        // "{seconds}" inside a message is nine literal characters and not a second expansion.
        var rendered = Palworld().Render("broadcast", new Dictionary<string, string>
        {
            ["message"] = "countdown {seconds} then {message}",
        });

        rendered.Should().Be("Broadcast countdown {seconds} then {message}");
    }

    [Fact]
    public void A_missing_argument_is_refused_rather_than_rendered_as_an_empty_slot()
    {
        var act = () => Palworld().Render("shutdown", new Dictionary<string, string> { ["seconds"] = "10" });

        act.Should().Throw<RconArgumentException>().Which.ParameterName.Should().Be("message");
    }

    [Fact]
    public void An_argument_the_template_has_no_slot_for_is_refused_rather_than_dropped()
    {
        var act = () => Palworld().Render("save", new Dictionary<string, string> { ["message"] = "anything" });

        act.Should().Throw<RconArgumentException>().Which.ParameterName.Should().Be("message");
    }

    [Fact]
    public void An_argument_beyond_the_length_limit_is_refused()
    {
        var act = () => Palworld().Render("broadcast", new Dictionary<string, string>
        {
            ["message"] = new string('a', 5000),
        });

        act.Should().Throw<RconArgumentException>();
    }

    [Fact]
    public void A_malformed_template_is_rejected_when_the_catalogue_is_built_not_when_it_is_used()
    {
        var act = () => new RconCommandCatalog([new RconCommand("broken", "Broadcast {message", ReadOnly: false)]);

        act.Should().Throw<ArgumentException>().WithMessage("*malformed template*");
    }

    [Fact]
    public void A_duplicated_command_id_is_rejected_because_it_has_no_single_read_only_classification()
    {
        var act = () => new RconCommandCatalog(
        [
            new RconCommand("save", "Save", ReadOnly: false),
            new RconCommand("save", "Save", ReadOnly: true),
        ]);

        act.Should().Throw<ArgumentException>().WithMessage("*more than once*");
    }

    [Fact]
    public void A_command_with_no_template_is_rejected()
    {
        var act = () => new RconCommandCatalog([new RconCommand("save", "   ", ReadOnly: false)]);

        act.Should().Throw<ArgumentException>().WithMessage("*no template*");
    }

    [Fact]
    public void Lookup_is_case_insensitive_but_the_declared_id_is_what_comes_back()
    {
        Palworld().Get("SAVE").Id.Should().Be("save");
        Palworld().Contains("Players").Should().BeTrue();
        Palworld().Contains("nope").Should().BeFalse();
    }
}
