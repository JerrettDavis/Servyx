using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// Matches the <c>include</c>/<c>exclude</c> glob patterns a game definition's <c>backup:</c> block
/// declares against root-relative target paths.
/// </summary>
/// <remarks>
/// <para>
/// Supported syntax is deliberately the subset the schema actually uses: <c>**</c> spans any number of
/// path segments, <c>*</c> matches within a single segment, and <c>?</c> matches a single non-separator
/// character. Everything else is literal. Matching is ordinal and case-sensitive, because the targets
/// these patterns are evaluated against are Linux containers, where <c>Backups</c> and <c>backups</c> are
/// two different directories — folding case here would let an exclude silently match more than the
/// definition author wrote.
/// </para>
/// <para>
/// Patterns are compiled to <see cref="RegexOptions.NonBacktracking"/> regexes and cached. Definitions
/// are semi-trusted content (an author may publish one), so a pattern must not be able to turn into a
/// catastrophic-backtracking denial of service; the non-backtracking engine makes that structurally
/// impossible rather than relying on a timeout to notice.
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

    /// <summary>
    /// Whether <paramref name="directoryPath"/> is wholly covered by one of <paramref name="patterns"/>,
    /// so that the walker can prune the entire subtree instead of descending into it and rejecting every
    /// file individually.
    /// </summary>
    /// <remarks>
    /// This is what makes "never re-archive the image's own archives" a property of the traversal rather
    /// than of a per-file filter: the definition's <c>${DATA_DIR}/backups/**</c> exclusion prunes
    /// <c>backups</c> before a single directory listing is issued against it, so a backup directory
    /// holding a thousand tarballs costs nothing and can never contribute an entry by accident. The
    /// per-file exclude check still runs; this is an additional, stronger guarantee, not a replacement.
    /// </remarks>
    /// <param name="patterns">The exclude patterns.</param>
    /// <param name="directoryPath">A root-relative directory path.</param>
    public static bool ExcludesDirectory(IEnumerable<string> patterns, string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(directoryPath);

        var normalizedPath = Normalize(directoryPath);
        if (normalizedPath.Length == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            var normalizedPattern = Normalize(pattern);

            // "logs" excludes the directory "logs" outright.
            if (Compile(normalizedPattern).IsMatch(normalizedPath))
            {
                return true;
            }

            // "backups/**" excludes everything below "backups", which is the same thing as excluding the
            // directory for traversal purposes.
            if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
            {
                var trimmed = normalizedPattern[..^3];
                if (trimmed.Length > 0 && Compile(trimmed).IsMatch(normalizedPath))
                {
                    return true;
                }
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

    /// <summary>
    /// The leading run of literal path segments before the first segment containing a wildcard — the
    /// deepest directory a walker can start from without missing a match.
    /// </summary>
    /// <param name="pattern">The glob pattern.</param>
    /// <returns>A root-relative directory path, or the empty string when the pattern wildcards from the root.</returns>
    public static string StaticPrefix(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var segments = Normalize(pattern).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var literal = new List<string>();
        foreach (var segment in segments)
        {
            if (HasWildcard(segment))
            {
                break;
            }

            literal.Add(segment);
        }

        return string.Join('/', literal);
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
