using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>The parsed representation produced by <see cref="DotEnvConfigAdapter"/>.</summary>
/// <param name="Values">Every key's effective value; for a duplicate key this is the last occurrence's value.</param>
public sealed record DotEnvDocument(IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Parses and renders <c>.env</c>-style <c>KEY=VALUE</c> files, preserving comments, blank lines,
/// <c>export </c> prefixes, single- and double-quoted values, inline comments, and duplicate keys (the
/// last occurrence wins for reads; every occurrence is preserved verbatim on render).
/// </summary>
public sealed class DotEnvConfigAdapter : IConfigAdapter
{
    /// <inheritdoc />
    public string FormatId => "dotenv";

    /// <inheritdoc />
    public bool PreservesComments => true;

    /// <inheritdoc />
    public ConfigDocument Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var split = RawTextSplitter.Split(raw);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var spans = new List<ConfigSpan>();

        for (var lineIndex = 0; lineIndex < split.Lines.Count; lineIndex++)
        {
            var line = split.Lines[lineIndex];
            var bomOffset = lineIndex == 0 && line.Length > 0 && line[0] == (char)0xFEFF ? 1 : 0;

            var parsed = ParseLine(line, lineIndex, bomOffset);
            if (parsed is null)
            {
                continue;
            }

            var (key, span) = parsed.Value;
            values[key] = line.Substring(span.ValueStart, span.ValueLength);
            spans.Add(span);
        }

        return new ConfigDocument(new DotEnvDocument(values), split.Lines, spans, split.LineEnding, split.HasTrailingNewline);
    }

    /// <inheritdoc />
    public string Render(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Render();
    }

    /// <summary>
    /// Recognizes a single <c>KEY=VALUE</c> line (with an optional leading <c>export </c>) and locates its
    /// value's span. Returns <see langword="null"/> for blank lines, full-line comments, and any line that
    /// doesn't parse as a key/value pair — all of which are passthrough content, already preserved
    /// verbatim in <see cref="ConfigDocument.RawLines"/> with no span needed.
    /// </summary>
    private static (string Key, ConfigSpan Span)? ParseLine(string line, int lineIndex, int bomOffset)
    {
        var n = line.Length;
        var i = bomOffset;

        i = SkipInlineWhitespace(line, i);
        if (i >= n || line[i] == '#')
        {
            return null;
        }

        if (line.AsSpan(i).StartsWith("export ") || line.AsSpan(i).StartsWith("export\t"))
        {
            i += "export".Length;
            i = SkipInlineWhitespace(line, i);
        }

        var keyStart = i;
        while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
        {
            i++;
        }

        if (i == keyStart)
        {
            return null;
        }

        var key = line[keyStart..i];

        i = SkipInlineWhitespace(line, i);
        if (i >= n || line[i] != '=')
        {
            return null;
        }

        i++; // consume '='

        if (i < n && line[i] is '"' or '\'')
        {
            var quote = line[i];
            var valueStart = i + 1;
            var closing = line.IndexOf(quote, valueStart);
            var valueEnd = closing < 0 ? n : closing;
            return (key, new ConfigSpan(new ConfigPointer(key), lineIndex, valueStart, valueEnd - valueStart, quote.ToString()));
        }

        return (key, new ConfigSpan(new ConfigPointer(key), lineIndex, i, UnquotedValueLength(line, i), null));
    }

    /// <summary>
    /// Computes the length of an unquoted value starting at <paramref name="valueStart"/>, stopping before
    /// an inline comment (a <c>#</c> preceded by whitespace, or a <c>#</c> at the very start of the value)
    /// and trimming trailing whitespace immediately before that stop point.
    /// </summary>
    private static int UnquotedValueLength(string line, int valueStart)
    {
        var n = line.Length;
        var end = n;

        if (valueStart < n && line[valueStart] == '#')
        {
            return 0;
        }

        for (var k = valueStart + 1; k < n; k++)
        {
            if (line[k] == '#' && (line[k - 1] == ' ' || line[k - 1] == '\t'))
            {
                end = k;
                break;
            }
        }

        var valueEnd = end;
        while (valueEnd > valueStart && (line[valueEnd - 1] == ' ' || line[valueEnd - 1] == '\t'))
        {
            valueEnd--;
        }

        return valueEnd - valueStart;
    }

    private static int SkipInlineWhitespace(string line, int i)
    {
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }
}
