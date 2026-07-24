using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

public class DotEnvConfigAdapterTests
{
    [Fact]
    public void Parse_ReadsSimpleKeyValues_SkippingCommentsAndBlankLines()
    {
        var raw = FixturePaths.Read("dotenv-comments-and-blanks.env");
        var document = new DotEnvConfigAdapter().Parse(raw);

        var values = ((DotEnvDocument)document.Root).Values;
        values.Should().Contain(new KeyValuePair<string, string>("FOO", "bar"));
        values.Should().Contain(new KeyValuePair<string, string>("BAZ", "qux"));
        values.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_HandlesQuotesAndInlineComments()
    {
        var raw = FixturePaths.Read("dotenv-quotes-and-inline-comments.env");
        var document = new DotEnvConfigAdapter().Parse(raw);

        var values = ((DotEnvDocument)document.Root).Values;
        values["DOUBLE"].Should().Be("hello world");
        values["SINGLE"].Should().Be("single quoted value");
        values["EMPTY_DOUBLE"].Should().Be(string.Empty);
        values["UNQUOTED"].Should().Be("value");
        values["HASH_IN_VALUE"].Should().Be("a#b");
    }

    [Fact]
    public void Parse_ExportPrefixAndDuplicateKeys_LastOccurrenceWinsForReads()
    {
        var raw = FixturePaths.Read("dotenv-export-and-duplicates.env");
        var document = new DotEnvConfigAdapter().Parse(raw);

        var values = ((DotEnvDocument)document.Root).Values;
        values["FOO"].Should().Be("bar");
        values["BAZ"].Should().Be("second");
        values["QUX"].Should().Be("only");

        // Both BAZ occurrences are preserved as spans (and therefore in RawLines) even though only the
        // last one determines the read value.
        document.Spans.Count(s => s.Pointer == new ConfigPointer("BAZ")).Should().Be(2);
    }

    [Fact]
    public void Parse_Utf8Bom_IsPreservedAndDoesNotBreakFirstKey()
    {
        var raw = FixturePaths.Read("dotenv-utf8-bom.env");
        var document = new DotEnvConfigAdapter().Parse(raw);

        var values = ((DotEnvDocument)document.Root).Values;
        values["FOO"].Should().Be("bar");
        document.RawLines[0].Should().StartWith("﻿");
    }

    [Fact]
    public void WithValue_ChangingOneKey_OnlyChangesThatKeysCharacters()
    {
        var raw = "FOO=bar\nBAZ=qux\n";
        var document = new DotEnvConfigAdapter().Parse(raw);

        var edited = document.WithValue(new ConfigPointer("FOO"), "newvalue");
        var adapter = new DotEnvConfigAdapter();

        adapter.Render(edited).Should().Be("FOO=newvalue\nBAZ=qux\n");
    }
}
