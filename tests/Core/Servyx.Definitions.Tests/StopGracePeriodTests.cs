using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Lifecycle;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Covers the two stop-ladder fields a slow, save-critical shutdown depends on:
/// <c>deployments[].stopGracePeriodSeconds</c> (how long the container runtime itself waits before
/// force-killing) and <c>lifecycle.stop[].continueOnError</c> (whether a failed stage escalates or aborts).
/// </summary>
/// <remarks>
/// Every fixture is a targeted mutation of the real <c>definitions/palworld-docker.yaml</c>, on the same
/// principle as <see cref="SemanticRuleTests"/>: everything except the field under test is exactly what
/// ships, so a test cannot pass against a miniature document that the real schema would reject.
/// </remarks>
public class StopGracePeriodTests
{
    // The real definition's ladder: control 45s -> control 15s -> signal 30s -> kill (which declares none).
    private const int RealLadderTotalSeconds = 90;

    private const string ShutdownStage =
        "- { kind: control, channel: rcon, command: shutdown, args: { seconds: 30, message: \"Server shutting down\" }, timeout: 45s }";

    private const string DoExitStage = "- { kind: control, channel: rcon, command: doexit, timeout: 15s }";

    private const string SigIntStage = "- { kind: signal, signal: SIGINT, timeout: 30s }";

    private const string StopTimeoutLine = "    stopTimeout: 60s";

    /// <summary>
    /// The line separator the checked-out fixture actually uses, so a mutation that inserts a new YAML line
    /// does not depend on this repo's checkout being CRLF or LF.
    /// </summary>
    private static readonly string Newline = DefinitionYamlFixture.RealYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// Applies several targeted substitutions in order, asserting each one matched — the multi-mutation
    /// counterpart of <see cref="DefinitionYamlFixture.Mutate"/>, needed because reshaping a whole stop
    /// ladder means touching more than one line.
    /// </summary>
    private static string Mutate(params (string Find, string Replace)[] mutations)
    {
        var yaml = DefinitionYamlFixture.RealYaml;
        foreach (var (find, replace) in mutations)
        {
            if (!yaml.Contains(find, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Fixture mutation target '{find}' was not found in the definition text.");
            }

            yaml = yaml.Replace(find, replace, StringComparison.Ordinal);
        }

        return yaml;
    }

    private static string WithGracePeriod(string value, params (string Find, string Replace)[] extra) =>
        Mutate([(StopTimeoutLine, $"{StopTimeoutLine}{Newline}    stopGracePeriodSeconds: {value}"), .. extra]);

    private static IReadOnlyList<ValidationIssue> ErrorsIn(string yaml) =>
        new GameDefinitionYamlParser().Parse(yaml).Report.Issues
            .Where(i => i.Severity == ValidationSeverity.Error)
            .ToList();

    // ---------------------------------------------------------------------------------------------
    // A ladder with no control stage at all
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A game that handles its own termination signal (saving, then exiting) needs no control-channel stage
    /// in its ladder at all — signal-then-kill is the whole plan. The cross-reference validation in
    /// <c>GameDefinitionYamlParser.Semantics.cs</c> only checks command references that a control stage
    /// actually made, so a ladder that makes none must be entirely valid rather than "suspiciously empty".
    /// </summary>
    [Fact]
    public void StopLadder_WithNoControlStage_ParsesWithNoErrors()
    {
        var yaml = Mutate(
            (ShutdownStage, "- { kind: signal, signal: SIGTERM, timeout: 300s }"),
            (DoExitStage, string.Empty));

        var result = new GameDefinitionYamlParser().Parse(yaml);

        ErrorsIn(yaml).Should().BeEmpty();
        result.Definition.Should().NotBeNull();
        result.Definition!.Lifecycle.Stop.Stages.Should().NotContain(s => s is StopStage.Rcon);
        result.Definition.Lifecycle.Stop.Stages[^1].Should().BeOfType<StopStage.Kill>();
    }

    // ---------------------------------------------------------------------------------------------
    // stopGracePeriodSeconds
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void StopGracePeriod_NotDeclared_IsNullAndNotAnError()
    {
        var result = new GameDefinitionYamlParser().Parse(DefinitionYamlFixture.RealYaml);

        result.Definition.Should().NotBeNull();
        result.Definition!.Deployments.Should().OnlyContain(d => d.StopGracePeriod == null);
        result.Report.Issues.Should().NotContain(i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void StopGracePeriod_CoveringTheWholeLadder_IsParsedAndAccepted()
    {
        var yaml = WithGracePeriod("240");

        ErrorsIn(yaml).Should().BeEmpty();

        var definition = new GameDefinitionYamlParser().Parse(yaml).Definition;
        definition.Should().NotBeNull();
        definition!.Deployments[0].StopGracePeriod.Should().Be(TimeSpan.FromSeconds(240));
    }

    /// <summary>Exactly equal to the ladder total is enough — the rule is "at least", not "strictly more".</summary>
    [Fact]
    public void StopGracePeriod_ExactlyTheLadderTotal_IsAccepted()
    {
        ErrorsIn(WithGracePeriod(RealLadderTotalSeconds.ToString())).Should().BeEmpty();
    }

    /// <summary>
    /// The whole point of the field: a grace period shorter than the ladder means the runtime force-kills
    /// part-way through it, so the ladder's later stages never run and the save is truncated. Both numbers
    /// have to appear in the message, because the fix is arithmetic the author cannot do without them.
    /// </summary>
    [Fact]
    public void StopGracePeriod_ShorterThanTheLadderTotal_IsErrorNamingBothNumbers()
    {
        var errors = ErrorsIn(WithGracePeriod("30"));

        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("stopGracePeriodSeconds: 30");
        errors[0].Message.Should().Contain($"total {RealLadderTotalSeconds} seconds");
        errors[0].Message.Should().Contain("docker-thijsvanloef");
    }

    /// <summary>
    /// The comparison is against the ladder as declared, not a constant: lengthening a stage's timeout must
    /// be enough on its own to turn a previously-sufficient grace period into an error.
    /// </summary>
    [Fact]
    public void StopGracePeriod_MeasuredAgainstTheDeclaredLadder_NotAFixedBudget()
    {
        var yaml = WithGracePeriod("120", (SigIntStage, "- { kind: signal, signal: SIGTERM, timeout: 300s }"));

        var errors = ErrorsIn(yaml);

        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("total 360 seconds");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void StopGracePeriod_ZeroOrNegative_IsError(string value)
    {
        var errors = ErrorsIn(WithGracePeriod(value));

        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("positive whole number of seconds");
    }

    [Fact]
    public void StopGracePeriod_NotAWholeNumber_IsError()
    {
        ErrorsIn(WithGracePeriod("\"240s\"")).Should().ContainSingle()
            .Which.Message.Should().Contain("whole number");
    }

    // ---------------------------------------------------------------------------------------------
    // continueOnError
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Defaults are asymmetric on purpose (see <see cref="StopStage.ContinueOnError"/>): an unreachable
    /// control channel must never wedge a stop, while a container runtime refusing a signal is a real fault.
    /// </summary>
    [Fact]
    public void ContinueOnError_DefaultsToTrueForControlStages_AndFalseForSignalStages()
    {
        var stages = new GameDefinitionYamlParser().Parse(DefinitionYamlFixture.RealYaml).Definition!.Lifecycle.Stop.Stages;

        stages.OfType<StopStage.Rcon>().Should().NotBeEmpty().And.OnlyContain(s => s.ContinueOnError);
        stages.OfType<StopStage.Signal>().Should().NotBeEmpty().And.OnlyContain(s => !s.ContinueOnError);
    }

    [Fact]
    public void ContinueOnError_DeclaredExplicitly_OverridesTheDefaultOnEitherKindOfStage()
    {
        var yaml = Mutate(
            (DoExitStage, "- { kind: control, channel: rcon, command: doexit, timeout: 15s, continueOnError: false }"),
            (SigIntStage, "- { kind: signal, signal: SIGINT, timeout: 30s, continueOnError: true }"));

        ErrorsIn(yaml).Should().BeEmpty();

        var stages = new GameDefinitionYamlParser().Parse(yaml).Definition!.Lifecycle.Stop.Stages;

        stages.OfType<StopStage.Rcon>().Should().Contain(s => s.CommandId == "doexit" && !s.ContinueOnError);
        stages.OfType<StopStage.Signal>().Should().OnlyContain(s => s.ContinueOnError);
    }

    /// <summary>
    /// <c>kill</c> is terminal — there is no stage after it to escalate to — so the field is meaningless
    /// there and, per the parser's reject-unknown-keys rule, declaring it is an error rather than a no-op.
    /// </summary>
    [Fact]
    public void ContinueOnError_OnTheTerminalKillStage_IsAnUnrecognizedField()
    {
        var errors = ErrorsIn(Mutate(("- { kind: kill }", "- { kind: kill, continueOnError: true }")));

        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("continueOnError");
    }
}
