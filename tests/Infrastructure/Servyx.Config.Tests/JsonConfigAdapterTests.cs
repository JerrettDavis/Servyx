using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

/// <summary>
/// Tests for <see cref="JsonConfigAdapter"/> — the adapter that makes <c>SurfaceFormat.Json</c> readable and
/// writable at runtime, and the first one whose values are addressed by RFC 6901 JSON pointer rather than by
/// a flat key. Fixtures are inline (like <see cref="PropertiesConfigAdapterTests"/>) since the documents are
/// short enough to read beside the assertion that depends on their exact formatting.
/// </summary>
public class JsonConfigAdapterTests
{
    /// <summary>
    /// A nested configuration document in the shape a real game server ships: a mix of strings, integers,
    /// booleans, and one nested object whose member is only reachable by pointer (<c>/visibility/public</c>).
    /// <c>ReplaceLineEndings</c> pins the line endings so the byte-for-byte assertions below do not depend on
    /// how this source file happens to be checked out.
    /// </summary>
    private static readonly string NestedSurface =
        """
        {
          "display_name": "Test Server",
          "description": "",
          "max_players": 32,
          "visibility": {
            "public": true,
            "lan": true
          },
          "join_password": "",
          "verify_users": true,
          "autosave_interval": 10,
          "autosave_slots": 5,
          "command_access": "admins-only",
          "auto_pause": true
        }
        """.ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// The same surface plus content no setting descriptor models — a nested <c>diagnostics</c> object, an
    /// array, a null, a key with an unusual name — and deliberately irregular formatting (a four-space block,
    /// a compact one-line object, a blank line, no space after one colon). All of it must survive a write.
    /// </summary>
    private static readonly string SurfaceWithUnmodeledContent =
        """
        {
          "display_name": "Test Server",
          "max_players": 32,

          "visibility": {
            "public": true,
            "lan": true
          },
          "diagnostics": {
              "log_level": "warn",
              "sinks": ["file", "stdout"],
              "retention_days": null
          },
          "operator_tags": [1, 2, 3],
          "vendor/extension~key":"left alone",
          "auto_pause": true
        }
        """.ReplaceLineEndings("\n") + "\n";

    [Fact]
    public void FormatId_IsJson()
    {
        new JsonConfigAdapter().FormatId.Should().Be("json");
    }

    [Fact]
    public void Parse_NestedScalars_AreAddressedByRfc6901Pointers()
    {
        var document = new JsonConfigAdapter().Parse(NestedSurface);

        var values = ((JsonConfigDocument)document.Root).Values;
        values["/visibility/public"].Text.Should().Be("true");
        values["/visibility/lan"].Text.Should().Be("true");
        values["/display_name"].Text.Should().Be("Test Server");
        values["/max_players"].Text.Should().Be("32");
        values["/description"].Text.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Scalars_CarryTheirNativeJsonKinds()
    {
        var document = new JsonConfigAdapter().Parse(NestedSurface);

        var values = ((JsonConfigDocument)document.Root).Values;
        values["/max_players"].Kind.Should().Be(JsonValueKind.Number);
        values["/autosave_interval"].Kind.Should().Be(JsonValueKind.Number);
        values["/visibility/public"].Kind.Should().Be(JsonValueKind.True);
        values["/display_name"].Kind.Should().Be(JsonValueKind.String);
        values["/command_access"].Kind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void Parse_ArrayElements_AreAddressedByZeroBasedIndex()
    {
        const string raw = """{"tags": ["alpha", "beta"], "ports": [8211, 27015]}""";

        var document = new JsonConfigAdapter().Parse(raw);

        var values = ((JsonConfigDocument)document.Root).Values;
        values["/tags/0"].Text.Should().Be("alpha");
        values["/tags/1"].Text.Should().Be("beta");
        values["/ports/1"].Kind.Should().Be(JsonValueKind.Number);
        values["/ports/1"].Text.Should().Be("27015");
    }

    [Fact]
    public void Parse_PropertyNamesContainingSlashOrTilde_AreEscapedPerRfc6901()
    {
        const string raw = """{"a/b": 1, "c~d": 2}""";

        var document = new JsonConfigAdapter().Parse(raw);

        var values = ((JsonConfigDocument)document.Root).Values;
        values.Keys.Should().Contain("/a~1b");
        values.Keys.Should().Contain("/c~0d");
    }

    [Fact]
    public void Parse_StringWithEscapeSequences_DecodesTextButKeepsRawSource()
    {
        const string raw = "{\"motd\": \"line1\\nline2 \\u00e9\"}";

        var document = new JsonConfigAdapter().Parse(raw);

        var motd = ((JsonConfigDocument)document.Root).Values["/motd"];
        motd.Text.Should().Be("line1\nline2 \u00e9");
        motd.Raw.Should().Be("line1\\nline2 \\u00e9");
    }

    [Fact]
    public void Parse_DuplicateProperties_LastOccurrenceWinsForReads_BothSpansPreserved()
    {
        const string raw = """{"max_players": 8, "max_players": 16}""";

        var document = new JsonConfigAdapter().Parse(raw);

        ((JsonConfigDocument)document.Root).Values["/max_players"].Text.Should().Be("16");
        document.Spans.Count(s => s.Pointer == new ConfigPointer("/max_players")).Should().Be(2);
    }

    [Fact]
    public void Parse_EmptyContainers_AreValidAndContributeNoValues()
    {
        var document = new JsonConfigAdapter().Parse("""{"a": {}, "b": []}""");

        ((JsonConfigDocument)document.Root).Values.Should().BeEmpty();
        document.Spans.Should().BeEmpty();
    }

    [Fact]
    public void Render_RoundTripsUnmodifiedInput_ByteForByte()
    {
        var adapter = new JsonConfigAdapter();

        var document = adapter.Parse(NestedSurface);

        adapter.Render(document).Should().Be(NestedSurface);
    }

    /// <summary>
    /// The preserve-unknown guarantee, stated as an exact-text assertion: after writing one nested boolean,
    /// the rendered document differs from the source in exactly those five characters and nowhere else. The
    /// fixture carries a nested object, an array, a <c>null</c>, a slash/tilde-bearing key, a blank line, a
    /// four-space indented block, and a colon with no space after it — none of which any setting models —
    /// so the assertion fails the moment the adapter re-serializes instead of splicing.
    /// </summary>
    [Fact]
    public void WithValue_WritingOneNestedSetting_PreservesUnmodeledKeysAndFormattingExactly()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(SurfaceWithUnmodeledContent);

        var edited = document.WithValue(new ConfigPointer("/visibility/public"), "false");

        var expected = SurfaceWithUnmodeledContent.Replace(
            "\"public\": true",
            "\"public\": false",
            StringComparison.Ordinal);
        adapter.Render(edited).Should().Be(expected);
    }

    [Fact]
    public void WithValue_ChangingAnIntegerSetting_KeepsItAJsonNumberNotAString()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);

        var rendered = adapter.Render(document.WithValue(new ConfigPointer("/max_players"), "64"));

        rendered.Should().Contain("\"max_players\": 64");
        var reparsed = ((JsonConfigDocument)adapter.Parse(rendered).Root).Values["/max_players"];
        reparsed.Kind.Should().Be(JsonValueKind.Number);
        reparsed.Text.Should().Be("64");
    }

    [Fact]
    public void WithValue_ChangingABooleanSetting_KeepsItAJsonBoolean()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);

        var rendered = adapter.Render(document.WithValue(new ConfigPointer("/auto_pause"), "false"));

        ((JsonConfigDocument)adapter.Parse(rendered).Root).Values["/auto_pause"].Kind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    public void WithValue_ChangingAStringSetting_KeepsTheSurroundingQuotes()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);

        var rendered = adapter.Render(document.WithValue(new ConfigPointer("/display_name"), "Renamed"));

        rendered.Should().Contain("\"display_name\": \"Renamed\"");
        var reparsed = ((JsonConfigDocument)adapter.Parse(rendered).Root).Values["/display_name"];
        reparsed.Kind.Should().Be(JsonValueKind.String);
        reparsed.Text.Should().Be("Renamed");
    }

    [Fact]
    public void EscapeStringContent_ValueWithQuotesBackslashesAndNewline_StaysReparsable()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);
        const string awkward = "He said \"go\"\nC:\\servers\ttab";

        var rendered = adapter.Render(document.WithValue(
            new ConfigPointer("/display_name"),
            JsonConfigAdapter.EscapeStringContent(awkward)));

        var reparsed = ((JsonConfigDocument)adapter.Parse(rendered).Root).Values["/display_name"];
        reparsed.Kind.Should().Be(JsonValueKind.String);
        reparsed.Text.Should().Be(awkward);
    }

    /// <summary>
    /// Pins the documented "refuse, do not create" choice for a pointer whose intermediate objects are absent
    /// from the source: the write throws and names the pointer, and the document is left byte-identical —
    /// the adapter never invents the missing structure, indentation, or separators.
    /// </summary>
    [Fact]
    public void WithValue_PointerWhoseIntermediateObjectsAreAbsent_ThrowsInsteadOfCreatingThem()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);

        Action write = () => document.WithValue(new ConfigPointer("/moderation/kick_on_idle"), "true");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/moderation/kick_on_idle*");
        adapter.Render(document).Should().Be(NestedSurface);
    }

    [Fact]
    public void WithValue_TopLevelPointerAbsentFromTheSource_AlsoThrowsRatherThanAppending()
    {
        var adapter = new JsonConfigAdapter();
        var document = adapter.Parse(NestedSurface);

        Action write = () => document.WithValue(new ConfigPointer("/not_in_the_file"), "1");

        write.Should().Throw<KeyNotFoundException>().WithMessage("*/not_in_the_file*");
    }

    [Fact]
    public void Parse_MalformedJson_ThrowsFormatExceptionNamingTheLineAndColumn()
    {
        const string raw = "{\n  \"a\": 1,\n  \"b\": \n}\n";

        Action parse = () => new JsonConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>()
            .WithMessage("*line 4*")
            .WithMessage("*column 1*")
            .WithMessage("*expected a JSON value*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("""{"a": 1,}""")]
    [InlineData("{a: 1}")]
    [InlineData("""{"a": 01}""")]
    [InlineData("""{"a": 1} trailing""")]
    [InlineData("// a comment\n{}")]
    [InlineData("{\"a\": \"unterminated}")]
    [InlineData("""{"a": 1""")]
    [InlineData("""["a", ]""")]
    public void Parse_MalformedInput_ThrowsFormatException_NeverASilentlyEmptyDocument(string raw)
    {
        Action parse = () => new JsonConfigAdapter().Parse(raw);

        parse.Should().Throw<FormatException>().WithMessage("Invalid JSON at line *");
    }

    [Fact]
    public void Parse_CrlfDocument_PreservesLineEndingsAndKeepsSpanOffsetsAligned()
    {
        var crlf = NestedSurface.ReplaceLineEndings("\r\n");
        var adapter = new JsonConfigAdapter();

        var document = adapter.Parse(crlf);

        document.LineEnding.Should().Be("\r\n");
        adapter.Render(document).Should().Be(crlf);
        adapter.Render(document.WithValue(new ConfigPointer("/max_players"), "64"))
            .Should().Be(crlf.Replace("\"max_players\": 32", "\"max_players\": 64", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_DocumentWithLeadingByteOrderMark_RoundTripsAndKeepsSpanOffsetsAligned()
    {
        const string raw = "\uFEFF{\"max_players\": 32}";
        var adapter = new JsonConfigAdapter();

        var document = adapter.Parse(raw);

        adapter.Render(document).Should().Be(raw);
        adapter.Render(document.WithValue(new ConfigPointer("/max_players"), "64"))
            .Should().Be("\uFEFF{\"max_players\": 64}");
    }

    /// <summary>
    /// Every format this project ships an <see cref="IConfigAdapter"/> for. <c>yaml</c> is no longer a
    /// pending case: <see cref="YamlConfigAdapter"/> is registered by
    /// <see cref="ServiceCollectionExtensions.AddServyxConfig"/>, so it is asserted as present below rather
    /// than merely tolerated.
    /// </summary>
    private static readonly string[] KnownFormatIds = ["dotenv", "ini", "properties", "json", "yaml"];

    /// <summary>
    /// Pins registration without pinning it to a moment in time. An exact-set assertion would have to be
    /// edited in lockstep with every adapter that gets wired up, which means it is either failing or lying
    /// during the window between an adapter being written and being registered. Asserting "every known
    /// format is present, every id is unique, and nothing outside the known set is registered" still catches
    /// what the exact-set version caught — a dropped registration, a typo'd format id — plus a duplicate id,
    /// which it did not.
    /// </summary>
    /// <remarks>
    /// <c>yaml</c> moved from <see cref="KnownFormatIds"/>-only into the presence assertion when
    /// <see cref="YamlConfigAdapter"/> was wired up. Leaving it merely tolerated would have meant a dropped
    /// yaml registration passing this test silently — the window the looser form existed to cover has
    /// closed, and keeping the looser form open past that point is how a gap becomes permanent.
    /// </remarks>
    [Fact]
    public void AddServyxConfig_RegistersAnAdapterForEveryBuiltInFormat_WithDistinctFormatIds()
    {
        var services = new ServiceCollection();

        services.AddServyxConfig();

        var formatIds = services
            .Where(d => d.ServiceType == typeof(IConfigAdapter))
            .Select(d => ((IConfigAdapter)Activator.CreateInstance(d.ImplementationType!)!).FormatId)
            .ToList();

        formatIds.Should().OnlyHaveUniqueItems();
        formatIds.Should().Contain(KnownFormatIds);
        formatIds.Should().BeSubsetOf(KnownFormatIds);
    }
}
