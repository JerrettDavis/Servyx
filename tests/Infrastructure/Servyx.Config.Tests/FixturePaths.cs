namespace Servyx.Config.Tests;

/// <summary>Locates the <c>Fixtures/</c> files copied to the test output directory.</summary>
internal static class FixturePaths
{
    /// <summary>Reads a fixture file's raw text exactly as written on disk (no encoding normalization beyond UTF-8 decode).</summary>
    public static string Read(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        var bytes = File.ReadAllBytes(path);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Every <c>.env</c> fixture file name, for parameterized round-trip tests.</summary>
    public static IEnumerable<object[]> DotEnvFixtures()
    {
        yield return ["dotenv-comments-and-blanks.env"];
        yield return ["dotenv-quotes-and-inline-comments.env"];
        yield return ["dotenv-export-and-duplicates.env"];
        yield return ["dotenv-crlf-no-trailing-newline.env"];
        yield return ["dotenv-utf8-bom.env"];
        yield return ["real-palworld.env"];
    }

    /// <summary>Every <c>.ini</c> fixture file name, for parameterized round-trip tests.</summary>
    public static IEnumerable<object[]> IniFixtures()
    {
        yield return ["ini-multi-section.ini"];
        yield return ["real-palworld.ini"];
    }
}
