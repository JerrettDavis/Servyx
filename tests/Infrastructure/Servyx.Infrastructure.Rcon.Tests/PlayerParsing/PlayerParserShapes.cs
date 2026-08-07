using Servyx.Domain.Definitions.Model;
using Servyx.Infrastructure.Rcon.Tests.Support;

namespace Servyx.Infrastructure.Rcon.Tests.PlayerParsing;

/// <summary>
/// One declared parser spec per reply shape under <c>PlayerParsing/Fixtures/</c>, keyed by the fixture
/// directory name.
/// </summary>
/// <remarks>
/// <para>
/// The shape names describe the FORMAT of a reply, never the title that emits it — a numbered line list and
/// a summary sentence are both formats several titles produce, and a source-scan test fails the build if a
/// game name reaches a file under <c>src/</c>. The patterns here are the ones a real definition file would
/// declare, spelled exactly as YAML would carry them, so a fixture failing is evidence about the parser and
/// not about this file's own transcription.
/// </para>
/// <para>
/// Every pattern is compiled through <see cref="CompiledPattern.TryCompile"/>, i.e. under
/// <see cref="System.Text.RegularExpressions.RegexOptions.NonBacktracking"/> with the production match
/// timeout — the same gate a definition file passes through. A shape added here that needs a backtracking
/// construct fails <see cref="Pattern"/> loudly rather than being quietly matched by a different engine
/// than production would use.
/// </para>
/// </remarks>
internal static class PlayerParserShapes
{
    public const string CsvWithHeader = "csv-with-header";
    public const string SummaryLine = "summary-line";
    public const string LinesNumbered = "lines-numbered";
    public const string LinesHeader = "lines-header";
    public const string CountPattern = "count-pattern";
    public const string CountJson = "count-json";

    public static IReadOnlyList<string> All { get; } =
        [CsvWithHeader, SummaryLine, LinesNumbered, LinesHeader, CountPattern, CountJson];

    public static PlayerParserSpec For(string shape) => shape switch
    {
        CsvWithHeader => new PlayerParserSpec.CsvWithHeader(["name", "playerUid", "steamId"], "name", null),

        SummaryLine => new PlayerParserSpec.SummaryLine(
            Pattern(@"There are (?<count>\d+) of a max(?: of)? (?<max>\d+) players online:?(?<names>.*)"),
            PlayerParserSpec.SummaryLine.DefaultNameSeparator),

        LinesNumbered => new PlayerParserSpec.Lines(
            HeaderPattern: null,
            EntryPattern: Pattern(@"^\s*\d+\.\s*(?<name>[^,]+),\s*(?<id>\S+)\s*$"),
            IgnorePatterns: [Pattern("^No Players Connected")]),

        LinesHeader => new PlayerParserSpec.Lines(
            HeaderPattern: Pattern(@"^Online players \((?<count>\d+)\)"),
            EntryPattern: Pattern(@"^\s*(?<name>\S+)\s*\(online\)\s*$"),
            IgnorePatterns: []),

        CountPattern => new PlayerParserSpec.Count(Pattern(@"Players online: (?<count>\d+)"), null),

        CountJson => new PlayerParserSpec.Count(null, "/data/serverGameState/numConnectedPlayers"),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No parser spec is declared for this fixture shape."),
    };

    /// <summary>Reads a fixture reply from the repo, exactly as a control channel would have returned it.</summary>
    public static string Fixture(string shape, string fileName)
    {
        var path = Path.Combine(
            RepoRootLocator.Find().FullName,
            "tests", "Infrastructure", "Servyx.Infrastructure.Rcon.Tests", "PlayerParsing", "Fixtures",
            shape,
            fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing player-list fixture '{shape}/{fileName}'.", path);
        }

        return File.ReadAllText(path);
    }

    private static CompiledPattern Pattern(string source) =>
        CompiledPattern.TryCompile(source, out var error)
        ?? throw new InvalidOperationException($"Test pattern '{source}' is not a valid non-backtracking regex: {error}");
}
