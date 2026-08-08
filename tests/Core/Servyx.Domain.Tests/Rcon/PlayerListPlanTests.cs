using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;

namespace Servyx.Domain.Tests.Rcon;

public class PlayerListPlanTests
{
    private const string ChannelId = "rcon";

    private static readonly PlayerParserSpec.CsvWithHeader StandInSpec = new(["a"], "a", null);

    private static PlayersConfig Players(
        IReadOnlyDictionary<string, PlayerParserSpec>? parsers,
        params string[] preferred) =>
        new(preferred, TimeSpan.FromSeconds(30), parsers ?? new Dictionary<string, PlayerParserSpec>(StringComparer.Ordinal));

    private static Dictionary<string, PlayerParserSpec> Parsers(params (string Key, PlayerParserSpec Spec)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Spec, StringComparer.Ordinal);

    [Fact]
    public void No_players_config_at_all_yields_an_unresolved_plan_rather_than_a_default_command()
    {
        var plan = PlayerListPlan.Resolve(null, ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.CommandId.Should().BeNull();
        plan.Parser.Should().BeNull();
        plan.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_empty_preferred_list_yields_an_unresolved_plan_naming_the_channel()
    {
        var plan = PlayerListPlan.Resolve(Players(null), ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.Diagnostic.Should().Contain(ChannelId);
    }

    [Fact]
    public void A_preferred_list_naming_only_other_channels_yields_an_unresolved_plan()
    {
        var plan = PlayerListPlan.Resolve(Players(null, "rest.players", "query"), ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_channel_id_that_is_only_a_prefix_of_another_is_not_matched()
    {
        var parsers = Parsers(("rconx.players", StandInSpec));
        var plan = PlayerListPlan.Resolve(Players(parsers, "rconx.players"), ChannelId);

        plan.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void The_first_preferred_entry_this_channel_can_actually_read_is_the_one_used()
    {
        var parsers = Parsers(("rcon.list", StandInSpec));
        var plan = PlayerListPlan.Resolve(Players(parsers, "rest.players", "rcon.players", "rcon.list"), ChannelId);

        // "rcon.players" is skipped because it has no declared parser; "rcon.list" is the first readable one.
        plan.IsResolved.Should().BeTrue();
        plan.CommandId.Should().Be("list");
        plan.Parser.Should().BeSameAs(StandInSpec);
    }

    [Fact]
    public void A_preferred_entry_with_no_declared_parser_still_resolves_its_command_but_no_reply_shape()
    {
        var plan = PlayerListPlan.Resolve(Players(null, "rcon.players"), ChannelId);

        plan.IsResolved.Should().BeTrue();
        plan.CommandId.Should().Be("players");
        plan.Parser.Should().BeNull();
        plan.Diagnostic.Should().Contain("rcon.players");
    }

    [Fact]
    public void A_declared_parser_that_no_preferred_entry_names_is_never_used()
    {
        var parsers = Parsers(("rcon.players", StandInSpec));
        var plan = PlayerListPlan.Resolve(Players(parsers, "rest.players"), ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.Parser.Should().BeNull();
    }

    [Fact]
    public void A_preferred_entry_naming_the_channel_but_no_operation_resolves_no_command()
    {
        var plan = PlayerListPlan.Resolve(Players(null, ChannelId), ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.CommandId.Should().BeNull();
        plan.Diagnostic.Should().Contain(ChannelId);
    }

    [Theory]
    [MemberData(nameof(UnresolvedScenarios))]
    public void Every_unresolved_plan_carries_a_diagnostic_explaining_why(PlayersConfig? players)
    {
        var plan = PlayerListPlan.Resolve(players, ChannelId);

        plan.IsResolved.Should().BeFalse();
        plan.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    public static IEnumerable<object?[]> UnresolvedScenarios()
    {
        yield return [null];
        yield return [Players(null)];
        yield return [Players(null, "rest.players", "query")];
        yield return [Players(null, ChannelId)];
        yield return [Players(Parsers(("rcon.players", StandInSpec)), "rest.players")];
    }

    [Fact]
    public void The_none_plan_resolves_nothing_and_says_why()
    {
        PlayerListPlan.None.IsResolved.Should().BeFalse();
        PlayerListPlan.None.CommandId.Should().BeNull();
        PlayerListPlan.None.Parser.Should().BeNull();
        PlayerListPlan.None.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }
}
