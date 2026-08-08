using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Proves <see cref="PlayerListPlan.Resolve"/> against the real, shipped definitions under the repo-root
/// <c>definitions/</c> directory — the regression this task exists to close: <c>RconSession.GetPlayersAsync</c>
/// used to hardcode the player-list command id and a legacy CSV parser shape, which would invoke a
/// nonexistent command against Minecraft (whose command is <c>list</c>, not <c>players</c>) and mis-parse
/// ARK and Factorio (whose reply shape is <c>lines</c>, not <c>csv-with-header</c>).
/// </summary>
public class PlayerListPlanResolutionTests
{
    private static GameDefinition Plan(string fileName) =>
        Parse(fileName);

    private static GameDefinition Parse(string fileName)
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", fileName);
        var result = new GameDefinitionYamlParser().Parse(File.ReadAllText(path));
        result.Definition.Should().NotBeNull($"'{fileName}' must parse successfully");
        return result.Definition!;
    }

    [Theory]
    [InlineData("palworld-docker.yaml", "players", typeof(PlayerParserSpec.CsvWithHeader))]
    [InlineData("minecraft-itzg.yaml", "list", typeof(PlayerParserSpec.SummaryLine))]
    [InlineData("ark-asa-pok.yaml", "players", typeof(PlayerParserSpec.Lines))]
    [InlineData("factorio-factoriotools.yaml", "players", typeof(PlayerParserSpec.Lines))]
    public void Each_shipped_definition_resolves_the_rcon_player_command_and_reply_shape_it_declares(
        string fileName, string expectedCommandId, Type expectedParserType)
    {
        var definition = Plan(fileName);
        var plan = PlayerListPlan.Resolve(definition.Control.Players, PlayerListPlan.RconChannelId);

        plan.IsResolved.Should().BeTrue($"'{fileName}' declares a control.players source for its rcon channel");
        plan.CommandId.Should().Be(expectedCommandId);
        plan.Parser.Should().BeOfType(expectedParserType);
    }

    [Theory]
    [MemberData(nameof(ShippedDefinitionsTests.DefinitionFiles), MemberType = typeof(ShippedDefinitionsTests))]
    public void Every_shipped_definitions_resolved_command_is_one_its_own_rcon_channel_declares(string path)
    {
        var fileName = Path.GetFileName(path);
        var result = new GameDefinitionYamlParser().Parse(File.ReadAllText(path));
        var definition = result.Definition;
        definition.Should().NotBeNull($"'{fileName}' must parse successfully");

        var rconChannel = definition!.Control.Channels.FirstOrDefault(c => c.Id == PlayerListPlan.RconChannelId);
        if (rconChannel is null || definition.Control.Players is null)
        {
            return;
        }

        var plan = PlayerListPlan.Resolve(definition.Control.Players, PlayerListPlan.RconChannelId);

        plan.IsResolved.Should().BeTrue(
            $"'{fileName}' declares both an rcon channel and a control.players block");
        rconChannel.Commands.Should().ContainKey(plan.CommandId!,
            $"'{fileName}' resolved rcon player-list command '{plan.CommandId}' must be one its own rcon channel declares");
        plan.Parser.Should().NotBeNull($"'{fileName}' resolved rcon player-list command must have a declared reply shape");
    }

    [Fact]
    public void A_higher_ranked_entry_on_another_channel_is_skipped_rather_than_invoked_over_this_one()
    {
        var definition = Plan("palworld-docker.yaml");
        definition.Control.Players!.Preferred.Should().Equal("rest.players", "rcon.players", "query");

        var plan = PlayerListPlan.Resolve(definition.Control.Players, PlayerListPlan.RconChannelId);

        plan.IsResolved.Should().BeTrue();
        plan.CommandId.Should().Be("players");
        plan.Parser.Should().BeOfType<PlayerParserSpec.CsvWithHeader>();
    }
}
