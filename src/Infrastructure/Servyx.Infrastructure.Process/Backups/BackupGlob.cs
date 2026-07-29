using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// Matches the <c>include</c>/<c>exclude</c> glob patterns a local backup definition declares against
/// root-relative paths.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a third copy rather than a shared type.</strong> Near-identical matchers live in
/// <c>Servyx.Infrastructure.Docker.Backups.BackupGlob</c> and
/// <c>Servyx.Infrastructure.Ssh.Backups.BackupGlob</c>. Infrastructure projects may not reference one
/// another, so the only home all three could share is <c>Servyx.Domain</c>. Nothing here does I/O, so
/// promoting it would not drag I/O into Domain — the blocker is purely that a promotion has to edit two
/// providers, an adopter, three DI extensions, a dozen test files and the web host's context sources in the
/// same change. That is a mechanical cross-project refactor deserving its own commit and its own
/// verification run, not a side effect of adding a third provider. <see cref="BackupGlob"/> and
/// <see cref="BackupRetentionEvaluator"/> remain the two pieces that are genuinely provider-agnostic policy
/// and the right candidates when that refactor happens.
/// </para>
/// <para>
/// This copy matches the <em>Docker</em> one rather than the smaller SSH one, because
/// <see cref="LocalProcessBackupProvider"/> walks the tree itself instead of delegating to a host's
/// <c>tar</c>. It therefore needs the walker-support surface — <see cref="ExcludesDirectory"/>,
/// <see cref="StaticPrefix"/>, <see cref="HasWildcard"/> — that the SSH copy has no use for.
/// </para>
/// <para>
/// <strong>Matching is ordinal and case-sensitive even on Windows.</strong> The alternative — folding case
/// on Windows and not on Linux — would make the same definition capture a different set of files depending
/// on which machine the panel happens to run on, and would let an exclude silently match more than its
/// author wrote. A definition is content, not a filesystem: it is matched the same way everywhere. Path
/// <em>separators</em> are normalised (<c>\</c> becomes <c>/</c>) so a Windows-shaped relative path still
/// matches a definition written in POSIX form; only the segment text is compared ordinally.
/// </para>
/// <para>
/// Supported syntax is the subset the schema uses: <c>**</c> spans any number of path segments, <c>*</c>
/// matches within a single segment, and <c>?</c> matches a single non-separator character. Patterns compile
/// to <see cref="RegexOptions.NonBacktracking"/> regexes so a definition author cannot author a
/// catastrophic-backtracking denial of service.
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
    /// Whether <paramref name="directoryPath"/> is wholly covered by one of <paramref name="patterns"/>, so
    /// that the walker can prune the entire subtree instead of descending into it and rejecting every file
    /// individually.
    /// </summary>
    /// <remarks>
    /// This is what makes "an archive never contains an archive" a property of the traversal rather than of a
    /// per-file filter: the Servyx artifact directory is excluded before a single directory listing is issued
    /// against it, so a store holding a thousand tarballs costs nothing to skip and can never contribute an
    /// entry by accident. The per-file exclude check still runs; this is an additional, stronger guarantee,
    /// not a replacement.
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
    /// The leading run of literal path segments before the first segment containing a wildcard — the deepest
    /// directory a walker can start from without missing a match.
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
