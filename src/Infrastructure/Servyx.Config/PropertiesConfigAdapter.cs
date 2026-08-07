using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>The parsed representation produced by <see cref="PropertiesConfigAdapter"/>.</summary>
/// <param name="Values">Every key's effective value; for a duplicate key this is the last occurrence's value.</param>
public sealed record PropertiesDocument(IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Parses and renders Java <c>.properties</c>-style <c>key=value</c> files — the format Minecraft's
/// <c>server.properties</c> uses (<c>definitions/minecraft-itzg.yaml</c>'s <c>properties</c> surface).
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="DotEnvConfigAdapter"/> even though both are flat key/value text:
/// this format has no <c>export</c> prefix, no quoting (a value runs to the end of the line, verbatim — a
/// literal <c>"</c> or <c>'</c> in a value is not a delimiter), allows dotted keys
/// (<c>rcon.password</c>, <c>rcon.port</c> are real <c>server.properties</c> keys), and treats <c>!</c> as a
/// second comment marker alongside <c>#</c> — all genuine differences from <c>.env</c> convention. See the
/// remarks on <see cref="Servyx.Domain.Definitions.Model.SurfaceFormat.Properties"/> for why this earns its
/// own format id rather than being folded into <c>dotenv</c>.
/// </remarks>
public sealed class PropertiesConfigAdapter : IConfigAdapter
{
    /// <inheritdoc />
    public string FormatId => "properties";

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

        return new ConfigDocument(new PropertiesDocument(values), split.Lines, spans, split.LineEnding, split.HasTrailingNewline);
    }

    /// <inheritdoc />
    public string Render(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Render();
    }

    /// <summary>
    /// Recognizes a single <c>key=value</c> line. Returns <see langword="null"/> for blank lines, full-line
    /// comments (<c>#</c> or <c>!</c> as the first non-whitespace character), and any line that doesn't
    /// parse as a key/value pair — all passthrough content already preserved verbatim in
    /// <see cref="ConfigDocument.RawLines"/>. Unlike <see cref="DotEnvConfigAdapter"/>, a value is never
    /// quote-stripped and runs to the end of the line (trailing whitespace trimmed) — there is no inline
    /// comment syntax in this format, so a <c>#</c> after a value is part of the value, not a comment.
    /// </summary>
    private static (string Key, ConfigSpan Span)? ParseLine(string line, int lineIndex, int bomOffset)
    {
        var n = line.Length;
        var i = bomOffset;

        i = SkipInlineWhitespace(line, i);
        if (i >= n || line[i] is '#' or '!')
        {
            return null;
        }

        var keyStart = i;
        while (i < n && line[i] != '=' && line[i] != ':' && !char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (i == keyStart)
        {
            return null;
        }

        var key = line[keyStart..i];

        i = SkipInlineWhitespace(line, i);
        if (i >= n || (line[i] != '=' && line[i] != ':'))
        {
            return null;
        }

        i++; // consume the separator
        i = SkipInlineWhitespace(line, i);

        var valueEnd = n;
        while (valueEnd > i && char.IsWhiteSpace(line[valueEnd - 1]))
        {
            valueEnd--;
        }

        return (key, new ConfigSpan(new ConfigPointer(key), lineIndex, i, valueEnd - i, QuoteStyle: null));
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
