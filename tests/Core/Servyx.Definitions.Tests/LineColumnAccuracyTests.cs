using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// The main justification for parsing over <c>YamlStream</c>/<c>YamlMappingNode</c> rather than
/// <c>Deserializer.Deserialize&lt;T&gt;()</c> is that <see cref="ValidationIssue.Line"/>/<see cref="ValidationIssue.Column"/>
/// point at the actual offending node. These tests prove it, computing the expected position independently
/// (see <see cref="LineColumnCalculator"/>) rather than asserting a number copied from the parser's own
/// output.
/// </summary>
/// <remarks>
/// Every fixture here is normalized to LF-only before parsing, so the expected-position calculation does
/// not need to replicate any particular CRLF-counting convention — see the remarks on
/// <see cref="LineColumnCalculator"/>.
/// </remarks>
public class LineColumnAccuracyTests
{
    private static string Normalize(string yaml) => yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void UnsupportedApiVersion_PointsAtTheApiVersionValue()
    {
        var yaml = Normalize(DefinitionYamlFixture.Mutate("apiVersion: servyx.dev/v1", "apiVersion: servyx.dev/v2"));
        var (expectedLine, expectedColumn) = LineColumnCalculator.Locate(yaml, "servyx.dev/v2");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("apiVersion")).Subject;
        issue.Line.Should().Be(expectedLine);
        issue.Column.Should().Be(expectedColumn);
    }

    [Fact]
    public void UnsupportedKind_PointsAtTheKindValue()
    {
        var yaml = Normalize(DefinitionYamlFixture.Mutate("kind: GameDefinition", "kind: BogusKind"));
        var (expectedLine, expectedColumn) = LineColumnCalculator.Locate(yaml, "BogusKind");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("kind")).Subject;
        issue.Line.Should().Be(expectedLine);
        issue.Column.Should().Be(expectedColumn);
    }

    [Fact]
    public void MalformedBoolean_PointsAtTheOffendingScalar()
    {
        var yaml = Normalize(DefinitionYamlFixture.Mutate("hostNetwork: false", "hostNetwork: notabool"));
        var (expectedLine, expectedColumn) = LineColumnCalculator.Locate(yaml, "notabool");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("hostNetwork")).Subject;
        issue.Line.Should().Be(expectedLine);
        issue.Column.Should().Be(expectedColumn);
    }

    [Fact]
    public void UnrecognizedFlowMappingValue_PointsAtTheValueInsideTheFlowMapping()
    {
        var yaml = Normalize(DefinitionYamlFixture.Mutate(
            "{ port: 8211,  protocol: udp, purpose: game,  var: PORT,       published: true }",
            "{ port: 8211,  protocol: xdp, purpose: game,  var: PORT,       published: true }"));
        var (expectedLine, expectedColumn) = LineColumnCalculator.Locate(yaml, "xdp");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("protocol: xdp")).Subject;
        issue.Line.Should().Be(expectedLine);
        issue.Column.Should().Be(expectedColumn);
    }

    [Fact]
    public void UnknownTopLevelSection_PointsAtTheOffendingKey()
    {
        var withBogusSection = Normalize(DefinitionYamlFixture.RealYaml)
            .Replace("kind: GameDefinition\n", "kind: GameDefinition\nbogusSection: 1\n", StringComparison.Ordinal);
        var (expectedLine, expectedColumn) = LineColumnCalculator.Locate(withBogusSection, "bogusSection");

        var result = new GameDefinitionYamlParser().Parse(withBogusSection);

        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("bogusSection")).Subject;
        issue.Line.Should().Be(expectedLine);
        issue.Column.Should().Be(expectedColumn);
    }
}
