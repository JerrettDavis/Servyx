using Servyx.Infrastructure.Docker.Backups;

namespace Servyx.Infrastructure.Docker.Tests.Backups;

public class BackupGlobTests
{
    [Theory]
    [InlineData("Pal/Saved/SaveGames/**", "Pal/Saved/SaveGames/0/Level.sav", true)]
    [InlineData("Pal/Saved/SaveGames/**", "Pal/Saved/SaveGames/0/Players/a.sav", true)]
    [InlineData("Pal/Saved/SaveGames/**", "Pal/Saved/Logs/Pal.log", false)]
    [InlineData("backups/**", "backups/palworld-2026-07-20.tar.gz", true)]
    [InlineData("backups/**", "backupsx/a.tar.gz", false)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("*.tar.gz", "palworld.tar.gz", true)]
    [InlineData("*.tar.gz", "nested/palworld.tar.gz", false)]
    [InlineData("**/*.log", "a/b/c.log", true)]
    [InlineData("**/*.log", "c.log", true)]
    [InlineData(".env", ".env", true)]
    [InlineData("Level.?av", "Level.sav", true)]
    public void Matches_the_subset_of_glob_syntax_the_schema_uses(string pattern, string path, bool expected) =>
        BackupGlob.Matches(pattern, path).Should().Be(expected);

    [Fact]
    public void Matching_is_case_sensitive_because_the_targets_are()
    {
        BackupGlob.Matches("backups/**", "Backups/a.tar.gz").Should().BeFalse();
    }

    [Theory]
    [InlineData("backups/**", "backups", true)]
    [InlineData("backups/**", "backups/nested", true)]
    [InlineData("backups/**", "Pal", false)]
    [InlineData("Pal/Saved/Logs/**", "Pal/Saved/Logs", true)]
    [InlineData("Pal/Saved/Logs/**", "Pal/Saved", false)]
    public void ExcludesDirectory_prunes_whole_subtrees(string pattern, string directory, bool expected) =>
        BackupGlob.ExcludesDirectory([pattern], directory).Should().Be(expected);

    [Theory]
    [InlineData("Pal/Saved/SaveGames/**", "Pal/Saved/SaveGames")]
    [InlineData("**", "")]
    [InlineData("**/*.log", "")]
    [InlineData("Pal/*/x", "Pal")]
    [InlineData(".env", ".env")]
    public void StaticPrefix_is_the_deepest_safe_place_to_start_walking(string pattern, string expected) =>
        BackupGlob.StaticPrefix(pattern).Should().Be(expected);

    [Theory]
    [InlineData("${DATA_DIR}/x", "${DATA_DIR}/x")]
    [InlineData("/leading/slash", "leading/slash")]
    [InlineData("./relative/path", "relative/path")]
    [InlineData("double//slash", "double/slash")]
    [InlineData("back\\slash", "back/slash")]
    public void Normalize_produces_root_relative_forward_slash_form(string input, string expected) =>
        BackupGlob.Normalize(input).Should().Be(expected);
}
