using Servyx.Domain.Definitions.Model;

namespace Servyx.Infrastructure.Rcon.Tests.PlayerParsing;

/// <summary>
/// The parse-time half of the player-list isolation boundary: <see cref="RconPlayerListParser.Parse(string?, PlayerParserSpec?)"/>
/// is a total function. Every reply — empty, whitespace, truncated, binary, or simply in a completely
/// different format — produces a snapshot, and none produces an exception.
/// </summary>
/// <remarks>
/// This matters because several of the reply formats Servyx understands are modelled from unverified
/// community reports of what a given server actually prints. A wrong guess about a reply format has to cost
/// a "player count unavailable" line in the UI and nothing else — never a thrown exception on a polling
/// loop, and never a failure that could propagate into a lifecycle, backup, or readiness decision.
/// </remarks>
public class PlayerListParserRobustnessTests
{
    /// <summary>
    /// Replies that no declared shape can make sense of. Chosen so that "unreadable" is unambiguous for
    /// EVERY shape at once: nothing here is a valid CSV header row, a summary sentence, an entry line, or
    /// parseable JSON.
    /// </summary>
    private static readonly (string Label, string Reply)[] Unreadable =
    [
        ("empty", ""),
        ("whitespace", "   \n\t\n   "),
        ("newlines only", "\n\n\n"),
        ("one word", "garbage"),
        ("a sentence", "not a player list at all"),
        ("a refusal", "Unknown command. Type 'help' for help."),
        ("truncated summary", "There are of a max of players online:"),
        ("truncated json", "{\"data\":{\"serverGameSt"),
        ("control characters", "\u0001\u0002\u0003"),
        ("a lone number", "7"),
    ];

    public static TheoryData<string, string, string> UnreadableCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var shape in PlayerParserShapes.All)
        {
            foreach (var (label, reply) in Unreadable)
            {
                data.Add(shape, label, reply);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnreadableCases))]
    public void Parse_AnUnreadableReply_YieldsUnknownWithADiagnostic_AndNeverThrows(string shape, string label, string reply)
    {
        var spec = PlayerParserShapes.For(shape);
        PlayerListSnapshot? snapshot = null;

        var act = () => snapshot = RconPlayerListParser.Parse(reply, spec);

        act.Should().NotThrow($"'{shape}' must survive a {label} reply");
        snapshot!.Fidelity.Should().Be(PlayerListFidelity.Unknown, $"'{shape}' cannot read a {label} reply");
        snapshot.Players.Should().BeEmpty();
        snapshot.Count.Should().BeNull();
        snapshot.Diagnostic.Should().NotBeNullOrWhiteSpace("a degraded result must be able to explain itself");
    }

    [Fact]
    public void Parse_ANullReply_YieldsUnknown()
    {
        foreach (var shape in PlayerParserShapes.All)
        {
            RconPlayerListParser.Parse(null, PlayerParserShapes.For(shape))
                .Fidelity.Should().Be(PlayerListFidelity.Unknown);
        }
    }

    [Fact]
    public void Parse_WithNoDeclaredParser_YieldsUnknown_RatherThanThrowing()
    {
        var snapshot = RconPlayerListParser.Parse("name,uid,steam\nAlice,1,2", spec: null);

        snapshot.Fidelity.Should().Be(PlayerListFidelity.Unknown);
        snapshot.Diagnostic.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Every fixture reply fed to every OTHER shape's parser — the "the operator wired the wrong parser to
    /// this channel" case. The result is not asserted (a coincidental match is allowed to be a match); that
    /// nothing throws is.
    /// </summary>
    public static TheoryData<string, string, string> CrossShapeCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var source in PlayerParserShapes.All)
        {
            foreach (var target in PlayerParserShapes.All.Where(s => s != source))
            {
                data.Add(target, source, "populated.txt");
                data.Add(target, source, "empty-server.txt");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CrossShapeCases))]
    public void Parse_AReplyInSomeOtherShapesFormat_NeverThrows(string parserShape, string replyShape, string fixture)
    {
        var reply = PlayerParserShapes.Fixture(replyShape, fixture);
        var spec = PlayerParserShapes.For(parserShape);

        var act = () => RconPlayerListParser.Parse(reply, spec);

        act.Should().NotThrow($"parsing a '{replyShape}' reply with the '{parserShape}' parser must degrade, not fail");
    }

    /// <summary>
    /// A reply far larger than any real one, fed to every shape. The patterns are compiled
    /// <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/> with an explicit match
    /// timeout, so this is bounded work rather than an open-ended one — this test exists to keep it that way.
    /// </summary>
    [Fact]
    public void Parse_APathologicallyLargeReply_StillCompletesWithoutThrowing()
    {
        var reply = string.Join("\n", Enumerable.Repeat(new string('a', 2_000) + "," + new string('b', 2_000), 500));

        foreach (var shape in PlayerParserShapes.All)
        {
            var act = () => RconPlayerListParser.Parse(reply, PlayerParserShapes.For(shape));
            act.Should().NotThrow($"'{shape}' must bound its own work");
        }
    }
}
