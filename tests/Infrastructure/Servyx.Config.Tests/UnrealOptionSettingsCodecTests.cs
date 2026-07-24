using Servyx.Domain.Configuration;

namespace Servyx.Config.Tests;

public class UnrealOptionSettingsCodecTests
{
    private static string RealOptionSettingsScalar()
    {
        var raw = FixturePaths.Read("real-palworld.ini");
        var document = new IniConfigAdapter().Parse(raw);
        var span = document.Spans.Single(s => s.Pointer == new ConfigPointer("[/Script/Pal.PalGameWorldSettings].OptionSettings"));
        return document.RawLines[span.LineIndex].Substring(span.ValueStart, span.ValueLength);
    }

    [Fact]
    public void Decode_NumericFormatting_IsPreservedVerbatim()
    {
        var codec = new UnrealOptionSettingsCodec();
        var decoded = codec.Decode(RealOptionSettingsScalar());

        decoded["DayTimeSpeedRate"].Should().Be("1.000000");
    }

    [Fact]
    public void DecodeThenEncode_WithoutEditing_NumericValueSurvives_AndDoesNotBecome1Or1Point0()
    {
        var codec = new UnrealOptionSettingsCodec();
        var scalar = RealOptionSettingsScalar();

        var decoded = codec.Decode(scalar);
        var reEncoded = codec.Encode(decoded);

        reEncoded.Should().Be(scalar);
        reEncoded.Should().Contain("DayTimeSpeedRate=1.000000");
        reEncoded.Should().NotContain("DayTimeSpeedRate=1,");
        reEncoded.Should().NotContain("DayTimeSpeedRate=1.0,");
    }

    [Fact]
    public void Decode_HandlesNestedParentheses()
    {
        var codec = new UnrealOptionSettingsCodec();
        var decoded = codec.Decode("(A=1,CrossplayPlatforms=(Steam,Xbox,PS5,Mac),B=2)");

        decoded.Should().HaveCount(3);
        decoded["CrossplayPlatforms"].Should().Be("(Steam,Xbox,PS5,Mac)");
        decoded["A"].Should().Be("1");
        decoded["B"].Should().Be("2");
    }

    [Fact]
    public void Decode_QuotedValueContainingCommas_IsNotSplit()
    {
        var codec = new UnrealOptionSettingsCodec();
        var decoded = codec.Decode("(A=1,ServerDescription=\"Holding balls, and throwing spheres\",B=2)");

        decoded.Should().HaveCount(3);
        decoded["ServerDescription"].Should().Be("\"Holding balls, and throwing spheres\"");
    }

    [Fact]
    public void Decode_EmptyValue_IsPreserved()
    {
        var codec = new UnrealOptionSettingsCodec();
        var decoded = codec.Decode("(A=1,DenyTechnologyList=,B=2)");

        decoded["DenyTechnologyList"].Should().Be(string.Empty);
    }

    [Fact]
    public void Encode_PreservesMemberOrder()
    {
        var codec = new UnrealOptionSettingsCodec();
        var scalar = RealOptionSettingsScalar();

        var decoded = codec.Decode(scalar);
        var keysBefore = decoded.Keys.ToList();

        var reEncoded = codec.Encode(decoded);
        var keysAfter = codec.Decode(reEncoded).Keys.ToList();

        keysAfter.Should().Equal(keysBefore);
    }

    [Fact]
    public void UnknownKeys_RetainOriginalOrder_AfterBatchEditOfTwelveOtherKeys()
    {
        var codec = new UnrealOptionSettingsCodec();
        var scalar = RealOptionSettingsScalar();
        var decoded = codec.Decode(scalar);
        var keysBefore = decoded.Keys.ToList();

        var edited = new OrderedDictionary<string, string>();
        foreach (var (key, value) in decoded)
        {
            edited[key] = value;
        }

        var keysToEdit = new[]
        {
            "Difficulty", "DayTimeSpeedRate", "NightTimeSpeedRate", "ExpRate", "PalCaptureRate",
            "PalSpawnNumRate", "DropItemMaxNum", "BaseCampMaxNum", "GuildPlayerMaxNum", "WorkSpeedRate",
            "CoopPlayerMaxNum", "MaxBuildingLimitNum",
        };
        keysToEdit.Should().HaveCount(12);

        foreach (var key in keysToEdit)
        {
            edited[key] = edited[key] + "_EDITED";
        }

        var reEncoded = codec.Encode(edited);
        var keysAfter = codec.Decode(reEncoded).Keys.ToList();

        keysAfter.Should().Equal(keysBefore, "editing values must never reorder members, including the ones left untouched");

        var afterValues = codec.Decode(reEncoded);
        foreach (var key in keysToEdit)
        {
            afterValues[key].Should().EndWith("_EDITED");
        }

        // A key that was never touched keeps its exact original raw text.
        afterValues["ServerName"].Should().Be(decoded["ServerName"]);
    }
}
