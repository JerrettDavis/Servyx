using FluentAssertions;
using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

public class IniConfigAdapterTests
{
    [Fact]
    public void Parse_ReadsSectionsAndValuesContainingEqualsAndHash()
    {
        var raw = FixturePaths.Read("ini-multi-section.ini");
        var document = new IniConfigAdapter().Parse(raw);

        var sections = ((IniDocument)document.Root).Sections;
        sections["Section1"]["Key1"].Should().Be("Value1");
        sections["Section1"]["Key2"].Should().Be("Value=With=Equals");
        sections["Section1"]["Key3"].Should().Be("HasHash#InIt");
        sections["Section2"]["KeyA"].Should().Be("1");
    }

    [Fact]
    public void Parse_DuplicateSections_MergeEntriesInFileOrder()
    {
        var raw = FixturePaths.Read("ini-multi-section.ini");
        var document = new IniConfigAdapter().Parse(raw);

        var sections = ((IniDocument)document.Root).Sections;

        // Key4 lives under the second occurrence of [Section1], but the read model merges by section name.
        sections["Section1"]["Key4"].Should().Be("AddedLater");
        sections["Section1"].Should().ContainKey("Key1");
    }

    [Fact]
    public void Parse_BothCommentStyles_AreSkippedAsPassthrough()
    {
        var raw = FixturePaths.Read("ini-multi-section.ini");
        var document = new IniConfigAdapter().Parse(raw);

        var sections = ((IniDocument)document.Root).Sections;
        sections.Values.SelectMany(s => s.Keys).Should().NotContain(k => k.StartsWith(';') || k.StartsWith('#'));
    }

    [Fact]
    public void WithValue_ChangingOneKey_PreservesEverythingElseByteForByte()
    {
        var raw = "[S]\nKey1=Value1\nKey2=Value2\n";
        var document = new IniConfigAdapter().Parse(raw);

        var edited = document.WithValue(new ConfigPointer("[S].Key1"), "Changed");
        var adapter = new IniConfigAdapter();

        adapter.Render(edited).Should().Be("[S]\nKey1=Changed\nKey2=Value2\n");
    }

    [Fact]
    public void RealPalworldIni_SingleValueEdit_ChangesOnlyThatValuesCharacters()
    {
        var raw = FixturePaths.Read("real-palworld.ini");
        var adapter = new IniConfigAdapter();
        var document = adapter.Parse(raw);

        var pointer = new ConfigPointer("[/Script/Pal.PalGameWorldSettings].OptionSettings");
        var originalSpan = document.Spans.Single(s => s.Pointer == pointer);
        var originalLine = document.RawLines[originalSpan.LineIndex];

        // Replace only the scalar's raw text with itself but with one character changed deep inside, to
        // prove WithValue only ever touches the span's own characters.
        var newRaw = originalLine.Substring(originalSpan.ValueStart, originalSpan.ValueLength).Replace("Difficulty=None", "Difficulty=Normal");
        var edited = document.WithValue(pointer, newRaw);
        var rendered = adapter.Render(edited);

        rendered.Should().NotBe(raw);
        rendered.Replace("Difficulty=Normal", "Difficulty=None").Should().Be(raw);
    }
}
