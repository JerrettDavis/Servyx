
namespace Servyx.Config.Tests;

/// <summary>
/// Proves the round-trip fidelity contract by property, not by example: for every fixture in the corpus,
/// <c>Render(Parse(x)) == x</c> byte-for-byte.
/// </summary>
public class RoundTripTests
{
    [Theory]
    [MemberData(nameof(FixturePaths.DotEnvFixtures), MemberType = typeof(FixturePaths))]
    public void DotEnv_RoundTrips_ByteForByte(string fixtureName)
    {
        var original = FixturePaths.Read(fixtureName);
        var adapter = new DotEnvConfigAdapter();

        var document = adapter.Parse(original);
        var rendered = adapter.Render(document);

        rendered.Should().Be(original, because: $"'{fixtureName}' must round-trip byte-for-byte");
    }

    [Theory]
    [MemberData(nameof(FixturePaths.IniFixtures), MemberType = typeof(FixturePaths))]
    public void Ini_RoundTrips_ByteForByte(string fixtureName)
    {
        var original = FixturePaths.Read(fixtureName);
        var adapter = new IniConfigAdapter();

        var document = adapter.Parse(original);
        var rendered = adapter.Render(document);

        rendered.Should().Be(original, because: $"'{fixtureName}' must round-trip byte-for-byte");
    }

    /// <summary>
    /// The same property for YAML, run over both line-ending conventions: every fixture is checked as
    /// written on disk and again with its terminators rewritten, so a fixture's own checked-in style cannot
    /// be the only thing the adapter is ever exercised against.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixturePaths.YamlFixtures), MemberType = typeof(FixturePaths))]
    public void Yaml_RoundTrips_ByteForByte(string fixtureName)
    {
        var original = FixturePaths.Read(fixtureName);
        var adapter = new YamlConfigAdapter();

        var document = adapter.Parse(original);
        var rendered = adapter.Render(document);

        rendered.Should().Be(original, because: $"'{fixtureName}' must round-trip byte-for-byte");
    }

    [Theory]
    [MemberData(nameof(FixturePaths.YamlFixtures), MemberType = typeof(FixturePaths))]
    public void Yaml_RoundTrips_ByteForByte_UnderBothLineEndingConventions(string fixtureName)
    {
        var adapter = new YamlConfigAdapter();

        foreach (var lineEnding in new[] { "\n", "\r\n" })
        {
            var original = FixturePaths.Read(fixtureName).ReplaceLineEndings(lineEnding);

            adapter.Render(adapter.Parse(original)).Should().Be(
                original,
                because: $"'{fixtureName}' must round-trip byte-for-byte with {(lineEnding == "\n" ? "LF" : "CRLF")} terminators");
        }
    }

    [Fact]
    public void DotEnv_PreservesCrlfLineEndings()
    {
        var original = FixturePaths.Read("dotenv-crlf-no-trailing-newline.env");
        var adapter = new DotEnvConfigAdapter();

        var document = adapter.Parse(original);

        document.LineEnding.Should().Be("\r\n");
        document.HasTrailingNewline.Should().BeFalse();
        adapter.Render(document).Should().Be(original);
    }

    [Fact]
    public void DotEnv_PreservesLfLineEndings()
    {
        var original = FixturePaths.Read("dotenv-comments-and-blanks.env");
        var adapter = new DotEnvConfigAdapter();

        var document = adapter.Parse(original);

        document.LineEnding.Should().Be("\n");
        document.HasTrailingNewline.Should().BeTrue();
        adapter.Render(document).Should().Be(original);
    }

    [Fact]
    public void RealPalworldEnv_RoundTrips_AndHasNoTrailingNewline()
    {
        var original = FixturePaths.Read("real-palworld.env");
        var adapter = new DotEnvConfigAdapter();

        var document = adapter.Parse(original);

        document.HasTrailingNewline.Should().BeFalse();
        adapter.Render(document).Should().Be(original);
    }

    [Fact]
    public void RealPalworldIni_RoundTrips_AndHasTrailingNewline()
    {
        var original = FixturePaths.Read("real-palworld.ini");
        var adapter = new IniConfigAdapter();

        var document = adapter.Parse(original);

        document.HasTrailingNewline.Should().BeTrue();
        adapter.Render(document).Should().Be(original);
    }
}
