namespace Servyx.Domain.Configuration;

/// <summary>
/// Locates the exact character range of a single value within a <see cref="ConfigDocument"/>'s
/// <see cref="ConfigDocument.RawLines"/>, so an edit can splice in a replacement without disturbing
/// anything else on the line or in the file.
/// </summary>
/// <param name="Pointer">The pointer this span is the location of.</param>
/// <param name="LineIndex">Index into <see cref="ConfigDocument.RawLines"/> of the line this value lives on.</param>
/// <param name="ValueStart">Character offset within the line where the value's content begins (after any opening quote).</param>
/// <param name="ValueLength">Length, in characters, of the value's content (excluding any surrounding quotes).</param>
/// <param name="QuoteStyle">The quote character (<c>"</c> or <c>'</c>) surrounding the value in source, or <see langword="null"/> if unquoted.</param>
public sealed record ConfigSpan(ConfigPointer Pointer, int LineIndex, int ValueStart, int ValueLength, string? QuoteStyle);

/// <summary>
/// A parsed configuration document, as produced by an <see cref="IConfigAdapter"/>. <see cref="Root"/> is
/// an opaque, format-specific parse tree (its concrete shape is owned by the adapter that produced it) and
/// is a read-only convenience view only — it has no bearing on rendering. <see cref="RawLines"/>, together
/// with <see cref="LineEnding"/> and <see cref="HasTrailingNewline"/>, is authoritative for
/// <see cref="Render"/>: an adapter's <c>Parse</c> step must populate them so that
/// <c>Render(Parse(x)) == x</c> byte-for-byte for unmodified input, and the only supported way to change
/// what <see cref="Render"/> produces is <see cref="WithValue"/>, which edits exactly the characters a
/// <see cref="ConfigSpan"/> covers and nothing else.
/// </summary>
/// <param name="Root">The format-specific parsed representation.</param>
/// <param name="RawLines">
/// The original source, split into lines with line terminators removed. Authoritative for
/// <see cref="Render"/>; editing <see cref="Root"/> directly does not affect rendered output.
/// </param>
/// <param name="Spans">The location of every value the owning adapter recognized, keyed by <see cref="ConfigPointer"/>.</param>
/// <param name="LineEnding">
/// The line terminator to rejoin <see cref="RawLines"/> with (<c>"\n"</c> or <c>"\r\n"</c>). For a file
/// with mixed line endings this is the dominant terminator observed during parsing — a mixed-ending file
/// therefore normalizes to its dominant style on render rather than reproducing the mix, since guessing
/// per-line would be worse than a documented, deterministic choice.
/// </param>
/// <param name="HasTrailingNewline">Whether the original source ended with a line terminator.</param>
public sealed record ConfigDocument(
    object Root,
    IReadOnlyList<string> RawLines,
    IReadOnlyList<ConfigSpan> Spans,
    string LineEnding,
    bool HasTrailingNewline)
{
    /// <summary>
    /// Reconstructs the document's text from <see cref="RawLines"/>, <see cref="LineEnding"/>, and
    /// <see cref="HasTrailingNewline"/>. For a document fresh out of <see cref="IConfigAdapter.Parse"/>
    /// this reproduces the original source byte-for-byte; after one or more <see cref="WithValue"/> calls
    /// it reproduces the original source with only the edited spans changed.
    /// </summary>
    public string Render()
    {
        if (RawLines.Count == 0)
        {
            return string.Empty;
        }

        var body = string.Join(LineEnding, RawLines);
        return HasTrailingNewline ? body + LineEnding : body;
    }

    /// <summary>
    /// Returns a new <see cref="ConfigDocument"/> with the value at <paramref name="pointer"/> replaced by
    /// <paramref name="newValue"/>. Only the characters covered by that value's <see cref="ConfigSpan"/>
    /// change — every other character in <see cref="RawLines"/> (comments, ordering, unrelated values) is
    /// identical to the source. Other spans on the same line are shifted to account for any length
    /// difference between the old and new value.
    /// </summary>
    /// <remarks>
    /// When more than one span is registered for <paramref name="pointer"/> (a duplicate key), the last
    /// one — the occurrence that determines the effective value on read — is the one edited, consistent
    /// with "last wins" duplicate-key semantics.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No span is registered for <paramref name="pointer"/>.</exception>
    public ConfigDocument WithValue(ConfigPointer pointer, string newValue)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(newValue);

        var spanIndex = -1;
        for (var i = Spans.Count - 1; i >= 0; i--)
        {
            if (Spans[i].Pointer == pointer)
            {
                spanIndex = i;
                break;
            }
        }

        if (spanIndex < 0)
        {
            throw new KeyNotFoundException($"No span is registered for pointer '{pointer.Path}'.");
        }

        var span = Spans[spanIndex];
        var line = RawLines[span.LineIndex];
        var newLine = string.Concat(line.AsSpan(0, span.ValueStart), newValue, line.AsSpan(span.ValueStart + span.ValueLength));

        var newRawLines = RawLines.ToArray();
        newRawLines[span.LineIndex] = newLine;

        var delta = newValue.Length - span.ValueLength;
        var newSpans = new ConfigSpan[Spans.Count];
        for (var i = 0; i < Spans.Count; i++)
        {
            var s = Spans[i];
            if (i == spanIndex)
            {
                newSpans[i] = s with { ValueLength = newValue.Length };
            }
            else if (s.LineIndex == span.LineIndex && s.ValueStart > span.ValueStart)
            {
                newSpans[i] = s with { ValueStart = s.ValueStart + delta };
            }
            else
            {
                newSpans[i] = s;
            }
        }

        return this with { RawLines = newRawLines, Spans = newSpans };
    }
}
