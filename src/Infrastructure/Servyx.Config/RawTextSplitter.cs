namespace Servyx.Config;

/// <summary>
/// Splits raw source text into lines while recording exactly what it takes to put it back together
/// byte-for-byte: the dominant line-ending style and whether the source ended with a trailing newline.
/// Shared by every <c>IConfigAdapter</c> in this project so line-ending detection has one implementation.
/// </summary>
internal static class RawTextSplitter
{
    /// <summary>The result of splitting raw text into <see cref="ConfigDocument"/>-ready lines.</summary>
    /// <param name="Lines">The source, split into lines with any line terminator removed.</param>
    /// <param name="LineEnding">The dominant line terminator observed (<c>"\n"</c> or <c>"\r\n"</c>).</param>
    /// <param name="HasTrailingNewline">Whether the source text ended with a line terminator.</param>
    public readonly record struct Result(IReadOnlyList<string> Lines, string LineEnding, bool HasTrailingNewline);

    /// <summary>
    /// Splits <paramref name="raw"/> into lines. A file whose line endings are mixed is not corrupted by
    /// this pass — it is normalized to whichever of <c>\n</c>-only or <c>\r\n</c> occurs more often, and
    /// that choice is reported back via <see cref="Result.LineEnding"/> so callers can render consistently
    /// with what they parsed.
    /// </summary>
    public static Result Split(string raw)
    {
        if (raw.Length == 0)
        {
            return new Result([], "\n", false);
        }

        var crlfCount = 0;
        var lfOnlyCount = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n')
            {
                continue;
            }

            if (i > 0 && raw[i - 1] == '\r')
            {
                crlfCount++;
            }
            else
            {
                lfOnlyCount++;
            }
        }

        var lineEnding = crlfCount > lfOnlyCount ? "\r\n" : "\n";
        var hasTrailingNewline = raw[^1] == '\n';

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\n')
            {
                continue;
            }

            var end = i;
            if (end > start && raw[end - 1] == '\r')
            {
                end--;
            }

            lines.Add(raw[start..end]);
            start = i + 1;
        }

        if (start < raw.Length)
        {
            lines.Add(raw[start..]);
        }

        return new Result(lines, lineEnding, hasTrailingNewline);
    }
}
