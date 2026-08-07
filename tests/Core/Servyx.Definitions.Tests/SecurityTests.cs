using System.Diagnostics;
using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// The parser's whole defense against a ReDoS'd definition file is that it never runs a definition-authored
/// regex against any input at all — it only compiles the pattern under <c>RegexOptions.NonBacktracking</c>
/// to validate it (see the remarks on <c>ValidateSafeRegex</c> in <c>GameDefinitionYamlParser.Support.cs</c>).
/// These tests prove both halves of that: a classically catastrophic pattern completes fast, and a pattern
/// using a construct NonBacktracking cannot represent (a backreference) is rejected as malformed rather than
/// silently accepted.
/// </summary>
public class SecurityTests
{
    [Fact]
    public void CatastrophicBacktrackingPattern_InWorldIdPattern_DoesNotHang()
    {
        // The textbook catastrophic-backtracking shape for a normal backtracking engine. Under
        // RegexOptions.NonBacktracking this is a perfectly representable, linear-time pattern — compiling it
        // (which is all this parser ever does with a definition-authored regex) is fast regardless.
        var yaml = DefinitionYamlFixture.Mutate(
            "worldIdPattern: \"^[0-9A-F]{32}$\"",
            "worldIdPattern: \"^(a+)+(a+)+(a+)+$\"");

        var stopwatch = Stopwatch.StartNew();
        var act = () => new GameDefinitionYamlParser().Parse(yaml);
        var result = act.Should().NotThrow().Subject;
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result.Report.Should().NotBeNull();
    }

    [Fact]
    public void BackreferencePattern_InWorldIdPattern_IsRejectedAsMalformed_NeverEvaluated()
    {
        // Backreferences are not representable by RegexOptions.NonBacktracking's automaton, so this fails to
        // compile — the parser reports it as a malformed pattern rather than falling back to a backtracking
        // engine that could be forced into exponential time by an adversarial world folder name.
        var yaml = DefinitionYamlFixture.Mutate(
            "worldIdPattern: \"^[0-9A-F]{32}$\"",
            "worldIdPattern: '^(?<g>[0-9A-F]{16})\\1$'");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("not a valid non-backtracking regex", StringComparison.Ordinal));
    }

    [Fact]
    public void EnabledWhen_CompoundExpression_IsRejectedNotEvaluated()
    {
        var yaml = DefinitionYamlFixture.Mutate(
            "enabledWhen: \"env.RCON_ENABLED == 'true'\"",
            "enabledWhen: \"env.RCON_ENABLED == 'true' || env.FORCE == 'true'\"");

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("only supported shape", StringComparison.Ordinal));
    }
}
