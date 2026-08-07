using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Every input here is the kind of thing an untrusted definition file could be — truncated, empty,
/// non-UTF8, huge, pathologically nested, or full of duplicate keys — and every one of them must produce a
/// <see cref="ValidationReport"/>, never an unhandled exception. See the class remarks on
/// <see cref="GameDefinitionYamlParser"/> for why nothing under this project throws for a content problem.
/// </summary>
public class RobustnessTests
{
    [Fact]
    public void EmptyInput_ProducesAnErrorReport_DoesNotThrow()
    {
        var act = () => new GameDefinitionYamlParser().Parse(string.Empty);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void WhitespaceOnlyInput_ProducesAnErrorReport_DoesNotThrow()
    {
        var act = () => new GameDefinitionYamlParser().Parse("   \n\t\n   ");

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error);
    }

    [Theory]
    [InlineData("apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated")]
    [InlineData("apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata: {id: palworld, ")]
    [InlineData("apiVersion: \"unterminated string")]
    public void TruncatedYaml_ProducesAnErrorReport_DoesNotThrow(string truncated)
    {
        var act = () => new GameDefinitionYamlParser().Parse(truncated);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void NonMappingRoot_ProducesAnErrorReport_DoesNotThrow()
    {
        var act = () => new GameDefinitionYamlParser().Parse("- just\n- a\n- list\n");

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void NonUtf8Bytes_ProducesAnErrorReport_DoesNotThrow()
    {
        // Invalid UTF-8 continuation bytes with no leading byte — never a valid UTF-8 sequence. Decoded via
        // UTF8Encoding(throwOnInvalidBytes: false), which substitutes U+FFFD replacement characters rather
        // than throwing, so the bytes reach the parser as text that is not valid YAML — pinned here rather
        // than asserted only for "some report came back".
        byte[] invalidUtf8 = [0x80, 0x81, 0x82, 0xFF, 0xFE, 0x00, 0x01];

        var act = () => new GameDefinitionYamlParser().Parse(invalidUtf8);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.IsValid.Should().BeFalse();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("not valid YAML", StringComparison.Ordinal));
    }

    [Fact]
    public void TenMegabyteFile_ParsesWithoutThrowing_WithinAReasonableTime()
    {
        // A real definition plus a single ~10MB comment tail — large but not adversarial, exercising the
        // "large file" robustness requirement independent of the "pathological structure" one below.
        var yaml = DefinitionYamlFixture.RealYaml + "\n# " + new string('x', 10 * 1024 * 1024) + "\n";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => new GameDefinitionYamlParser().Parse(yaml);
        var result = act.Should().NotThrow().Subject;
        stopwatch.Stop();

        result.Definition.Should().NotBeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DuplicateTopLevelKeys_ProducesAnErrorReport_DoesNotThrow()
    {
        // Pinned real behavior: YamlDotNet's composer itself rejects a duplicate mapping key while building
        // the node tree (a YamlException, "Duplicate key ..."), which the outer catch in Parse converts to a
        // ValidationIssue. This is neither "first wins" nor "last wins" — it is a parse failure, and no
        // GameDefinition is ever produced. If a future YamlDotNet version changes this to a
        // silent-overwrite policy, this test will start failing and should be revisited rather than relaxed
        // back to a vacuous assertion.
        var yaml = "apiVersion: servyx.dev/v1\napiVersion: servyx.dev/v2\nkind: GameDefinition\n";

        var act = () => new GameDefinitionYamlParser().Parse(yaml);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error
            && i.Message.Contains("not valid YAML", StringComparison.Ordinal)
            && i.Message.Contains("Duplicate key", StringComparison.Ordinal));
    }

    // -- Structural nesting depth: the fix for a real, reproduced stack-overflow crash --------------------------
    //
    // A YamlDotNet YamlStream.Load over deeply nested flow collections ([[[[...]]]]), chained block-sequence
    // dashes on one line (- - - - -), OR plain indentation-based block nesting (mapping-in-mapping,
    // sequence-in-sequence, one level per line) recurses proportionally to nesting depth in its own scanner
    // and can overflow the process stack — confirmed empirically against this project's compiled library:
    // fine at depth 1000/3000, crashes the whole test process at 5000/10000, for every one of those three
    // shapes. StackOverflowException has been uncatchable in .NET since 2.0 SP1, so no try/catch inside Parse
    // can defend against it; SafeYamlLoader.TryLoad is a cheap pre-scan of the raw text, run before anything
    // reaches YamlStream.Load, that rejects this shape outright, counting indentation depth, flow-bracket
    // depth, and same-line chained-dash depth as one combined total. The flow-syntax and chained-dash tests
    // use those shapes specifically because they cost ~1-2 bytes per nesting level — the same cheap-per-byte
    // property that makes them a real remote-DoS vector for a "drop in a YAML file" component; the
    // block-sequence and block-mapping tests below cover the indentation-based shape, which is how
    // essentially all real YAML expresses depth and was initially under-exercised by this suite (the original
    // version of this test used indentation at only depth 500, which costs O(depth²) bytes and never reached
    // the real danger zone).

    [Fact]
    public void FlowNesting_AtTheMaximumSupportedDepth_IsAccepted()
    {
        // "deepField: [[[...]]]" is itself one indentation level (the top-level "deepField:" line), which now
        // counts toward the same combined total as the flow brackets — so the flow run tops out at 99, not
        // 100, to land exactly on the 100 combined-depth ceiling.
        var yaml = BuildNestedFlowYaml(depth: 99);

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Report.Issues.Should().NotContain(i => i.Message.Contains("exceeds the maximum supported depth", StringComparison.Ordinal));
    }

    [Fact]
    public void FlowNesting_OneLevelBeyondTheMaximumSupportedDepth_IsRejectedWithLineAndColumn()
    {
        var yaml = BuildNestedFlowYaml(depth: 100);

        var result = new GameDefinitionYamlParser().Parse(yaml);

        result.Definition.Should().BeNull();
        var issue = result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal)).Subject;
        issue.Line.Should().Be(3);
        issue.Column.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FlowNesting_AtTenThousandLevelsDeep_ProducesACleanReport_InsteadOfCrashingTheProcess()
    {
        // This is the reviewer's exact reproduction: depth 10000 of nested '[' previously crashed the host
        // process with an uncatchable StackOverflowException before YamlStream.Load ever returned.
        var yaml = BuildNestedFlowYaml(depth: 10_000);

        var act = () => new GameDefinitionYamlParser().Parse(yaml);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Should().NotBeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void ChainedBlockSequenceDashes_AtTenThousandLevelsDeep_ProducesACleanReport_InsteadOfCrashingTheProcess()
    {
        // The other cheap-per-byte nesting construct the pre-scan defends against: "- - - - ..." chained on
        // one line, ~2 bytes per level, with no closing token needed.
        var yaml = "apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField:\n  " + string.Concat(Enumerable.Repeat("- ", 10_000)) + "value\n";

        var act = () => new GameDefinitionYamlParser().Parse(yaml);

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockSequenceIndentation_AtTenThousandLevelsDeep_ProducesACleanReport_InsteadOfCrashingTheProcess()
    {
        // The reviewer's exact reproduction of the second gap: one bare "- " per line, each line 2 spaces
        // more indented than the last, with no flow brackets and no same-line chained dashes at all. Every
        // level costs a whole new line (this IS the O(depth²)-bytes shape), but it is how a real, large
        // definition's own block sequences are actually written, and it recurses in YamlDotNet's scanner
        // exactly as flow nesting does — indentation-based depth is the vector the first fix pass missed.
        var builder = new System.Text.StringBuilder("apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField:\n");
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(' ', i * 2).Append("- \n");
        }

        var act = () => new GameDefinitionYamlParser().Parse(builder.ToString());

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void BlockMappingIndentation_AtTenThousandLevelsDeep_ProducesACleanReport_InsteadOfCrashingTheProcess()
    {
        // The reviewer's other indentation-based reproduction: increasingly-indented mapping keys (k0: / k1:
        // / k2: / ...), no dashes anywhere. Proves the depth counter is driven by indentation itself, not by
        // block-sequence dashes specifically — a mapping-only document is just as dangerous.
        var builder = new System.Text.StringBuilder("apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField:\n");
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(' ', i * 2 + 2).Append('k').Append(i).Append(":\n");
        }

        var act = () => new GameDefinitionYamlParser().Parse(builder.ToString());

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedBlockAndFlowNesting_CombinesBothTowardTheSameLimit_ProducesACleanReport()
    {
        // Indentation depth and flow-bracket depth must be added together, not tracked as two independent
        // maxima: 40 levels of block-mapping indentation (well under the limit on its own) plus a flow
        // sequence nested deep enough that indentation-depth + flow-depth together exceed 100, even though
        // neither alone would.
        var builder = new System.Text.StringBuilder("apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField:\n");
        for (var i = 0; i < 40; i++)
        {
            builder.Append(' ', i * 2 + 2).Append('k').Append(i).Append(":\n");
        }

        builder.Append(' ', 40 * 2 + 2).Append("leaf: ").Append('[', 90).Append(']', 90).Append('\n');

        var act = () => new GameDefinitionYamlParser().Parse(builder.ToString());

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void MergeKeyFollowedByDeepBlockSequence_IsStillCaughtByTheDepthGuard()
    {
        // A merge key (<<: *anchor) does not itself add nesting, but must not create a blind spot for the
        // deeply-indented content that follows it in the same document.
        var builder = new System.Text.StringBuilder(
            "apiVersion: servyx.dev/v1\nkind: GameDefinition\nbase: &base { a: 1 }\ndeepField:\n  <<: *base\n");
        for (var i = 0; i < 5_000; i++)
        {
            builder.Append(' ', i * 2 + 2).Append("- \n");
        }

        var act = () => new GameDefinitionYamlParser().Parse(builder.ToString());

        var result = act.Should().NotThrow().Subject;
        result.Definition.Should().BeNull();
        result.Report.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error && i.Message.Contains("exceeds the maximum supported depth of 100", StringComparison.Ordinal));
    }

    [Fact]
    public void AnchorAliasChain_DoesNotHangOrCrash_EvenThoughDepthGuardDoesNotCoverIt()
    {
        // A "billion laughs"-shaped chain (each anchor's flow sequence aliases the previous one twice) is a
        // structurally different attack — exponential blowup from ALIAS EXPANSION during composition, not
        // from scanner recursion over textual nesting — and this project's structural-depth pre-scan does not
        // (and structurally cannot, since the raw text itself is small and shallow) defend against it. This
        // test is a probe, not a guarantee: it documents that YamlDotNet's representation-model composer does
        // not appear to eagerly materialize the full exponential expansion at this chain length, so no crash
        // or hang is observed — see this phase's report for why this is flagged as a separate, unaddressed
        // risk category rather than claimed as fixed.
        var builder = new System.Text.StringBuilder("apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField: &a0 [x]\n");
        for (var i = 1; i < 60; i++)
        {
            builder.Append("field").Append(i).Append(": &a").Append(i).Append(" [*a").Append(i - 1).Append(", *a").Append(i - 1).Append("]\n");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var act = () => new GameDefinitionYamlParser().Parse(builder.ToString());
        var result = act.Should().NotThrow().Subject;
        stopwatch.Stop();

        result.Report.Should().NotBeNull();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TagDirectiveAndCustomTag_DoNotBypassOrBreakParsing()
    {
        var yaml = "%TAG !e! tag:example.com,2000:\n---\napiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField: !e!thing foo\n";

        var act = () => new GameDefinitionYamlParser().Parse(yaml);

        var result = act.Should().NotThrow().Subject;
        result.Report.Should().NotBeNull();
        // "deepField" is still an unrecognized top-level key regardless of the tag on its value — proves the
        // tag directive/custom-tag syntax was parsed, not silently swallowed or mishandled by the pre-scan.
        result.Report.Issues.Should().Contain(i => i.Message.Contains("deepField", StringComparison.Ordinal));
    }

    private static string BuildNestedFlowYaml(int depth) =>
        "apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField: " + new string('[', depth) + new string(']', depth) + "\n";
}
