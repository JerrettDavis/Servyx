using System.Globalization;
using Servyx.Domain.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Servyx.Config;

/// <summary>
/// The source presentation style of a scalar recognized by <see cref="YamlConfigAdapter"/>.
/// </summary>
/// <remarks>
/// Deliberately this project's own enum rather than a re-export of <c>YamlDotNet.Core.ScalarStyle</c>:
/// <see cref="YamlConfigDocument"/> is part of <c>Servyx.Config</c>'s public surface, and leaking a
/// YamlDotNet type through it would force every consumer that merely reads a parsed document — including
/// projects that have no other reason to know YAML exists — to take a package reference. The equivalent
/// leak is acceptable for <c>JsonConfigAdapter</c> only because <see cref="System.Text.Json.JsonValueKind"/>
/// ships in the base class library.
/// </remarks>
public enum YamlScalarStyle
{
    /// <summary>An unquoted scalar, e.g. <c>unless-stopped</c> or <c>8211:8211/udp</c>.</summary>
    Plain,

    /// <summary>A <c>'single-quoted'</c> scalar.</summary>
    SingleQuoted,

    /// <summary>A <c>"double-quoted"</c> scalar.</summary>
    DoubleQuoted,

    /// <summary>A literal block scalar introduced by <c>|</c>.</summary>
    Literal,

    /// <summary>A folded block scalar introduced by <c>&gt;</c>.</summary>
    Folded,
}

/// <summary>A single scalar YAML value recognized by <see cref="YamlConfigAdapter"/>.</summary>
/// <param name="Style">The scalar's source presentation style.</param>
/// <param name="Raw">
/// The value exactly as it appears in source, <i>including</i> any surrounding quotes or block-scalar
/// indicator and its indented body. This is the whole source extent YamlDotNet attributes to the scalar, not
/// the narrower range the value's <see cref="ConfigSpan"/> covers — see <see cref="IsAddressable"/>.
/// </param>
/// <param name="Text">
/// The value as text, with quoting removed and escape sequences decoded — what a reader of the configuration
/// actually sees. For a block scalar this is the folded/literal body YAML resolves it to.
/// </param>
/// <param name="IsAddressable">
/// Whether a <see cref="ConfigSpan"/> was registered for this value, i.e. whether
/// <see cref="ConfigDocument.WithValue"/> can write to it. <see langword="false"/> for every scalar this
/// adapter can read but deliberately refuses to write — block scalars, multi-line plain scalars, and
/// valueless keys. A caller that wants to know a write will fail <i>before</i> attempting it should consult
/// this rather than catching <see cref="KeyNotFoundException"/>.
/// </param>
public sealed record YamlScalarValue(YamlScalarStyle Style, string Raw, string Text, bool IsAddressable);

/// <summary>The parsed representation produced by <see cref="YamlConfigAdapter"/>.</summary>
/// <param name="Values">
/// Every scalar value in the document, keyed by its RFC 6901 JSON pointer
/// (<c>/services/palworld/ports/0</c>, <c>""</c> for a scalar document root) — the same addressing scheme
/// <see cref="JsonConfigAdapter"/> uses, so a <c>SettingBinding.ByPointer</c> reads identically against a
/// <c>yaml</c> and a <c>json</c> surface. This is a read-only convenience view and is deliberately
/// <i>wider</i> than <see cref="ConfigDocument.Spans"/>: it includes scalars that can be read but not
/// written (see <see cref="YamlScalarValue.IsAddressable"/>).
/// </param>
public sealed record YamlConfigDocument(IReadOnlyDictionary<string, YamlScalarValue> Values);

/// <summary>
/// Parses and renders YAML configuration files — the format every shipped definition's <c>compose</c>
/// surface declares (<c>definitions/*.yaml</c>, <c>format: yaml</c>, a Docker Compose file) — addressing
/// every writable scalar by its RFC 6901 JSON pointer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-trip.</b> Like every other adapter here, this one never re-serializes. Parsing records the exact
/// character range each writable scalar occupies and a write splices over that range only, so comments,
/// blank lines, key order, indentation, anchors, block scalars, and every key the tool does not model
/// survive a write untouched — none of those characters are ever rewritten. YamlDotNet is used purely as a
/// position-reporting reader; its emitter is never invoked, which is the only way to keep the byte-for-byte
/// contract against a format whose round-tripping serializers do not preserve comments.
/// </para>
/// <para>
/// <b>Quote boundaries are normalized, and the normalization is guarded.</b> YamlDotNet reports a quoted
/// scalar's position <i>including</i> its surrounding quotes; <see cref="ConfigSpan"/>'s contract is that the
/// span covers the value's content only, with the quote character recorded separately in
/// <see cref="ConfigSpan.QuoteStyle"/> — the convention <see cref="JsonConfigAdapter"/> already establishes.
/// This adapter therefore trims one character from each end of a quoted scalar's reported extent. Getting
/// that wrong is not a cosmetic bug: writing over the quotes of a Compose port entry turns the string
/// <c>"27015:27015/udp"</c> into a YAML mapping and silently changes what the file means. Because the
/// correction depends on undocumented reader behavior, <see cref="Parse"/> asserts that the character at the
/// reported start really is the expected quote and throws <see cref="FormatException"/> if it is not, so a
/// future YamlDotNet change fails a build rather than corrupting an operator's file.
/// </para>
/// <para>
/// <b>Only single-line scalars are writable.</b> A <see cref="ConfigSpan"/> carries one
/// <see cref="ConfigSpan.LineIndex"/> and <see cref="ConfigDocument.WithValue"/> rewrites exactly one entry
/// of <see cref="ConfigDocument.RawLines"/>; it can neither span nor add lines. Anything whose source extent
/// crosses a line break is therefore recorded in <see cref="YamlConfigDocument.Values"/> as readable but
/// gets no span: literal (<c>|</c>) and folded (<c>&gt;</c>) block scalars, and multi-line plain scalars. A
/// valueless key (<c>empty:</c>) is also left unwritable — its zero-length extent sits flush against the
/// colon, so splicing into it would emit <c>empty:x</c>, which YAML reads as the plain scalar
/// <c>"empty:x"</c> rather than a mapping. An explicitly empty <i>quoted</i> value (<c>key: ""</c>) has real
/// quotes to write between and stays addressable.
/// </para>
/// <para>
/// <b>Collections are not addressable; their scalar elements are.</b> Following
/// <see cref="JsonConfigAdapter"/>, a mapping or sequence node gets no span of its own — only the scalars
/// inside it do. A Compose ports list is reachable element-by-element
/// (<c>/services/palworld/ports/0</c>), while the list itself (<c>/services/palworld/ports</c>) resolves to
/// no span and makes <see cref="ConfigDocument.WithValue"/> throw <see cref="KeyNotFoundException"/> naming
/// the pointer. That is the correct outcome, not a gap to paper over: publishing a port means adding or
/// removing a list <i>element</i>, which changes the file's line count, and the one-line-splice invariant
/// that the whole fidelity contract rests on cannot express it. The shipped definitions' <c>strategy:
/// publish-udp</c> / <c>publish-tcp</c> bindings consequently remain unappliable until a strategy layer
/// above <c>IConfigMerger</c> resolves such a binding into a concrete element pointer at plan time.
/// </para>
/// <para>
/// <b>An aliased node is addressed once, at its anchor.</b> YamlDotNet resolves an alias
/// (<c>*name</c>) and a merge key (<c>&lt;&lt;:</c>) to the <i>same node instance</i> as the anchor it
/// refers to, reporting the anchor's source position for both. Registering a span under both pointers would
/// mean a write through one silently rewrote the other, so the walk tracks visited nodes by reference
/// identity and records a scalar exactly once — under the pointer where the anchor is defined. Pointers that
/// reach a value only through an alias are absent from both <see cref="YamlConfigDocument.Values"/> and
/// <see cref="ConfigDocument.Spans"/>. Compose files use <c>x-common: &amp;common</c> and <c>&lt;&lt;:</c>
/// routinely, so this is a real hazard rather than a theoretical one.
/// </para>
/// <para>
/// <b>Deliberate divergences from the sibling adapters.</b> Duplicate keys are rejected, not resolved
/// last-wins: YamlDotNet's representation model throws on them while loading, and working around that would
/// mean hand-rolling a second YAML parser purely to reproduce a permissive behavior the format itself does
/// not sanction. A multi-document stream (<c>---</c>) is likewise rejected, because a single flat pointer
/// space cannot unambiguously address two roots and Compose does not use them. An <i>empty</i> or
/// comments-only file, by contrast, is accepted — unlike JSON, that is valid YAML (a stream of zero
/// documents), and refusing it would break the round-trip contract for a legitimately empty surface.
/// </para>
/// <para>
/// <b>Untrusted input is depth-checked before the reader sees it.</b> YamlDotNet's scanner is
/// recursive-descent, and a few kilobytes of deeply nested input can overflow the process stack — a
/// <see cref="StackOverflowException"/> is uncatchable in .NET and takes the host down, so no
/// <c>try</c>/<c>catch</c> downstream can defend against it. <see cref="Parse"/> therefore runs its own
/// bounded pre-scan first, the same defense <c>Servyx.Definitions.SafeYamlLoader</c> applies to definition
/// files. That type is <c>internal</c> to a project this one does not reference, so the check is
/// reimplemented here rather than shared — and, unlike the original, this one understands block scalars, so
/// a Compose file with a deeply indented <c>command: |</c> body is not mistaken for pathological nesting.
/// </para>
/// </remarks>
public sealed class YamlConfigAdapter : IConfigAdapter
{
    /// <summary>
    /// The maximum combined structural nesting depth accepted — indentation levels, open flow collections
    /// (<c>[</c>/<c>{</c>), and chained block-sequence dashes (<c>- - - -</c>) all count toward one total,
    /// because each costs the same single recursion frame in YamlDotNet's scanner. Matches the limit
    /// <c>Servyx.Definitions.SafeYamlLoader</c> settles on for the same reason: comfortably above anything a
    /// real Compose file needs (the deepest shipped definition measures 7 under this metric) and comfortably
    /// below the depth at which the scanner overflows the stack.
    /// </summary>
    private const int MaxStructuralNestingDepth = 100;

    /// <inheritdoc />
    public string FormatId => "yaml";

    /// <summary>
    /// Always <see langword="true"/>. Comments survive because the rendered output is the original text with
    /// only the edited spans replaced — the adapter never re-emits the document from its parse tree.
    /// </summary>
    public bool PreservesComments => true;

    /// <inheritdoc />
    /// <exception cref="FormatException">
    /// <paramref name="raw"/> is not well-formed YAML, nests more deeply than
    /// <see cref="MaxStructuralNestingDepth"/>, contains duplicate keys, or contains more than one document.
    /// The message carries the 1-based line and column of the offending position where the reader supplies
    /// one.
    /// </exception>
    public ConfigDocument Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var split = RawTextSplitter.Split(raw);
        EnsureNestingDepthIsSupported(split.Lines);

        var stream = Load(raw);

        var values = new Dictionary<string, YamlScalarValue>(StringComparer.Ordinal);
        var spans = new List<ConfigSpan>();

        // A stream with no documents is an empty or comments-only file: valid YAML, nothing to address.
        if (stream.Documents.Count > 1)
        {
            var second = stream.Documents[1].RootNode;
            throw Error(
                (int)second.Start.Line,
                (int)second.Start.Column,
                "this file contains more than one YAML document ('---'), which cannot be addressed by a "
                + "single flat pointer space");
        }

        if (stream.Documents.Count == 1)
        {
            new Walker(raw, split.Lines, values, spans).Walk(stream.Documents[0].RootNode, string.Empty);
        }

        return new ConfigDocument(new YamlConfigDocument(values), split.Lines, spans, split.LineEnding, split.HasTrailingNewline);
    }

    /// <inheritdoc />
    public string Render(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Render();
    }

    /// <summary>
    /// Loads <paramref name="raw"/> into a <see cref="YamlStream"/>, translating every reader failure —
    /// malformed syntax, a duplicate key — into a <see cref="FormatException"/> carrying the 1-based
    /// position, the convention <see cref="JsonConfigAdapter"/> establishes for this interface.
    /// </summary>
    /// <remarks>
    /// The text is handed over byte-for-byte, including any leading BOM. Stripping it first would shift every
    /// <c>Mark.Index</c> by one relative to <see cref="RawTextSplitter"/>'s line list, which keeps the BOM in
    /// line 0, and span offsets are only safe while the reader and the line list agree on what the source is.
    /// The BOM is instead removed from the affected key's <i>pointer</i> in <see cref="Walker"/>, which
    /// changes no offset.
    /// </remarks>
    private static YamlStream Load(string raw)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(raw));
            return stream;
        }
        catch (YamlException ex)
        {
            throw Error((int)ex.Start.Line, (int)ex.Start.Column, ex.Message.TrimEnd('.'));
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            throw Error(1, 1, ex.Message.TrimEnd('.'));
        }
    }

    private static FormatException Error(int line, int column, string message) =>
        new($"Invalid YAML at line {line.ToString(CultureInfo.InvariantCulture)}, column {column.ToString(CultureInfo.InvariantCulture)}: {message}.");

    /// <summary>
    /// Rejects input whose structural nesting could overflow YamlDotNet's recursive-descent scanner, before
    /// a single character of it reaches that scanner.
    /// </summary>
    /// <remarks>
    /// Line-oriented rather than character-oriented so that block scalars can be recognized and skipped:
    /// a <c>command: |</c> body is inert text that costs the scanner no recursion at all, however deeply it
    /// happens to be indented, and counting its indentation as nesting would reject ordinary Compose files.
    /// Like any pre-scan this is a heuristic and not a second YAML parser — it tracks quoting and comments
    /// only well enough not to mis-measure nesting inside string content.
    /// </remarks>
    private static void EnsureNestingDepthIsSupported(IReadOnlyList<string> lines)
    {
        var indentStack = new List<int>();
        var flowDepth = 0;
        var inBlockScalar = false;
        var blockScalarParentIndent = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var indent = IndentOf(line);

            if (inBlockScalar)
            {
                // A blank line, or any line indented past the header, is still block-scalar body.
                if (indent < 0 || indent > blockScalarParentIndent)
                {
                    continue;
                }

                inBlockScalar = false;
            }

            if (indent < 0)
            {
                continue;
            }

            if (flowDepth == 0)
            {
                if (line[indent] == '#')
                {
                    continue;
                }

                // Standard "pop while shallower-or-equal, then push": one block level per indentation step.
                while (indentStack.Count > 0 && indent <= indentStack[^1])
                {
                    indentStack.RemoveAt(indentStack.Count - 1);
                }

                indentStack.Add(indent);
            }

            var scan = ScanLine(line, indent, ref flowDepth);

            var combined = indentStack.Count + scan.ExtraDashes + flowDepth;
            if (combined > MaxStructuralNestingDepth)
            {
                throw Error(
                    lineIndex + 1,
                    indent + 1,
                    "the document's structural nesting (indentation, flow collections '[]'/'{}', and/or "
                    + $"chained block-sequence '-' entries) exceeds the maximum supported depth of "
                    + $"{MaxStructuralNestingDepth.ToString(CultureInfo.InvariantCulture)}, and is rejected "
                    + "before parsing to avoid a stack overflow in the underlying YAML scanner");
            }

            if (scan.OpensBlockScalar && flowDepth == 0)
            {
                inBlockScalar = true;
                blockScalarParentIndent = indent;
            }
        }
    }

    /// <summary>The offset of a line's first non-whitespace character, or <c>-1</c> when the line is blank.</summary>
    private static int IndentOf(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] is not (' ' or '\t'))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Walks one line, updating <paramref name="flowDepth"/> for brackets opened or closed on it, and
    /// reporting how many chained <c>-</c> tokens it leads with and whether it ends by opening a block
    /// scalar.
    /// </summary>
    private static (int ExtraDashes, bool OpensBlockScalar) ScanLine(string line, int indent, ref int flowDepth)
    {
        var dashTokens = 0;
        var i = indent;

        // "- - - value": the first dash is already covered by this line's indentation push; each further one
        // is compact extra nesting at the same column.
        while (i < line.Length && line[i] == '-' && (i + 1 >= line.Length || line[i + 1] is ' ' or '\t'))
        {
            dashTokens++;
            i += 2;
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }
        }

        var lastTokenStart = -1;
        var lastTokenEnd = -1;
        var inSingle = false;
        var inDouble = false;
        var quotedSinceTokenStart = false;

        for (; i < line.Length; i++)
        {
            var c = line[i];

            if (inSingle)
            {
                if (c == '\'')
                {
                    // '' is an escaped quote inside a single-quoted scalar, not a terminator.
                    if (i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    inSingle = false;
                }

                continue;
            }

            if (inDouble)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inDouble = false;
                }

                continue;
            }

            if (c is ' ' or '\t')
            {
                continue;
            }

            // An unquoted '#' preceded by whitespace starts a comment; the rest of the line is inert.
            if (c == '#' && i > 0 && line[i - 1] is ' ' or '\t')
            {
                break;
            }

            if (lastTokenEnd <= i && (i == 0 || line[i - 1] is ' ' or '\t'))
            {
                lastTokenStart = i;
                quotedSinceTokenStart = false;
            }

            lastTokenEnd = i + 1;

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    quotedSinceTokenStart = true;
                    break;
                case '"':
                    inDouble = true;
                    quotedSinceTokenStart = true;
                    break;
                case '[' or '{':
                    flowDepth++;
                    break;
                case ']' or '}':
                    if (flowDepth > 0)
                    {
                        flowDepth--;
                    }

                    break;
                default:
                    break;
            }
        }

        var opensBlockScalar = !quotedSinceTokenStart
            && lastTokenStart >= 0
            && lastTokenEnd == line.TrimEnd().Length
            && IsBlockScalarHeader(line.AsSpan(lastTokenStart, lastTokenEnd - lastTokenStart));

        return (Math.Max(0, dashTokens - 1), opensBlockScalar);
    }

    /// <summary>
    /// Whether a whitespace-delimited token is a block-scalar header: <c>|</c> or <c>&gt;</c>, optionally
    /// followed by a chomping indicator and/or an explicit indentation indicator (<c>|-</c>, <c>&gt;2</c>,
    /// <c>|+2</c>). A token such as <c>foo|</c> is a plain scalar that merely ends in a pipe, not a header.
    /// </summary>
    private static bool IsBlockScalarHeader(ReadOnlySpan<char> token)
    {
        if (token.Length == 0 || token[0] is not ('|' or '>'))
        {
            return false;
        }

        for (var i = 1; i < token.Length; i++)
        {
            if (token[i] is not ('+' or '-' or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks a loaded document's representation model, recording every scalar under its RFC 6901 pointer and
    /// registering a <see cref="ConfigSpan"/> for each one that can be written by a single-line splice.
    /// </summary>
    private sealed class Walker(
        string raw,
        IReadOnlyList<string> lines,
        Dictionary<string, YamlScalarValue> values,
        List<ConfigSpan> spans)
    {
        /// <summary>
        /// Nodes already recorded, compared by reference. An alias or merge key resolves to the very same
        /// instance as its anchor, so this is what stops one source location from being registered under two
        /// pointers — which would make a write through one of them silently rewrite the other.
        /// </summary>
        private readonly HashSet<YamlNode> _visited = new(ReferenceEqualityComparer.Instance);

        /// <summary>Records <paramref name="node"/> and everything beneath it under <paramref name="pointer"/>.</summary>
        public void Walk(YamlNode node, string pointer)
        {
            if (!_visited.Add(node))
            {
                return;
            }

            switch (node)
            {
                case YamlScalarNode scalar:
                    Record(pointer, scalar);
                    break;

                case YamlMappingNode mapping:
                    foreach (var pair in mapping.Children)
                    {
                        // A complex key ('? [a, b]') has no reference-token spelling, so the entry it
                        // introduces is left unaddressed rather than given an invented pointer.
                        if (pair.Key is not YamlScalarNode { Value: { } name })
                        {
                            continue;
                        }

                        Walk(pair.Value, $"{pointer}/{EscapePointerToken(name)}");
                    }

                    break;

                case YamlSequenceNode sequence:
                    for (var i = 0; i < sequence.Children.Count; i++)
                    {
                        Walk(sequence.Children[i], $"{pointer}/{i.ToString(CultureInfo.InvariantCulture)}");
                    }

                    break;

                default:
                    break;
            }
        }

        private void Record(string pointer, YamlScalarNode scalar)
        {
            var style = MapStyle(scalar.Style);
            if (style is null)
            {
                return;
            }

            var startIndex = (int)scalar.Start.Index;
            var endIndex = Math.Min((int)scalar.End.Index, raw.Length);
            var sourceRaw = endIndex > startIndex ? raw[startIndex..endIndex] : string.Empty;

            var span = TryCreateSpan(pointer, scalar, style.Value);
            if (span is not null)
            {
                spans.Add(span);
            }

            values[pointer] = new YamlScalarValue(style.Value, sourceRaw, scalar.Value ?? string.Empty, span is not null);
        }

        /// <summary>
        /// Locates the writable character range of <paramref name="scalar"/>, or returns <see langword="null"/>
        /// when the value cannot be written by replacing characters on one line.
        /// </summary>
        private ConfigSpan? TryCreateSpan(string pointer, YamlScalarNode scalar, YamlScalarStyle style)
        {
            // A block scalar's extent covers its indicator and its whole indented body; a plain scalar may
            // fold across lines. Neither fits a single-LineIndex span, and WithValue rewrites one line.
            if (style is YamlScalarStyle.Literal or YamlScalarStyle.Folded)
            {
                return null;
            }

            var start = scalar.Start;
            var end = scalar.End;
            if (start.Line != end.Line)
            {
                return null;
            }

            var lineIndex = (int)start.Line - 1;
            if (lineIndex < 0 || lineIndex >= lines.Count)
            {
                return null;
            }

            var line = lines[lineIndex];
            var valueStart = (int)start.Column - 1;
            var byColumn = (int)(end.Column - start.Column);
            var byIndex = (int)(end.Index - start.Index);

            // Column and index deltas must agree for a scalar that begins and ends on one line; a
            // disagreement means the reader's two position systems have diverged and every offset below is
            // suspect. Refuse rather than splice at a guessed location.
            if (byColumn != byIndex)
            {
                throw Error(
                    (int)start.Line,
                    (int)start.Column,
                    $"the reader reported inconsistent extents for the scalar at '{pointer}' "
                    + $"({byColumn.ToString(CultureInfo.InvariantCulture)} columns vs "
                    + $"{byIndex.ToString(CultureInfo.InvariantCulture)} characters); refusing to write "
                    + "rather than risk splicing at the wrong offset");
            }

            if (valueStart < 0 || valueStart + byColumn > line.Length)
            {
                throw Error(
                    (int)start.Line,
                    (int)start.Column,
                    $"the reader placed the scalar at '{pointer}' outside the bounds of its own source line; "
                    + "refusing to write rather than risk splicing at the wrong offset");
            }

            var contentEnd = valueStart + byColumn;
            var contentStart = SkipNodeProperties(line, valueStart, contentEnd);
            if (contentStart < 0)
            {
                return null;
            }

            var contentLength = contentEnd - contentStart;

            if (style is YamlScalarStyle.Plain)
            {
                // A valueless key ('empty:') has a zero-length extent flush against the colon. Splicing there
                // would emit 'empty:x', which YAML reads as one plain scalar rather than a mapping entry.
                return contentLength == 0 ? null : new ConfigSpan(new ConfigPointer(pointer), lineIndex, contentStart, contentLength, null);
            }

            var quote = style is YamlScalarStyle.SingleQuoted ? '\'' : '"';

            // YamlDotNet reports a quoted scalar's extent INCLUDING both quotes, while ConfigSpan's contract
            // is content-only with the quote recorded separately. Verify the assumption before acting on it:
            // trimming a quote that is not there would shift the write one character into the value, and for
            // a Compose port entry losing the quotes turns a string into a mapping.
            if (contentLength < 2 || line[contentStart] != quote || line[contentStart + contentLength - 1] != quote)
            {
                throw Error(
                    (int)start.Line,
                    (int)start.Column,
                    $"expected the {style} scalar at '{pointer}' to be delimited by {quote} characters at the "
                    + "reported extent, but it is not — YamlDotNet's scalar position reporting has changed "
                    + "and this adapter refuses to write rather than corrupt the file");
            }

            return new ConfigSpan(new ConfigPointer(pointer), lineIndex, contentStart + 1, contentLength - 2, quote.ToString());
        }

        /// <summary>
        /// Advances past any node properties — an anchor (<c>&amp;name</c>) and/or a tag (<c>!tag</c>) — that
        /// the reader includes at the front of a scalar's reported extent, returning the offset where the
        /// value itself begins, or <c>-1</c> when nothing but properties is left.
        /// </summary>
        /// <remarks>
        /// A scalar written <c>port: &amp;p "8211"</c> is reported as starting at the <c>&amp;</c>, not at the
        /// opening quote, so without this the quote check below would reject a perfectly ordinary Compose
        /// file — anchored scalars are common in the <c>x-common: &amp;common</c> idiom. Neither <c>&amp;</c>
        /// nor <c>!</c> can begin an unquoted plain scalar in YAML (both are reserved indicators), so keying
        /// off those two characters cannot misfire on real content.
        /// </remarks>
        private static int SkipNodeProperties(string line, int start, int end)
        {
            var i = start;
            while (i < end && line[i] is '&' or '!')
            {
                var tokenEnd = i;
                while (tokenEnd < end && line[tokenEnd] is not (' ' or '\t'))
                {
                    tokenEnd++;
                }

                while (tokenEnd < end && line[tokenEnd] is ' ' or '\t')
                {
                    tokenEnd++;
                }

                // An anchor with no value after it ('key: &a') is a null scalar with nothing writable in it.
                if (tokenEnd >= end)
                {
                    return -1;
                }

                i = tokenEnd;
            }

            return i;
        }

        private static YamlScalarStyle? MapStyle(ScalarStyle style) => style switch
        {
            ScalarStyle.Plain => YamlScalarStyle.Plain,
            ScalarStyle.SingleQuoted => YamlScalarStyle.SingleQuoted,
            ScalarStyle.DoubleQuoted => YamlScalarStyle.DoubleQuoted,
            ScalarStyle.Literal => YamlScalarStyle.Literal,
            ScalarStyle.Folded => YamlScalarStyle.Folded,

            // ScalarStyle.Any never comes off a parse — it is the "let the emitter decide" value for nodes
            // built in memory. Anything unrecognized is left unaddressed rather than guessed at.
            _ => null,
        };

        /// <summary>
        /// Escapes one RFC 6901 reference token: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>, in
        /// that order. A leading byte-order mark is dropped first: YamlDotNet does not strip it, so it would
        /// otherwise ride along inside the first top-level key's name and make that key unaddressable by the
        /// pointer a definition author would write. Removing it here affects no span offset, because keys are
        /// never spanned.
        /// </summary>
        private static string EscapePointerToken(string name) =>
            name.TrimStart('﻿')
                .Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
    }
}
