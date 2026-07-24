using Servyx.Domain.Configuration;

namespace Servyx.Domain.Tests.Configuration;

public class ConfigDocumentTests
{
    private static ConfigDocument Build(string[] lines, IReadOnlyList<ConfigSpan> spans, string lineEnding = "\n", bool trailingNewline = true) =>
        new(new object(), lines, spans, lineEnding, trailingNewline);

    [Fact]
    public void Render_JoinsRawLinesWithLineEnding_AndAppendsTrailingNewlineWhenSet()
    {
        var document = Build(["FOO=bar", "BAZ=qux"], [], trailingNewline: true);

        document.Render().Should().Be("FOO=bar\nBAZ=qux\n");
    }

    [Fact]
    public void Render_OmitsTrailingNewline_WhenSourceHadNone()
    {
        var document = Build(["FOO=bar", "BAZ=qux"], [], trailingNewline: false);

        document.Render().Should().Be("FOO=bar\nBAZ=qux");
    }

    [Fact]
    public void Render_UsesCrlf_WhenLineEndingIsCrlf()
    {
        var document = Build(["A=1", "B=2"], [], lineEnding: "\r\n", trailingNewline: true);

        document.Render().Should().Be("A=1\r\nB=2\r\n");
    }

    [Fact]
    public void Render_EmptyDocument_ReturnsEmptyString()
    {
        var document = Build([], []);

        document.Render().Should().BeEmpty();
    }

    [Fact]
    public void WithValue_ReplacesOnlyTheSpanCharacters()
    {
        var pointer = new ConfigPointer("FOO");
        var span = new ConfigSpan(pointer, LineIndex: 0, ValueStart: 4, ValueLength: 3, QuoteStyle: null);
        var document = Build(["FOO=bar", "BAZ=qux"], [span]);

        var edited = document.WithValue(pointer, "longervalue");

        edited.RawLines[0].Should().Be("FOO=longervalue");
        edited.RawLines[1].Should().Be("BAZ=qux");
    }

    [Fact]
    public void WithValue_ShiftsLaterSpansOnTheSameLine()
    {
        var pointerA = new ConfigPointer("A");
        var pointerB = new ConfigPointer("B");
        var spanA = new ConfigSpan(pointerA, LineIndex: 0, ValueStart: 2, ValueLength: 1, QuoteStyle: null);
        var spanB = new ConfigSpan(pointerB, LineIndex: 0, ValueStart: 6, ValueLength: 1, QuoteStyle: null);
        var document = Build(["A=1,B=2"], [spanA, spanB]);

        var edited = document.WithValue(pointerA, "999");

        edited.RawLines[0].Should().Be("A=999,B=2");
        var shiftedSpanB = edited.Spans.Single(s => s.Pointer == pointerB);
        shiftedSpanB.ValueStart.Should().Be(6 + ("999".Length - 1));
        edited.RawLines[0].Substring(shiftedSpanB.ValueStart, shiftedSpanB.ValueLength).Should().Be("2");
    }

    [Fact]
    public void WithValue_UnknownPointer_Throws()
    {
        var document = Build(["FOO=bar"], []);

        var act = () => document.WithValue(new ConfigPointer("MISSING"), "x");

        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void WithValue_DuplicatePointer_EditsTheLastOccurrence()
    {
        var pointer = new ConfigPointer("BAZ");
        var firstSpan = new ConfigSpan(pointer, LineIndex: 0, ValueStart: 4, ValueLength: 5, QuoteStyle: null);
        var secondSpan = new ConfigSpan(pointer, LineIndex: 1, ValueStart: 4, ValueLength: 6, QuoteStyle: null);
        var document = Build(["BAZ=first", "BAZ=second"], [firstSpan, secondSpan]);

        var edited = document.WithValue(pointer, "third");

        edited.RawLines[0].Should().Be("BAZ=first");
        edited.RawLines[1].Should().Be("BAZ=third");
    }
}
