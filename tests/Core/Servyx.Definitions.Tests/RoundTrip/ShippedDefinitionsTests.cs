using System.Text.RegularExpressions;
using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests.RoundTrip;

/// <summary>
/// Directory-wide coverage for every real, shipped definition under the repo-root <c>definitions/</c>
/// directory. Unlike <see cref="PalworldDefinitionRoundTripTests"/> and
/// <see cref="MinecraftDefinitionRoundTripTests"/>, which each name a single file explicitly, the theories
/// here enumerate the directory at run time via <see cref="RepoRootLocator"/>, so a newly added definition
/// (ARK ASA, Factorio, Satisfactory, ...) is covered automatically with no test edits.
/// </summary>
public class ShippedDefinitionsTests
{
    // Lowercase kebab-case stem, .yaml extension: e.g. "ark-survival-ascended.yaml", not "ArkASA.YAML" or
    // "ark_survival_ascended.yaml".
    private static readonly Regex KebabCaseYamlFileName = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*\.yaml$", RegexOptions.Compiled);

    public static IEnumerable<object[]> DefinitionFiles() =>
        DefinitionFilePaths().Select(path => new object[] { path });

    private static IReadOnlyList<string> DefinitionFilePaths()
    {
        var repoRoot = RepoRootLocator.Find();
        var definitionsDir = Path.Combine(repoRoot.FullName, "definitions");

        return Directory.Exists(definitionsDir)
            ? Directory.GetFiles(definitionsDir, "*.yaml", SearchOption.TopDirectoryOnly)
            : [];
    }

    /// <summary>
    /// Guards against <see cref="DefinitionFiles"/> silently enumerating zero cases — a broken
    /// <see cref="RepoRootLocator"/> or a directory rename would otherwise make every theory below pass
    /// vacuously instead of failing loudly.
    /// </summary>
    [Fact]
    public void DefinitionsDirectory_IsNotEmpty()
    {
        DefinitionFilePaths().Should().NotBeEmpty(
            "the repo-root definitions/ directory must contain at least one shipped game definition");
    }

    [Theory]
    [MemberData(nameof(DefinitionFiles))]
    public void ShippedDefinition_ParsesWithNoErrors(string path)
    {
        var fileName = Path.GetFileName(path);
        var result = new GameDefinitionYamlParser().Parse(File.ReadAllText(path));
        var errors = result.Report.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

        var failureMessage = errors.Count == 0
            ? string.Empty
            : $"'{fileName}' produced {errors.Count} error(s):\n" + string.Join(
                "\n", errors.Select(e => $"  line {e.Line}, col {e.Column}: {Escape(e.Message)}"));

        errors.Should().BeEmpty(failureMessage);
    }

    /// <summary>
    /// <c>metadata.id</c> is the primary key every server, backup, and control-channel lookup keys off of
    /// (see <see cref="GameDefinitionCatalogTests"/>) — a collision between two shipped definitions would be
    /// silently resolved by "whichever the catalogue happened to load last", which must never ship.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefinitionFiles))]
    public void ShippedDefinition_MetadataIdIsUnique_AndFileNameIsLowercaseKebabCaseYaml(string path)
    {
        var fileName = Path.GetFileName(path);

        KebabCaseYamlFileName.IsMatch(fileName).Should().BeTrue(
            $"'{fileName}' must be a lowercase-kebab-case filename with a .yaml extension");

        var definition = new GameDefinitionYamlParser().Parse(File.ReadAllText(path)).Definition;
        definition.Should().NotBeNull($"'{fileName}' must parse successfully to check its metadata.id");

        var id = definition!.Metadata.Id;
        id.Should().NotBeNullOrWhiteSpace($"'{fileName}' must declare a non-empty metadata.id");

        var collidingFiles = DefinitionFilePaths()
            .Where(other => other != path)
            .Select(other => (FileName: Path.GetFileName(other),
                Id: new GameDefinitionYamlParser().Parse(File.ReadAllText(other)).Definition?.Metadata.Id))
            .Where(other => other.Id == id)
            .Select(other => other.FileName)
            .ToList();

        collidingFiles.Should().BeEmpty(
            $"metadata.id '{id}' declared by '{fileName}' must be unique across definitions/, "
            + $"but is also declared by: {string.Join(", ", collidingFiles)}");
    }

    // AwesomeAssertions' "because" reason is run through a formatter that treats '{'/'}' as format
    // placeholders; validation messages can legitimately contain "${VAR}" (see the real definitions'
    // network capability vars), so brace characters are escaped before being embedded in a failure message.
    private static string Escape(string message) => message.Replace("{", "{{").Replace("}", "}}");
}
