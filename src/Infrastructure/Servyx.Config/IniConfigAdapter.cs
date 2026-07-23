using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>The parsed representation produced by <see cref="IniConfigAdapter"/>.</summary>
/// <param name="Sections">
/// Every section's effective key/value pairs, keyed by section name. For a duplicate section, entries are
/// merged in file order (later occurrences of the same key win); this is a read-only convenience view —
/// <see cref="ConfigDocument.RawLines"/> preserves every duplicate section and line exactly as written.
/// </param>
public sealed record IniDocument(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections);

/// <summary>
/// Parses and renders classic <c>.ini</c> files: <c>[Section]</c> headers, <c>key=value</c> pairs,
/// <c>;</c>- and <c>#</c>-style comments, preserved ordering, duplicate sections, and values that
/// themselves contain <c>=</c> (only the first <c>=</c> on a line splits key from value).
/// </summary>
public sealed class IniConfigAdapter : IConfigAdapter
{
    /// <inheritdoc />
    public string FormatId => "ini";

    /// <inheritdoc />
    public bool PreservesComments => true;

    /// <inheritdoc />
    public ConfigDocument Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var split = RawTextSplitter.Split(raw);
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var spans = new List<ConfigSpan>();
        var currentSection = string.Empty;

        for (var lineIndex = 0; lineIndex < split.Lines.Count; lineIndex++)
        {
            var line = split.Lines[lineIndex];
            var bomOffset = lineIndex == 0 && line.Length > 0 && line[0] == (char)0xFEFF ? 1 : 0;

            var i = SkipInlineWhitespace(line, bomOffset);
            if (i >= line.Length || line[i] is ';' or '#')
            {
                continue;
            }

            if (line[i] == '[')
            {
                var close = line.IndexOf(']', i + 1);
                if (close > i)
                {
                    currentSection = line[(i + 1)..close];
                    if (!sections.ContainsKey(currentSection))
                    {
                        sections[currentSection] = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                }

                continue;
            }

            var equals = line.IndexOf('=', i);
            if (equals < 0)
            {
                continue;
            }

            var key = line[i..equals].TrimEnd(' ', '\t');
            if (key.Length == 0)
            {
                continue;
            }

            var pointer = new ConfigPointer($"[{currentSection}].{key}");
            var valueStart = equals + 1;

            ConfigSpan span;
            if (valueStart < line.Length && line[valueStart] is '"' or '\'')
            {
                var quote = line[valueStart];
                var innerStart = valueStart + 1;
                var closing = line.IndexOf(quote, innerStart);
                var innerEnd = closing < 0 ? line.Length : closing;
                span = new ConfigSpan(pointer, lineIndex, innerStart, innerEnd - innerStart, quote.ToString());
            }
            else
            {
                span = new ConfigSpan(pointer, lineIndex, valueStart, line.Length - valueStart, null);
            }

            spans.Add(span);

            if (!sections.TryGetValue(currentSection, out var section))
            {
                section = new Dictionary<string, string>(StringComparer.Ordinal);
                sections[currentSection] = section;
            }

            section[key] = line.Substring(span.ValueStart, span.ValueLength);
        }

        var sectionsView = sections.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.Ordinal);

        return new ConfigDocument(new IniDocument(sectionsView), split.Lines, spans, split.LineEnding, split.HasTrailingNewline);
    }

    /// <inheritdoc />
    public string Render(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Render();
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
