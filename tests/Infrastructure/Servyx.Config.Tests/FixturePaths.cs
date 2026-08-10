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

    /// <summary>
    /// Every <c>.yaml</c> fixture file name, for parameterized round-trip tests. The four <c>compose-*</c>
    /// entries stand in for the Docker Compose files every shipped definition's <c>compose</c> surface points
    /// at — those live on a deployed server, never in this repository, so they are authored here from the
    /// upstream images' documented shapes rather than copied.
    /// </summary>
    public static IEnumerable<object[]> YamlFixtures()
    {
        yield return ["compose-palworld.yaml"];
        yield return ["compose-minecraft.yaml"];
        yield return ["compose-factorio.yaml"];
        yield return ["compose-ark-asa.yaml"];
        yield return ["yaml-comments-and-blanks.yaml"];
        yield return ["yaml-quotes-and-styles.yaml"];
        yield return ["yaml-block-scalars.yaml"];
        yield return ["yaml-anchors-and-aliases.yaml"];
        yield return ["yaml-crlf-no-trailing-newline.yaml"];
        yield return ["yaml-utf8-bom.yaml"];
    }
}
