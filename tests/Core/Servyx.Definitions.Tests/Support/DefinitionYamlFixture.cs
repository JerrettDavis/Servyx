namespace Servyx.Definitions.Tests.Support;

/// <summary>
/// Loads the real, shipped <c>definitions/palworld-docker.yaml</c> once per test run. Semantic-rule tests
/// mutate a copy of this text with a single targeted <see cref="string.Replace(string,string)"/> so the
/// fixture stays realistic — everything except the one deliberately-broken piece is exactly what ships —
/// rather than hand-authoring a bespoke miniature YAML document per rule.
/// </summary>
internal static class DefinitionYamlFixture
{
    private static readonly Lazy<string> RealYamlLazy = new(() =>
    {
        var repoRoot = RepoRootLocator.Find();
        var path = Path.Combine(repoRoot.FullName, "definitions", "palworld-docker.yaml");
        return File.ReadAllText(path);
    });

    /// <summary>The real, unmodified text of <c>definitions/palworld-docker.yaml</c>.</summary>
    public static string RealYaml => RealYamlLazy.Value;

    /// <summary>
    /// Returns <see cref="RealYaml"/> with <paramref name="find"/> replaced by <paramref name="replace"/>,
    /// asserting the substitution actually matched something — a silently-no-op mutation would make the
    /// test that uses it pass for the wrong reason.
    /// </summary>
    public static string Mutate(string find, string replace)
    {
        var yaml = RealYaml;
        if (!yaml.Contains(find, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture mutation target '{find}' was not found in the real definition text.");
        }

        return yaml.Replace(find, replace, StringComparison.Ordinal);
    }
}
