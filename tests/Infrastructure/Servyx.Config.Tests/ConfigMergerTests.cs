using FluentAssertions;
using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

public class ConfigMergerTests
{
    private const string OptionSettingsPointerPath = "[/Script/Pal.PalGameWorldSettings].OptionSettings";

    [Fact]
    public void Merge_SingleValueEdit_ChangesOnlyThatValuesCharacters_EverythingElseIdentical()
    {
        var original = FixturePaths.Read("real-palworld.env");
        var adapter = new DotEnvConfigAdapter();
        var document = adapter.Parse(original);
        var merger = new ConfigMerger([new UnrealOptionSettingsCodec()]);

        var edited = merger.Merge(document, new ConfigPointer("SERVER_NAME"), "MyNewServerName", MergePolicy.PreserveUnknown);
        var rendered = adapter.Render(edited);

        rendered.Should().NotBe(original);

        // Manually apply the same substitution as a plain string replace and assert byte-for-byte
        // equality — proving nothing else in the file (including the hand-written comment block directly
        // above SERVER_NAME) moved by even one character.
        var expected = original.Replace("SERVER_NAME=Palygondwanaland", "SERVER_NAME=MyNewServerName");
        rendered.Should().Be(expected);
    }

    [Fact]
    public void MergeAll_DecodesAndEncodesTheCodecExactlyOnce_ForManyEditsInTheSameScalar()
    {
        var original = FixturePaths.Read("real-palworld.ini");
        var adapter = new IniConfigAdapter();
        var document = adapter.Parse(original);

        var spy = new CountingConfigValueCodec(new UnrealOptionSettingsCodec());
        var merger = new ConfigMerger([spy]);

        var edits = new[]
        {
            EditFor("Difficulty", "Normal"),
            EditFor("DayTimeSpeedRate", "2.500000"),
            EditFor("NightTimeSpeedRate", "3.000000"),
            EditFor("ServerName", "Edited Server"),
            EditFor("GuildPlayerMaxNum", "40"),
        };

        var result = merger.MergeAll(document, edits, MergePolicy.PreserveUnknown);

        spy.DecodeCount.Should().Be(1);
        spy.EncodeCount.Should().Be(1);

        var span = result.Spans.Single(s => s.Pointer == new ConfigPointer(OptionSettingsPointerPath));
        var newScalar = result.RawLines[span.LineIndex].Substring(span.ValueStart, span.ValueLength);
        var decoded = new UnrealOptionSettingsCodec().Decode(newScalar);

        decoded["Difficulty"].Should().Be("Normal");
        decoded["DayTimeSpeedRate"].Should().Be("2.500000");
        decoded["NightTimeSpeedRate"].Should().Be("3.000000");
        decoded["ServerName"].Should().Be("\"Edited Server\"");
        decoded["GuildPlayerMaxNum"].Should().Be("40");

        // Untouched members keep their exact original raw text.
        decoded["ExpRate"].Should().Be("1.000000");
    }

    [Fact]
    public void MergeAll_MixOfDirectAndScopedEdits_AppliesBoth()
    {
        var raw = "TOP=1\n[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(A=1,B=2)\n";
        var document = new IniConfigAdapter().Parse(raw);
        var merger = new ConfigMerger([new StubCodec()]);

        var edits = new ConfigEdit[]
        {
            new(new ConfigPointer("[].TOP"), "99"),
            new(new ConfigPointer($"{OptionSettingsPointerPath}#stub:A"), "111"),
        };

        var result = merger.MergeAll(document, edits, MergePolicy.PreserveUnknown);
        var rendered = new IniConfigAdapter().Render(result);

        rendered.Should().Be("TOP=99\n[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(A=111,B=2)\n");
    }

    [Fact]
    public void MergeAll_PreservesLineEnding_WhenSourceIsCrlf()
    {
        var raw = "FIRST=1\r\nSECOND=2\r\n";
        var document = new DotEnvConfigAdapter().Parse(raw);
        var merger = new ConfigMerger([]);

        var result = merger.Merge(document, new ConfigPointer("FIRST"), "99", MergePolicy.PreserveUnknown);

        result.LineEnding.Should().Be("\r\n");
        new DotEnvConfigAdapter().Render(result).Should().Be("FIRST=99\r\nSECOND=2\r\n");
    }

    private static ConfigEdit EditFor(string member, string newValue) =>
        new(new ConfigPointer($"{OptionSettingsPointerPath}#unreal-option-settings:{member}"), newValue);

    /// <summary>A spy over <see cref="UnrealOptionSettingsCodec"/> that counts Decode/Encode invocations.</summary>
    private sealed class CountingConfigValueCodec(IConfigValueCodec inner) : IConfigValueCodec
    {
        public int DecodeCount { get; private set; }

        public int EncodeCount { get; private set; }

        public string CodecId => inner.CodecId;

        public IReadOnlyDictionary<string, string> Decode(string scalar)
        {
            DecodeCount++;
            return inner.Decode(scalar);
        }

        public string Encode(IReadOnlyDictionary<string, string> members)
        {
            EncodeCount++;
            return inner.Encode(members);
        }
    }

    /// <summary>A minimal codec for a trivial <c>(A=..,B=..)</c> scalar, used where the full Unreal grammar isn't needed.</summary>
    private sealed class StubCodec : IConfigValueCodec
    {
        public string CodecId => "stub";

        public IReadOnlyDictionary<string, string> Decode(string scalar)
        {
            var inner = scalar.Trim('(', ')');
            var members = new OrderedDictionary<string, string>();
            foreach (var part in inner.Split(','))
            {
                var eq = part.IndexOf('=');
                members[part[..eq]] = part[(eq + 1)..];
            }

            return members;
        }

        public string Encode(IReadOnlyDictionary<string, string> members) =>
            "(" + string.Join(",", members.Select(kv => $"{kv.Key}={kv.Value}")) + ")";
    }
}
