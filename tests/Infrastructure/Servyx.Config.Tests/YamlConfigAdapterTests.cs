using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

/// <summary>
/// Tests for <see cref="YamlConfigAdapter"/> — the adapter that makes <c>SurfaceFormat.Yaml</c> readable and
/// writable at runtime, and therefore the first one that can touch a Docker Compose file. Fixtures live in
/// <c>Fixtures/</c> rather than inline (unlike <see cref="JsonConfigAdapterTests"/>) because YAML's
/// significant indentation makes a realistic document too long to read comfortably beside an assertion, and
/// because the byte-level cases — CRLF without a trailing newline, a leading BOM — are clearer as real bytes
/// on disk than as escapes in a string literal.
/// </summary>
public class YamlConfigAdapterTests
{
    private static ConfigDocument ParsePalworld(out string original)
    {
        original = FixturePaths.Read("compose-palworld.yaml");
        return new YamlConfigAdapter().Parse(original);
    }

    private static IReadOnlyDictionary<string, YamlScalarValue> ValuesOf(ConfigDocument document) =>
        ((YamlConfigDocument)document.Root).Values;

    [Fact]
    public void FormatId_IsYaml()
    {
        new YamlConfigAdapter().FormatId.Should().Be("yaml");
    }

    /// <summary>
    /// Unlike <see cref="JsonConfigAdapter"/>, this adapter claims comment preservation — YAML has comment
    /// syntax, and the splice-only write model keeps every one of them.
    /// </summary>
    [Fact]
    public void PreservesComments_IsTrue()
    {
        new YamlConfigAdapter().PreservesComments.Should().BeTrue();
    }

    [Fact]
    public void Parse_NestedScalars_AreAddressedByRfc6901Pointers()
    {
        var values = ValuesOf(ParsePalworld(out _));

        values["/services/palworld/image"].Text.Should().Be("thijsvanloef/palworld-server-docker:latest");
        values["/services/palworld/restart"].Text.Should().Be("unless-stopped");
        values["/services/palworld/environment/PLAYERS"].Text.Should().Be("16");
        values["/services/palworld/healthcheck/test/0"].Text.Should().Be("CMD");
    }

    [Fact]
    public void Parse_SequenceElements_AreAddressedByZeroBasedIndex()
    {
        var values = ValuesOf(ParsePalworld(out _));

        values["/services/palworld/ports/0"].Text.Should().Be("8211:8211/udp");
        values["/services/palworld/ports/1"].Text.Should().Be("27015:27015/udp");
        values["/services/palworld/ports/2"].Text.Should().Be("8212:8212/tcp");
        values["/services/palworld/volumes/0"].Text.Should().Be("./data:/palworld/");
    }

    [Fact]
    public void Parse_Scalars_CarryTheirSourceQuotingStyle()
    {
        var values = ValuesOf(ParsePalworld(out _));

        values["/services/palworld/ports/0"].Style.Should().Be(YamlScalarStyle.Plain);
        values["/services/palworld/ports/1"].Style.Should().Be(YamlScalarStyle.DoubleQuoted);
        values["/services/palworld/ports/2"].Style.Should().Be(YamlScalarStyle.SingleQuoted);
    }

    [Fact]
    public void Parse_KeysContainingSlashOrTilde_AreEscapedPerRfc6901()
    {
        var values = ValuesOf(new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-quotes-and-styles.yaml")));

        values.Keys.Should().Contain("/nested/a~1b");
        values.Keys.Should().Contain("/nested/c~0d");
    }

    [Fact]
    public void Parse_QuotedScalar_DecodesTextButKeepsRawSourceIncludingItsQuotes()
    {
        var values = ValuesOf(new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-quotes-and-styles.yaml")));

        values["/double_with_escape"].Text.Should().Be("line1\nline2 \u00e9");
        values["/double_with_escape"].Raw.Should().Be("\"line1\\nline2 \u00e9\"");
        values["/single_with_escape"].Text.Should().Be("it's quoted");
        values["/single_with_escape"].Raw.Should().Be("'it''s quoted'");
    }

    // ---------------------------------------------------------------------------------------------------
    // Quote-boundary normalization. YamlDotNet reports a quoted scalar's extent INCLUDING its quotes; the
    // ConfigSpan contract is content-only with the quote recorded in QuoteStyle. Getting this wrong writes
    // over the quotes and silently changes what the file means.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The quote-boundary invariant, asserted across the whole corpus rather than at one call site: for every
    /// span the adapter registers, a quoted value must have its quote character immediately <i>outside</i>
    /// both ends of the span and no quote inside them, and an unquoted value must report no quote style at
    /// all. <see cref="YamlConfigAdapter"/> guards the same assumption internally and throws when it does not
    /// hold; this is the reachable half of that guard — if a future YamlDotNet release changes how scalar
    /// positions are reported, this test fails instead of an operator's Compose file being corrupted.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixturePaths.YamlFixtures), MemberType = typeof(FixturePaths))]
    public void Parse_EverySpan_CoversTheValueContentAndNeverItsQuotes(string fixtureName)
    {
        var document = new YamlConfigAdapter().Parse(FixturePaths.Read(fixtureName));

        foreach (var span in document.Spans)
        {
            var line = document.RawLines[span.LineIndex];
            var because = $"'{fixtureName}' span for '{span.Pointer.Path}' must cover value content only";

            (span.ValueStart + span.ValueLength).Should().BeLessThanOrEqualTo(line.Length, because);

            if (span.QuoteStyle is null)
            {
                continue;
            }

            var quote = span.QuoteStyle[0];
            span.ValueStart.Should().BeGreaterThan(0, because);
            line[span.ValueStart - 1].Should().Be(quote, because);
            line[span.ValueStart + span.ValueLength].Should().Be(quote, because);
        }
    }

    /// <summary>
    /// The motivating regression. A Compose port entry is a string only because it is quoted: written as a
    /// bare <c>27015:27015/udp</c> inside quotes it is one scalar, but if a write consumed the quotes the
    /// remaining text would be re-read as a nested mapping and the published port would silently change
    /// meaning. This pins that a write through a quoted span leaves both quotes in place and the value still
    /// parses back as a quoted string.
    /// </summary>
    [Fact]
    public void WithValue_ChangingAQuotedPortEntry_KeepsTheQuotesAndStaysAString()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/ports/1"),
            "27016:27016/udp"));

        rendered.Should().Contain("\"27016:27016/udp\"");
        rendered.Should().Be(original.Replace("\"27015:27015/udp\"", "\"27016:27016/udp\"", StringComparison.Ordinal));

        var reparsed = ValuesOf(adapter.Parse(rendered))["/services/palworld/ports/1"];
        reparsed.Style.Should().Be(YamlScalarStyle.DoubleQuoted);
        reparsed.Text.Should().Be("27016:27016/udp");
    }

    [Fact]
    public void WithValue_ChangingASingleQuotedEntry_KeepsTheSingleQuotes()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/ports/2"),
            "8213:8213/tcp"));

        rendered.Should().Be(original.Replace("'8212:8212/tcp'", "'8213:8213/tcp'", StringComparison.Ordinal));
        ValuesOf(adapter.Parse(rendered))["/services/palworld/ports/2"].Style.Should().Be(YamlScalarStyle.SingleQuoted);
    }

    [Fact]
    public void WithValue_ChangingAnUnquotedEntry_LeavesItUnquoted()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/ports/0"),
            "8214:8214/udp"));

        rendered.Should().Be(original.Replace("- 8211:8211/udp", "- 8214:8214/udp", StringComparison.Ordinal));
        ValuesOf(adapter.Parse(rendered))["/services/palworld/ports/0"].Style.Should().Be(YamlScalarStyle.Plain);
    }

    // ---------------------------------------------------------------------------------------------------
    // Preserve-unknown, stated as exact text.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The preserve-unknown guarantee as an exact-text assertion. The fixture carries comments before,
    /// between, and after its entries, an inline comment on the very line being edited, blank lines, and a
    /// nested mapping — none of which any setting models. The assertion fails the moment the adapter
    /// re-serializes instead of splicing.
    /// </summary>
    [Fact]
    public void WithValue_WritingOneValue_PreservesEveryCommentAndBlankLineExactly()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("yaml-comments-and-blanks.yaml");
        var document = adapter.Parse(original);

        var rendered = adapter.Render(document.WithValue(new ConfigPointer("/alpha"), "ONE"));

        rendered.Should().Be(original.Replace("alpha: one ", "alpha: ONE ", StringComparison.Ordinal));
        rendered.Should().Contain("# inline comment, must survive a write to alpha");
        rendered.Should().Contain("# Leading comment, before anything else in the file.");
        rendered.Should().Contain("# A final comment, followed by a trailing newline.");
    }

    [Fact]
    public void WithValue_WritingIntoAServiceSubtree_LeavesUnmodeledTopLevelKeysUntouched()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/environment/PLAYERS"),
            "32"));

        rendered.Should().Be(original.Replace("PLAYERS: 16", "PLAYERS: 32", StringComparison.Ordinal));
        rendered.Should().Contain("volumes:\n  palworld-data: {}");
    }

    // ---------------------------------------------------------------------------------------------------
    // Sequence pointers: elements are writable, the containing list is not.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The conclusion the shipped <c>strategy: publish-udp</c> bindings run into, encoded as a test. A
    /// Compose ports list is addressable element-by-element but not as a whole, because publishing a port
    /// means adding or removing a line and <see cref="ConfigDocument.WithValue"/> can only replace characters
    /// within one. The failure is loud and names the pointer — never a silent no-op that would report success
    /// while changing nothing.
    /// </summary>
    [Fact]
    public void WithValue_PointerAddressingAWholeSequence_ThrowsAndChangesNothing()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        Action write = () => document.WithValue(new ConfigPointer("/services/palworld/ports"), "9999:9999/udp");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/services/palworld/ports*");
        adapter.Render(document).Should().Be(original);
    }

    [Fact]
    public void Parse_ContainerNodes_AreNotRegisteredAsValuesOrSpans()
    {
        var document = ParsePalworld(out _);
        var values = ValuesOf(document);

        values.Keys.Should().NotContain("/services/palworld/ports");
        values.Keys.Should().NotContain("/services/palworld");
        values.Keys.Should().NotContain("/services");
        document.Spans.Should().NotContain(s => s.Pointer.Path == "/services/palworld/ports");
    }

    [Fact]
    public void WithValue_PointerAddressingASequenceElement_Works()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/ports/0"),
            "9999:9999/udp"));

        rendered.Should().Be(original.Replace("- 8211:8211/udp", "- 9999:9999/udp", StringComparison.Ordinal));
    }

    [Fact]
    public void WithValue_PointerAbsentFromTheSource_ThrowsInsteadOfCreatingIt()
    {
        var document = ParsePalworld(out _);

        Action write = () => document.WithValue(new ConfigPointer("/services/palworld/cpu_shares"), "512");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/services/palworld/cpu_shares*");
    }

    // ---------------------------------------------------------------------------------------------------
    // Anchors, aliases, merge keys.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An alias and a merge key resolve to the same node instance as the anchor they refer to, and the reader
    /// reports the anchor's position for all of them. Registering a span under each pointer would mean a
    /// write through one silently rewrote the value seen through the others, so the value is addressable only
    /// where its anchor is defined.
    /// </summary>
    [Fact]
    public void Parse_ValuesReachedOnlyThroughAnAliasOrMergeKey_AreNotAddressed()
    {
        var document = new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-anchors-and-aliases.yaml"));
        var values = ValuesOf(document);

        values.Keys.Should().Contain("/defaults/restart");
        values.Keys.Should().Contain("/scalar_anchor");
        values.Keys.Should().Contain("/plain_anchor");

        values.Keys.Should().NotContain("/services/first/<</restart");
        values.Keys.Should().NotContain("/services/first/port");
        values.Keys.Should().NotContain("/services/second/port");
        values.Keys.Should().NotContain("/services/second/players");

        document.Spans.Select(s => s.Pointer.Path).Should().OnlyHaveUniqueItems();
        document.Spans.Select(s => (s.LineIndex, s.ValueStart)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void WithValue_PointerThatOnlyResolvesThroughAnAlias_Throws()
    {
        var document = new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-anchors-and-aliases.yaml"));

        Action write = () => document.WithValue(new ConfigPointer("/services/first/port"), "9000");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/services/first/port*");
    }

    /// <summary>
    /// The reader reports an anchored scalar as starting at its <c>&amp;anchor</c>, not at the value, so the
    /// adapter has to step over node properties before applying the quote correction. Without that, an
    /// ordinary <c>x-common: &amp;common</c> Compose file would fail to parse at all.
    /// </summary>
    [Fact]
    public void WithValue_ScalarCarryingAnAnchor_WritesTheValueAndKeepsTheAnchor()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("yaml-anchors-and-aliases.yaml");
        var document = adapter.Parse(original);

        var rendered = adapter.Render(document
            .WithValue(new ConfigPointer("/scalar_anchor"), "8299")
            .WithValue(new ConfigPointer("/plain_anchor"), "24"));

        rendered.Should().Contain("scalar_anchor: &port \"8299\"");
        rendered.Should().Contain("plain_anchor: &players 24");
    }

    /// <summary>
    /// Two scalars with identical text in different places must stay independently addressable. Guards the
    /// use of reference identity rather than value equality to detect aliases — <c>YamlScalarNode</c>
    /// compares by value, so a value-keyed visited-set would collapse these two into one.
    /// </summary>
    [Fact]
    public void Parse_TwoDistinctScalarsWithIdenticalText_AreBothAddressable()
    {
        var adapter = new YamlConfigAdapter();
        var document = ParsePalworld(out var original);

        ValuesOf(document)["/services/palworld/environment/PUID"].Text.Should().Be("1000");
        ValuesOf(document)["/services/palworld/environment/PGID"].Text.Should().Be("1000");

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/services/palworld/environment/PGID"),
            "1001"));

        rendered.Should().Be(original.Replace("PGID: 1000", "PGID: 1001", StringComparison.Ordinal));
        rendered.Should().Contain("PUID: 1000");
    }

    // ---------------------------------------------------------------------------------------------------
    // Readable but deliberately unwritable values.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_BlockScalars_AreReadableButNotAddressable()
    {
        var document = new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-block-scalars.yaml"));
        var values = ValuesOf(document);

        values["/literal"].Style.Should().Be(YamlScalarStyle.Literal);
        values["/literal"].Text.Should().StartWith("first line\nsecond line\n");
        values["/literal"].IsAddressable.Should().BeFalse();

        values["/folded"].Style.Should().Be(YamlScalarStyle.Folded);
        values["/folded"].IsAddressable.Should().BeFalse();
        values["/sequence_of_blocks/0"].IsAddressable.Should().BeFalse();

        values["/after"].IsAddressable.Should().BeTrue();
        values["/sequence_of_blocks/1"].IsAddressable.Should().BeTrue();
        document.Spans.Should().NotContain(s => s.Pointer.Path == "/literal");
    }

    [Fact]
    public void WithValue_BlockScalar_ThrowsRatherThanRewritingMultipleLines()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("compose-minecraft.yaml");
        var document = adapter.Parse(original);

        Action write = () => document.WithValue(new ConfigPointer("/services/minecraft/command"), "--nogui");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/services/minecraft/command*");
        adapter.Render(document).Should().Be(original);
    }

    [Fact]
    public void Parse_MultiLinePlainScalar_IsReadableButNotAddressable()
    {
        var values = ValuesOf(new YamlConfigAdapter().Parse(FixturePaths.Read("yaml-quotes-and-styles.yaml")));

        values["/multi_line_plain"].Text.Should().Be("this value folds onto a second line");
        values["/multi_line_plain"].IsAddressable.Should().BeFalse();
    }

    /// <summary>
    /// A valueless key has a zero-length extent flush against its colon, so splicing into it would emit
    /// <c>empty_plain:x</c> — one plain scalar, not a mapping entry. An explicitly empty <i>quoted</i> value
    /// has real quotes to write between and stays writable.
    /// </summary>
    [Fact]
    public void Parse_ValuelessKey_IsNotAddressable_ButAnEmptyQuotedValueIs()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("yaml-quotes-and-styles.yaml");
        var document = adapter.Parse(original);
        var values = ValuesOf(document);

        values["/empty_plain"].IsAddressable.Should().BeFalse();
        values["/empty_double"].IsAddressable.Should().BeTrue();
        values["/empty_single"].IsAddressable.Should().BeTrue();

        Action write = () => document.WithValue(new ConfigPointer("/empty_plain"), "x");
        write.Should().Throw<KeyNotFoundException>().WithMessage("*/empty_plain*");

        adapter.Render(document.WithValue(new ConfigPointer("/empty_double"), "filled"))
            .Should().Be(original.Replace("empty_double: \"\"", "empty_double: \"filled\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every value reports up front whether a write to it will succeed, so a planner can emit a blocked
    /// change rather than discovering the refusal by catching an exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixturePaths.YamlFixtures), MemberType = typeof(FixturePaths))]
    public void Parse_IsAddressable_AgreesWithWhetherASpanExists(string fixtureName)
    {
        var document = new YamlConfigAdapter().Parse(FixturePaths.Read(fixtureName));
        var spanned = document.Spans.Select(s => s.Pointer.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var (pointer, value) in ValuesOf(document))
        {
            value.IsAddressable.Should().Be(
                spanned.Contains(pointer),
                because: $"'{fixtureName}' pointer '{pointer}' must report addressability consistent with its span");
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Line endings, BOM, empty input.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_CrlfDocumentWithoutATrailingNewline_RoundTripsAndKeepsSpanOffsetsAligned()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("yaml-crlf-no-trailing-newline.yaml");

        var document = adapter.Parse(original);

        document.LineEnding.Should().Be("\r\n");
        document.HasTrailingNewline.Should().BeFalse();
        adapter.Render(document).Should().Be(original);
        adapter.Render(document.WithValue(new ConfigPointer("/services/crlf-service/ports/0"), "9100:9100/tcp"))
            .Should().Be(original.Replace("\"9000:9000/tcp\"", "\"9100:9100/tcp\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reader does not strip a byte-order mark, so it rides along inside the first top-level key's name.
    /// The BOM is left in the source (removing it would shift every reported offset out of step with the line
    /// list, breaking span alignment) and dropped only from the <i>pointer</i>, so the key is addressable by
    /// the name a definition author would actually write.
    /// </summary>
    [Fact]
    public void Parse_DocumentWithLeadingByteOrderMark_RoundTripsAndAddressesTheFirstKeyWithoutIt()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("yaml-utf8-bom.yaml");

        var document = adapter.Parse(original);

        original.Should().StartWith("\uFEFF");
        ValuesOf(document).Keys.Should().Contain("/services/bom-service/max_players");
        ValuesOf(document).Keys.Should().NotContain(k => k.Contains('\uFEFF', StringComparison.Ordinal));

        adapter.Render(document).Should().Be(original);
        adapter.Render(document.WithValue(new ConfigPointer("/services/bom-service/max_players"), "64"))
            .Should().Be(original.Replace("max_players: 32", "max_players: 64", StringComparison.Ordinal));
    }

    /// <summary>
    /// Unlike JSON, an empty or comments-only file is valid YAML — a stream of zero documents. Rejecting it
    /// would break the round-trip contract for a legitimately empty surface, so it parses to no values.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("# just a comment\n")]
    [InlineData("# a comment\n\n# and another\n")]
    public void Parse_EmptyOrCommentsOnlyDocument_IsAcceptedAndRoundTrips(string raw)
    {
        var adapter = new YamlConfigAdapter();

        var document = adapter.Parse(raw);

        ValuesOf(document).Should().BeEmpty();
        document.Spans.Should().BeEmpty();
        adapter.Render(document).Should().Be(raw);
    }

    // ---------------------------------------------------------------------------------------------------
    // Refusals: malformed input, duplicate keys, multiple documents, pathological nesting.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_MalformedYaml_ThrowsFormatExceptionNamingTheLineAndColumn()
    {
        const string raw = "services:\n  a: 1\n   b: 2\n";

        Action parse = () => new YamlConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>().WithMessage("Invalid YAML at line *, column *");
    }

    /// <summary>
    /// A deliberate divergence from every sibling adapter, which resolve a duplicate key last-wins and keep a
    /// span for each occurrence. YamlDotNet's representation model rejects duplicates while loading, and
    /// reproducing the permissive behavior would mean hand-rolling a second YAML parser to sanction something
    /// the format does not.
    /// </summary>
    [Fact]
    public void Parse_DuplicateKeys_AreRejectedRatherThanResolvedLastWins()
    {
        const string raw = "services:\n  a:\n    max_players: 8\n    max_players: 16\n";

        Action parse = () => new YamlConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>()
            .WithMessage("Invalid YAML at line *")
            .WithMessage("*Duplicate key*");
    }

    /// <summary>
    /// A single flat pointer space cannot unambiguously address two roots, and Compose does not use
    /// multi-document streams. A lone leading <c>---</c> marker is an explicit document start, not a second
    /// document, and stays supported — <c>compose-factorio.yaml</c> uses one.
    /// </summary>
    [Fact]
    public void Parse_MultiDocumentStream_IsRejected()
    {
        const string raw = "a: 1\n---\nb: 2\n";

        Action parse = () => new YamlConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>().WithMessage("*more than one YAML document*");
    }

    [Fact]
    public void Parse_SingleDocumentWithAnExplicitStartMarker_IsAccepted()
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read("compose-factorio.yaml");

        var document = adapter.Parse(original);

        original.Should().StartWith("---");
        ValuesOf(document)["/services/factorio/image"].Text.Should().Be("factoriotools/factorio:stable");
        adapter.Render(document).Should().Be(original);
    }

    /// <summary>
    /// Pathological nesting is rejected before a character reaches YamlDotNet's recursive-descent scanner: a
    /// <see cref="StackOverflowException"/> is uncatchable in .NET and would take the host down, so no
    /// downstream handler could contain it.
    /// </summary>
    [Fact]
    public void Parse_NestingDeeperThanTheSupportedLimit_IsRejectedBeforeReachingTheReader()
    {
        var raw = "a: " + new string('[', 500) + "\n";

        Action parse = () => new YamlConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>().WithMessage("*exceeds the maximum supported depth*");
    }

    /// <summary>
    /// The depth pre-scan must understand block scalars. A <c>command: |</c> body is inert text that costs
    /// the scanner no recursion however deeply it is indented, and the indentation-counting heuristic
    /// <c>Servyx.Definitions.SafeYamlLoader</c> uses would reject this document outright. Compose files use
    /// <c>command: |</c> and <c>entrypoint: &gt;</c> routinely, so this false positive would be a real
    /// outage rather than a theoretical one.
    /// </summary>
    [Fact]
    public void Parse_BlockScalarWithADeeplyIndentedBody_IsNotMistakenForPathologicalNesting()
    {
        var body = string.Join("\n", Enumerable.Range(1, 200).Select(i => new string(' ', i + 1) + "line " + i));
        var raw = "command: |\n" + body + "\nafter: done\n";
        var adapter = new YamlConfigAdapter();

        var document = adapter.Parse(raw);

        adapter.Render(document).Should().Be(raw);
        ValuesOf(document)["/after"].Text.Should().Be("done");
        ValuesOf(document)["/command"].IsAddressable.Should().BeFalse();
    }

    [Fact]
    public void Parse_NullArgument_Throws()
    {
        Action parse = () => new YamlConfigAdapter().Parse(null!);

        parse.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Render_NullArgument_Throws()
    {
        Action render = () => new YamlConfigAdapter().Render(null!);

        render.Should().Throw<ArgumentNullException>();
    }

    // ---------------------------------------------------------------------------------------------------
    // Corpus-wide write property.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Round-trip fidelity under writing, as a property over the whole corpus: rewriting any single value
    /// with the text it already holds must reproduce the source byte-for-byte. A span that is off by even one
    /// character — the exact failure mode the quote correction exists to prevent — fails here for every
    /// fixture that contains one.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixturePaths.YamlFixtures), MemberType = typeof(FixturePaths))]
    public void WithValue_RewritingEveryValueWithItsOwnText_ReproducesTheSourceExactly(string fixtureName)
    {
        var adapter = new YamlConfigAdapter();
        var original = FixturePaths.Read(fixtureName);
        var document = adapter.Parse(original);

        foreach (var span in document.Spans)
        {
            var current = document.RawLines[span.LineIndex].Substring(span.ValueStart, span.ValueLength);

            adapter.Render(document.WithValue(span.Pointer, current)).Should().Be(
                original,
                because: $"'{fixtureName}' rewriting '{span.Pointer.Path}' with its own text must change nothing");
        }
    }
}
