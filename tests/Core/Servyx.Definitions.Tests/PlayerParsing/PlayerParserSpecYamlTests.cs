using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests.PlayerParsing;

/// <summary>
/// Covers the four <c>control.players.parsers.*.kind</c> shapes: that each parses, that every required
/// piece of each is validated at definition-load time rather than at match time, and — the reason the
/// discriminator was left spelled <c>kind</c> in kebab-case — that the shape already shipping in
/// <c>definitions/palworld-docker.yaml</c> keeps parsing with a zero-line diff.
/// </summary>
public class PlayerParserSpecYamlTests
{
    /// <summary>The exact line the real, shipped definition declares its player parser on.</summary>
    private const string ShippedParserLine =
        "      rcon.players: { kind: csv-with-header, columns: [name, playerUid, steamId] }";

    private static DefinitionParseResult ParseWithParser(string parserYaml) =>
        new GameDefinitionYamlParser().Parse(DefinitionYamlFixture.Mutate(ShippedParserLine, parserYaml));

    private static PlayerParserSpec ParseSpec(string parserYaml)
    {
        var result = ParseWithParser(parserYaml);
        result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).Should().BeEmpty();
        return result.Definition!.Control.Players!.Parsers["rcon.players"];
    }

    private static void AssertError(string parserYaml, string messageContains)
    {
        var result = ParseWithParser(parserYaml);
        result.Report.Issues.Should().Contain(
            i => i.Severity == ValidationSeverity.Error && i.Message.Contains(messageContains, StringComparison.Ordinal),
            $"the definition must be rejected at load time; issues were: {Escape(result)}");
    }

    private static string Escape(DefinitionParseResult result) =>
        string.Join(" | ", result.Report.Issues.Select(i => i.Message)).Replace("{", "{{").Replace("}", "}}");

    // -- back-compat ----------------------------------------------------------------------------------------

    [Fact]
    public void ShippedCsvParserDeclaration_StillParses_WithDefaultedNameColumnAndNoIdColumn()
    {
        var spec = ParseSpec(ShippedParserLine).Should().BeOfType<PlayerParserSpec.CsvWithHeader>().Subject;

        spec.Columns.Should().Equal("name", "playerUid", "steamId");
        spec.NameColumn.Should().Be("name", "an omitted 'nameColumn' defaults to the first declared column");
        spec.IdColumn.Should().BeNull();
    }

    [Fact]
    public void CsvParser_WithExplicitNameAndIdColumns_ParsesThem()
    {
        var spec = ParseSpec(
                "      rcon.players: { kind: csv-with-header, columns: [name, playerUid, steamId], "
                + "nameColumn: name, idColumn: steamId }")
            .Should().BeOfType<PlayerParserSpec.CsvWithHeader>().Subject;

        spec.NameColumn.Should().Be("name");
        spec.IdColumn.Should().Be("steamId");
    }

    [Fact]
    public void CsvParser_NamingAColumnThatWasNeverDeclared_IsError()
    {
        AssertError(
            "      rcon.players: { kind: csv-with-header, columns: [name, playerUid], nameColumn: nope }",
            "'nameColumn: nope'");
    }

    // -- the three new shapes -------------------------------------------------------------------------------

    [Fact]
    public void SummaryLineParser_Parses_AndDefaultsItsNameSeparator()
    {
        var spec = ParseSpec(
                "      rcon.players:\n"
                + "        kind: summary-line\n"
                + "        pattern: 'There are (?<count>\\d+) of a max(?: of)? (?<max>\\d+) players online:?(?<names>.*)'\n")
            .Should().BeOfType<PlayerParserSpec.SummaryLine>().Subject;

        spec.NameSeparator.Should().Be(", ");
        spec.Pattern.HasGroup("count").Should().BeTrue();
        spec.Pattern.HasGroup("max").Should().BeTrue();
    }

    [Fact]
    public void SummaryLineParser_WithoutACountGroup_IsError()
    {
        AssertError(
            "      rcon.players: { kind: summary-line, pattern: '(?<names>.*) are online' }",
            "declares no '(?<count>...)' named group");
    }

    [Fact]
    public void LinesParser_ParsesItsHeaderEntryAndIgnorePatterns()
    {
        var spec = ParseSpec(
                "      rcon.players:\n"
                + "        kind: lines\n"
                + "        headerPattern: '^Online players \\((?<count>\\d+)\\)'\n"
                + "        entryPattern: '^\\s*(?<name>\\S+)\\s*\\(online\\)\\s*$'\n"
                + "        ignorePatterns:\n"
                + "          - '^No one is connected'\n")
            .Should().BeOfType<PlayerParserSpec.Lines>().Subject;

        spec.HeaderPattern.Should().NotBeNull();
        spec.HeaderPattern!.HasGroup("count").Should().BeTrue();
        spec.EntryPattern.HasGroup("name").Should().BeTrue();
        spec.IgnorePatterns.Should().ContainSingle();
    }

    [Fact]
    public void LinesParser_WhoseEntryPatternHasNoNameGroup_IsError()
    {
        AssertError(
            "      rcon.players: { kind: lines, entryPattern: '^(?<id>\\d+)$' }",
            "declares no '(?<name>...)' named group");
    }

    [Fact]
    public void CountParser_WithAJsonPointer_Parses()
    {
        var spec = ParseSpec(
                "      rcon.players: { kind: count, jsonPointer: /data/serverGameState/numConnectedPlayers }")
            .Should().BeOfType<PlayerParserSpec.Count>().Subject;

        spec.JsonPointer.Should().Be("/data/serverGameState/numConnectedPlayers");
        spec.Pattern.Should().BeNull();
    }

    [Fact]
    public void CountParser_DeclaringBothAPatternAndAPointer_IsError()
    {
        AssertError(
            "      rcon.players: { kind: count, pattern: '(?<count>\\d+)', jsonPointer: /count }",
            "exactly one of 'pattern' or 'jsonPointer'");
    }

    [Fact]
    public void CountParser_DeclaringNeitherAPatternNorAPointer_IsError()
    {
        AssertError("      rcon.players: { kind: count }", "exactly one of 'pattern' or 'jsonPointer'");
    }

    [Fact]
    public void CountParser_WithARelativeJsonPointer_IsError()
    {
        AssertError(
            "      rcon.players: { kind: count, jsonPointer: 'data/count' }",
            "not an RFC 6901 pointer");
    }

    // -- pattern compilation is a load-time concern ---------------------------------------------------------

    [Fact]
    public void AParserPatternThatDoesNotCompile_IsAnErrorAgainstTheDefinition_NotARuntimeSurprise()
    {
        AssertError(
            "      rcon.players: { kind: summary-line, pattern: '(?<count>\\d+' }",
            "is not a valid non-backtracking regex");
    }

    /// <summary>
    /// The whole point of compiling under <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/>
    /// with no fallback: a definition file cannot express a pattern that backtracks, so it cannot express a
    /// ReDoS. A backreference is the cheapest construct the non-backtracking engine refuses outright.
    /// </summary>
    [Fact]
    public void AParserPatternRequiringABacktrackingEngine_IsRefusedRatherThanDowngraded()
    {
        AssertError(
            "      rcon.players: { kind: summary-line, pattern: '(?<count>\\d+)\\s+\\k<count>' }",
            "is not a valid non-backtracking regex");
    }

    [Fact]
    public void AnUnknownParserKind_IsError()
    {
        AssertError("      rcon.players: { kind: json-array, members: {} }", "declares 'kind: json-array'");
    }

    [Fact]
    public void AnUnknownKeyOnAParser_IsError()
    {
        AssertError(
            "      rcon.players: { kind: csv-with-header, columns: [name], separator: ';' }",
            "unrecognized field 'separator'");
    }
}
