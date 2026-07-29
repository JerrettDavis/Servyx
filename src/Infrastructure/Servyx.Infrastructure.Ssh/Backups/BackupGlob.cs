using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Servyx.Infrastructure.Ssh.Backups;

/// <summary>
/// Matches the glob patterns an SSH backup definition declares against root-relative POSIX paths.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a second copy rather than a shared type.</strong> An identical (larger) matcher lives
/// in <c>Servyx.Infrastructure.Docker.Backups.BackupGlob</c>. Infrastructure projects may not reference one
/// another, so the only home both could share is <c>Servyx.Domain</c>. Nothing here does I/O, so promoting
/// it would not drag I/O into Domain — the blocker is purely that a promotion has to edit the Docker
/// provider, its adopter, its DI extension, six Docker test files and the web host's context source in the
/// same change. That is a mechanical cross-project refactor that deserves its own commit and its own
/// verification run, not a side effect of adding a second provider. <see cref="BackupGlob"/> and
/// <see cref="BackupRetentionEvaluator"/> are the two pieces that are genuinely provider-agnostic policy and
/// are the right candidates when that refactor happens.
/// </para>
/// <para>
/// This copy is deliberately <em>smaller</em> than the Docker one. <see cref="SshBackupProvider"/> archives
/// by asking the remote host's own <c>tar</c> to walk the tree, so it never implements a directory walker
/// and therefore needs none of the walker-support surface (<c>ExcludesDirectory</c>, <c>StaticPrefix</c>,
/// <c>HasWildcard</c>). What remains is normalization and whole-path matching, used to recognise foreign
/// archive filenames and to validate the include set.
/// </para>
/// <para>
/// Supported syntax is the subset the schema uses: <c>**</c> spans any number of path segments, <c>*</c>
/// matches within a single segment, and <c>?</c> matches a single non-separator character. Matching is
/// ordinal and case-sensitive, because the targets are POSIX hosts where <c>Backups</c> and <c>backups</c>
/// are two different directories. Patterns compile to <see cref="RegexOptions.NonBacktracking"/> regexes so
/// a definition author cannot author a catastrophic-backtracking denial of service.
/// </para>
/// </remarks>
public static class BackupGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Normalizes a path or pattern to the root-relative, forward-slash form matching expects.</summary>
    /// <param name="value">The raw path or pattern.</param>
    /// <returns>The normalized value; an empty string for a value that denotes the root itself.</returns>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized;
    }

    /// <summary>Whether <paramref name="pattern"/> matches <paramref name="path"/> in full.</summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <param name="path">A root-relative path.</param>
    public static bool Matches(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);

        return Compile(Normalize(pattern)).IsMatch(Normalize(path));
    }

    /// <summary>Whether any of <paramref name="patterns"/> matches <paramref name="path"/>.</summary>
    /// <param name="patterns">The glob patterns.</param>
    /// <param name="path">A root-relative path.</param>
    public static bool MatchesAny(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(path);

        var normalizedPath = Normalize(path);
        foreach (var pattern in patterns)
        {
            if (Compile(Normalize(pattern)).IsMatch(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="pattern"/> contains any wildcard metacharacter.</summary>
    /// <param name="pattern">The glob pattern.</param>
    public static bool HasWildcard(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal);
    }

    private static Regex Compile(string normalizedPattern) =>
        Cache.GetOrAdd(normalizedPattern, static p => new Regex(
            Translate(p),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern.AsSpan(i).StartsWith("**/", StringComparison.Ordinal))
            {
                // Zero or more whole segments.
                builder.Append("(?:[^/]+/)*");
                i += 3;
                continue;
            }

            if (i + 1 < pattern.Length && pattern[i] == '*' && pattern[i + 1] == '*')
            {
                builder.Append(".*");
                i += 2;
                continue;
            }

            switch (pattern[i])
            {
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(pattern[i].ToString()));
                    break;
            }

            i++;
        }

        builder.Append('$');
        return builder.ToString();
    }
}
