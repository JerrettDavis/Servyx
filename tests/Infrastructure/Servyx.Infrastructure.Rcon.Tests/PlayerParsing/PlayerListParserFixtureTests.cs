using Servyx.Domain.Definitions.Model;

namespace Servyx.Infrastructure.Rcon.Tests.PlayerParsing;

/// <summary>
/// Drives <see cref="RconPlayerListParser"/> from real sample replies held as files under
/// <c>PlayerParsing/Fixtures/&lt;shape&gt;/</c>, one directory per declared reply shape, each including an
/// empty-server case.
/// </summary>
/// <remarks>
/// The fixtures are read from the repository (located by <c>RepoRootLocator</c>) rather than copied to the
/// build output, matching how the shipped-definition tests read <c>definitions/</c>: a fixture is a piece of
/// evidence about a wire format, and editing one should change what the test asserts on the next run
/// without a build-configuration step in between.
/// </remarks>
public class PlayerListParserFixtureTests
{
    public static TheoryData<string, string, PlayerListFidelity, int?, int?, string> Cases() => new()
    {
        { PlayerParserShapes.CsvWithHeader, "populated.txt", PlayerListFidelity.NamesAndCount, 2, null, "Alice|Bob" },
        { PlayerParserShapes.CsvWithHeader, "empty-server.txt", PlayerListFidelity.NamesAndCount, 0, null, "" },
        { PlayerParserShapes.CsvWithHeader, "one-line-has-the-wrong-field-count.txt", PlayerListFidelity.NamesAndCount, 1, null, "Alice" },

        { PlayerParserShapes.SummaryLine, "populated.txt", PlayerListFidelity.NamesAndCount, 2, 20, "Alice|Bob" },
        { PlayerParserShapes.SummaryLine, "empty-server.txt", PlayerListFidelity.NamesAndCount, 0, 20, "" },
        { PlayerParserShapes.SummaryLine, "count-without-the-name-tail.txt", PlayerListFidelity.CountOnly, 3, 20, "" },

        { PlayerParserShapes.LinesNumbered, "populated.txt", PlayerListFidelity.NamesAndCount, 2, null, "Alice|Bob" },
        { PlayerParserShapes.LinesNumbered, "empty-server.txt", PlayerListFidelity.NamesAndCount, 0, null, "" },

        { PlayerParserShapes.LinesHeader, "populated.txt", PlayerListFidelity.NamesAndCount, 2, null, "alice|bob" },
        { PlayerParserShapes.LinesHeader, "empty-server.txt", PlayerListFidelity.NamesAndCount, 0, null, "" },
        { PlayerParserShapes.LinesHeader, "header-count-disagrees-with-the-entries.txt", PlayerListFidelity.CountOnly, 5, null, "" },

        { PlayerParserShapes.CountPattern, "populated.txt", PlayerListFidelity.CountOnly, 5, null, "" },
        { PlayerParserShapes.CountPattern, "empty-server.txt", PlayerListFidelity.CountOnly, 0, null, "" },

        { PlayerParserShapes.CountJson, "populated.txt", PlayerListFidelity.CountOnly, 3, null, "" },
        { PlayerParserShapes.CountJson, "empty-server.txt", PlayerListFidelity.CountOnly, 0, null, "" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Parse_AFixtureReply_YieldsTheExpectedFidelityCountAndNames(
        string shape,
        string fixture,
        PlayerListFidelity expectedFidelity,
        int? expectedCount,
        int? expectedMax,
        string expectedNames)
    {
        var snapshot = RconPlayerListParser.Parse(
            PlayerParserShapes.Fixture(shape, fixture),
            PlayerParserShapes.For(shape));

        snapshot.Fidelity.Should().Be(expectedFidelity, $"'{shape}/{fixture}' says: {snapshot.Diagnostic}");
        snapshot.Count.Should().Be(expectedCount);
        snapshot.Max.Should().Be(expectedMax);

        string[] names = expectedNames.Length == 0 ? [] : expectedNames.Split('|');
        snapshot.Players.Select(p => p.Name).Should().Equal(names);
    }

    [Fact]
    public void Parse_ACountOnlyReply_CarriesNoPlayers_BecauseCountOnlyIsAnOutcomeAndNotAFailure()
    {
        var snapshot = RconPlayerListParser.Parse(
            PlayerParserShapes.Fixture(PlayerParserShapes.CountJson, "populated.txt"),
            PlayerParserShapes.For(PlayerParserShapes.CountJson));

        snapshot.Fidelity.Should().Be(PlayerListFidelity.CountOnly);
        snapshot.Players.Should().BeEmpty();
        snapshot.Count.Should().Be(3, "a UI must be able to render 'N players online' with no roster at all");
    }

    [Fact]
    public void Parse_AReplyWithOneUnreadableLine_KeepsTheReadableOnes_AndExplainsTheRest()
    {
        var snapshot = RconPlayerListParser.Parse(
            PlayerParserShapes.Fixture(PlayerParserShapes.CsvWithHeader, "one-line-has-the-wrong-field-count.txt"),
            PlayerParserShapes.For(PlayerParserShapes.CsvWithHeader));

        // A name containing the field separator cannot be recovered from a format with no quoting, so the
        // line yields no player rather than a truncated name wearing someone else's identifier.
        snapshot.Players.Should().ContainSingle().Which.Name.Should().Be("Alice");
        snapshot.Diagnostic.Should().NotBeNull();
    }

    [Fact]
    public void EveryDeclaredShape_HasAnEmptyServerFixture()
    {
        // The empty-server reply is the case most likely to be a special sentinel rather than "the same
        // format with zero entries", so every shape must state what its own looks like.
        foreach (var shape in PlayerParserShapes.All)
        {
            var act = () => PlayerParserShapes.Fixture(shape, "empty-server.txt");
            act.Should().NotThrow($"'{shape}' must declare an empty-server fixture");
        }
    }
}
