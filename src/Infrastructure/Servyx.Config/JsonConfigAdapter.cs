using System.Globalization;
using System.Text;
using System.Text.Json;
using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>A single scalar JSON value recognized by <see cref="JsonConfigAdapter"/>.</summary>
/// <param name="Kind">
/// The value's native JSON type — <see cref="JsonValueKind.String"/>, <see cref="JsonValueKind.Number"/>,
/// <see cref="JsonValueKind.True"/>, <see cref="JsonValueKind.False"/>, or <see cref="JsonValueKind.Null"/>.
/// Containers (<c>{}</c> / <c>[]</c>) are not scalars and are never recorded here.
/// </param>
/// <param name="Raw">
/// The value exactly as it appears in source, minus the surrounding quotes for a string — i.e. the
/// characters the value's <see cref="ConfigSpan"/> covers. Escape sequences inside a string are left
/// encoded, so splicing this text back over the span reproduces the source byte-for-byte.
/// </param>
/// <param name="Text">
/// The value as text: a string with its escape sequences decoded, or the literal token for every other
/// kind (<c>42</c>, <c>1.5e3</c>, <c>true</c>, <c>false</c>, <c>null</c>). Pair this with
/// <see cref="Kind"/> when a caller needs to know whether <c>"true"</c> came from a JSON boolean or from a
/// quoted string.
/// </param>
public sealed record JsonScalar(JsonValueKind Kind, string Raw, string Text);

/// <summary>The parsed representation produced by <see cref="JsonConfigAdapter"/>.</summary>
/// <param name="Values">
/// Every scalar value in the document, keyed by its RFC 6901 JSON pointer (<c>/visibility/public</c>,
/// <c>/ports/0</c>, <c>""</c> for a scalar document root). For a duplicate property name this is the last
/// occurrence's value, matching the "last wins" read semantics of the other adapters in this project.
/// </param>
/// <remarks>
/// Named <c>JsonConfigDocument</c> rather than <c>JsonDocument</c> — the name the
/// <see cref="DotEnvDocument"/> / <see cref="IniDocument"/> / <see cref="PropertiesDocument"/> convention
/// would otherwise give it — purely to avoid colliding with <see cref="System.Text.Json.JsonDocument"/> at
/// every call site that happens to have <c>System.Text.Json</c> in scope.
/// </remarks>
public sealed record JsonConfigDocument(IReadOnlyDictionary<string, JsonScalar> Values);

/// <summary>
/// Parses and renders RFC 8259 JSON configuration files, addressing every scalar by its RFC 6901 JSON
/// pointer so that nested values — the shape a flat <c>key=value</c> format cannot express — are reachable
/// from a <c>SettingBinding.ByPointer</c> binding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Round-trip and typing.</b> Like every other adapter here, this one never re-serializes: parsing
/// records the exact character range each scalar occupies, and a write splices over that range only. Key
/// order, indentation, blank lines, the author's spacing around <c>:</c> and <c>,</c>, and every key the
/// tool does not model survive a write untouched, because none of those characters are ever rewritten.
/// A string's span deliberately excludes its quotes (<see cref="ConfigSpan.QuoteStyle"/> records them),
/// while a number/boolean/null span covers the bare token — so a write preserves each value's native JSON
/// type by construction: an integer field stays a JSON number and a string field stays a quoted string,
/// with no type inference and nothing to get wrong. Use <see cref="EscapeStringContent"/> to prepare a
/// value destined for a string span.
/// </para>
/// <para>
/// <b>Writing to a pointer whose parents do not exist: refuse, do not create.</b> Parsing registers a span
/// only for a scalar that is physically present in the source, so
/// <see cref="ConfigDocument.WithValue"/> against an absent pointer throws
/// <see cref="KeyNotFoundException"/> naming that pointer, rather than materializing the missing objects.
/// This is deliberate. Create-on-write would have to invent the very things this adapter exists to
/// preserve — where the new lines go, how deep to indent them, whether to add a trailing comma to the
/// sibling above — turning a targeted edit into a reflow of the operator's file. Worse, a game server
/// generally treats "key absent" and "key present with its default" as different states (absent means the
/// server picks, present pins the value across upgrades), so silently adding structure can change
/// behavior the operator never asked to change. A pointer that does not resolve is far more often a typo
/// in a definition's binding than an intent to extend the file, and a loud failure naming the pointer is
/// the actionable outcome in both cases: the fix is to add the key to the surface (or correct the
/// binding), not to have the tool guess.
/// </para>
/// <para>
/// <b>Strictness.</b> Input is validated against RFC 8259 — no comments, no trailing commas, no unquoted
/// property names, no leading zeros, no raw control characters inside strings. Malformed input throws
/// <see cref="FormatException"/> reporting the 1-based line and column and what was expected; it never
/// degrades into an empty or partial document, which would read as "this file has no settings" and invite
/// a write that clobbers a file the tool failed to understand.
/// </para>
/// </remarks>
public sealed class JsonConfigAdapter : IConfigAdapter
{
    /// <inheritdoc />
    public string FormatId => "json";

    /// <summary>
    /// Always <see langword="false"/>: RFC 8259 has no comment syntax, so there are no comments to carry
    /// through a round-trip. Everything a JSON file <i>can</i> carry — key order, whitespace, indentation,
    /// and unmodeled keys — is preserved regardless; see the class remarks.
    /// </summary>
    public bool PreservesComments => false;

    /// <inheritdoc />
    /// <exception cref="FormatException">
    /// <paramref name="raw"/> is not well-formed JSON. The message carries the 1-based line and column of
    /// the offending character.
    /// </exception>
    public ConfigDocument Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var split = RawTextSplitter.Split(raw);
        var values = new Dictionary<string, JsonScalar>(StringComparer.Ordinal);
        var spans = new List<ConfigSpan>();

        // Only scalars actually present in the source get a span. That absence is the whole enforcement
        // mechanism for the "refuse, do not create" rule documented on this class: an edit targeting a
        // pointer whose parents were never written cannot find a span, so ConfigDocument.WithValue throws
        // instead of inventing lines, indentation, and separators this adapter has no basis to guess.
        new Scanner(raw, values, spans).ParseDocument();

        return new ConfigDocument(new JsonConfigDocument(values), split.Lines, spans, split.LineEnding, split.HasTrailingNewline);
    }

    /// <inheritdoc />
    public string Render(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Render();
    }

    /// <summary>
    /// Encodes <paramref name="value"/> for splicing into a string value's <see cref="ConfigSpan"/>, which
    /// covers the content <i>between</i> the quotes: escapes <c>"</c>, <c>\</c>, and control characters per
    /// RFC 8259 and leaves everything else — including non-ASCII text — as written.
    /// </summary>
    /// <remarks>
    /// JSON is the one format in this project where an unescaped splice can produce a syntactically invalid
    /// file, so the escaping step is exposed rather than left to each caller to remember. Number, boolean,
    /// and null spans need no equivalent: they take their literal token verbatim.
    /// </remarks>
    public static string EscapeStringContent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A single-pass RFC 8259 reader that tracks (line, column) alongside its character index so every
    /// scalar it recognizes can be recorded as a <see cref="ConfigSpan"/> into the same line list
    /// <see cref="RawTextSplitter"/> produces — the reason this is hand-written rather than delegating to
    /// <c>Utf8JsonReader</c>, whose positions are byte offsets into the whole document.
    /// </summary>
    private sealed class Scanner(string raw, Dictionary<string, JsonScalar> values, List<ConfigSpan> spans)
    {
        /// <summary>Guards against stack exhaustion from adversarially nested input; far deeper than any real config file.</summary>
        private const int MaxDepth = 64;

        private int _index;
        private int _line;
        private int _column;

        private bool AtEnd => _index >= raw.Length;

        private char Current => raw[_index];

        /// <summary>Reads exactly one top-level value, plus surrounding whitespace, and rejects anything after it.</summary>
        public void ParseDocument()
        {
            // A leading BOM stays in RawTextSplitter's first line, so it must be consumed (advancing the
            // column) rather than trimmed, or every span on line 0 would be off by one.
            if (!AtEnd && Current == '﻿')
            {
                Advance();
            }

            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("expected a JSON value but reached the end of the document (an empty file is not valid JSON)");
            }

            ParseValue(string.Empty, depth: 0);
            SkipWhitespace();

            if (!AtEnd)
            {
                throw Error($"unexpected character '{Current}' after the top-level JSON value");
            }
        }

        /// <summary>
        /// Consumes one character, keeping <see cref="_line"/> and <see cref="_column"/> aligned with
        /// <see cref="RawTextSplitter.Split"/>'s output: a <c>\r</c> that immediately precedes a <c>\n</c>
        /// is stripped from the split line, so it must not advance the column.
        /// </summary>
        private void Advance()
        {
            var c = raw[_index];
            if (c == '\n')
            {
                _line++;
                _column = 0;
            }
            else if (c != '\r' || _index + 1 >= raw.Length || raw[_index + 1] != '\n')
            {
                _column++;
            }

            _index++;
        }

        private void SkipWhitespace()
        {
            while (!AtEnd && Current is ' ' or '\t' or '\n' or '\r')
            {
                Advance();
            }
        }

        private void ParseValue(string pointer, int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error($"nesting deeper than {MaxDepth} levels is not supported");
            }

            switch (Current)
            {
                case '{':
                    ParseObject(pointer, depth);
                    return;
                case '[':
                    ParseArray(pointer, depth);
                    return;
                case '"':
                    var text = ReadString();
                    Record(pointer, JsonValueKind.String, text, "\"");
                    return;
                case 't':
                    ParseLiteral(pointer, "true", JsonValueKind.True);
                    return;
                case 'f':
                    ParseLiteral(pointer, "false", JsonValueKind.False);
                    return;
                case 'n':
                    ParseLiteral(pointer, "null", JsonValueKind.Null);
                    return;
                default:
                    if (Current == '-' || IsDigit(Current))
                    {
                        ParseNumber(pointer);
                        return;
                    }

                    throw Error($"expected a JSON value but found '{Current}'");
            }
        }

        private void ParseObject(string parentPointer, int depth)
        {
            Advance(); // '{'
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("unterminated object: expected a property name or '}'");
            }

            if (Current == '}')
            {
                Advance();
                return;
            }

            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("unterminated object: expected a quoted property name");
                }

                if (Current != '"')
                {
                    throw Error($"expected a quoted property name but found '{Current}'");
                }

                var name = ReadString();
                SkipWhitespace();
                if (AtEnd || Current != ':')
                {
                    throw Error($"expected ':' after property '{name.Text}'");
                }

                Advance(); // ':'
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error($"unterminated object: expected a value for property '{name.Text}'");
                }

                ParseValue($"{parentPointer}/{EscapePointerToken(name.Text)}", depth + 1);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("unterminated object: expected ',' or '}'");
                }

                if (Current == ',')
                {
                    Advance();
                    continue;
                }

                if (Current == '}')
                {
                    Advance();
                    return;
                }

                throw Error($"expected ',' or '}}' in an object but found '{Current}'");
            }
        }

        private void ParseArray(string parentPointer, int depth)
        {
            Advance(); // '['
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error("unterminated array: expected a value or ']'");
            }

            if (Current == ']')
            {
                Advance();
                return;
            }

            var elementIndex = 0;
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("unterminated array: expected a value");
                }

                ParseValue($"{parentPointer}/{elementIndex.ToString(CultureInfo.InvariantCulture)}", depth + 1);
                SkipWhitespace();
                if (AtEnd)
                {
                    throw Error("unterminated array: expected ',' or ']'");
                }

                if (Current == ',')
                {
                    Advance();
                    elementIndex++;
                    continue;
                }

                if (Current == ']')
                {
                    Advance();
                    return;
                }

                throw Error($"expected ',' or ']' in an array but found '{Current}'");
            }
        }

        /// <summary>
        /// Reads a quoted string, returning both its source text (escapes still encoded) and its decoded
        /// text, along with the location of the content between the quotes. A JSON string cannot contain a
        /// raw newline — an unescaped control character is rejected below — so the content is guaranteed to
        /// live on one line, which is what makes a single-line <see cref="ConfigSpan"/> sufficient.
        /// </summary>
        private Token ReadString()
        {
            var lineIndex = _line;
            Advance(); // opening quote
            var contentStart = _column;
            var rawStart = _index;
            var decoded = new StringBuilder();

            while (true)
            {
                if (AtEnd)
                {
                    throw Error("unterminated string literal");
                }

                var c = Current;
                if (c == '"')
                {
                    break;
                }

                if (c == '\\')
                {
                    Advance();
                    decoded.Append(ReadEscape());
                    continue;
                }

                if (c < ' ')
                {
                    throw Error($"unescaped control character U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)} in a string literal");
                }

                decoded.Append(c);
                Advance();
            }

            var token = new Token(raw[rawStart.._index], decoded.ToString(), lineIndex, contentStart, _column - contentStart);
            Advance(); // closing quote
            return token;
        }

        private string ReadEscape()
        {
            if (AtEnd)
            {
                throw Error("unterminated escape sequence in a string literal");
            }

            var c = Current;
            switch (c)
            {
                case '"': Advance(); return "\"";
                case '\\': Advance(); return "\\";
                case '/': Advance(); return "/";
                case 'b': Advance(); return "\b";
                case 'f': Advance(); return "\f";
                case 'n': Advance(); return "\n";
                case 'r': Advance(); return "\r";
                case 't': Advance(); return "\t";
                case 'u':
                    Advance();
                    var code = 0;
                    for (var i = 0; i < 4; i++)
                    {
                        if (AtEnd)
                        {
                            throw Error("truncated '\\u' escape sequence in a string literal");
                        }

                        var digit = HexValue(Current);
                        if (digit < 0)
                        {
                            throw Error($"invalid hexadecimal digit '{Current}' in a '\\u' escape sequence");
                        }

                        code = (code << 4) | digit;
                        Advance();
                    }

                    return ((char)code).ToString();
                default:
                    throw Error($"unsupported escape sequence '\\{c}' in a string literal");
            }
        }

        private void ParseNumber(string pointer)
        {
            var lineIndex = _line;
            var start = _column;
            var rawStart = _index;

            if (Current == '-')
            {
                Advance();
                if (AtEnd || !IsDigit(Current))
                {
                    throw Error("expected a digit after '-' in a number");
                }
            }

            if (Current == '0')
            {
                // RFC 8259 forbids leading zeros; a following digit falls out of the number and is then
                // reported as an unexpected character, which is the clearer message of the two.
                Advance();
            }
            else
            {
                while (!AtEnd && IsDigit(Current))
                {
                    Advance();
                }
            }

            if (!AtEnd && Current == '.')
            {
                Advance();
                if (AtEnd || !IsDigit(Current))
                {
                    throw Error("expected at least one digit after the decimal point in a number");
                }

                while (!AtEnd && IsDigit(Current))
                {
                    Advance();
                }
            }

            if (!AtEnd && (Current == 'e' || Current == 'E'))
            {
                Advance();
                if (!AtEnd && (Current == '+' || Current == '-'))
                {
                    Advance();
                }

                if (AtEnd || !IsDigit(Current))
                {
                    throw Error("expected at least one digit in a number's exponent");
                }

                while (!AtEnd && IsDigit(Current))
                {
                    Advance();
                }
            }

            var text = raw[rawStart.._index];
            Record(pointer, JsonValueKind.Number, new Token(text, text, lineIndex, start, _column - start), quoteStyle: null);
        }

        private void ParseLiteral(string pointer, string literal, JsonValueKind kind)
        {
            var lineIndex = _line;
            var start = _column;

            if (_index + literal.Length > raw.Length || string.CompareOrdinal(raw, _index, literal, 0, literal.Length) != 0)
            {
                throw Error($"expected '{literal}'");
            }

            for (var i = 0; i < literal.Length; i++)
            {
                Advance();
            }

            Record(pointer, kind, new Token(literal, literal, lineIndex, start, literal.Length), quoteStyle: null);
        }

        private void Record(string pointer, JsonValueKind kind, Token token, string? quoteStyle)
        {
            spans.Add(new ConfigSpan(new ConfigPointer(pointer), token.LineIndex, token.ContentStart, token.ContentLength, quoteStyle));
            values[pointer] = new JsonScalar(kind, token.Raw, token.Text);
        }

        private FormatException Error(string message) =>
            new($"Invalid JSON at line {(_line + 1).ToString(CultureInfo.InvariantCulture)}, column {(_column + 1).ToString(CultureInfo.InvariantCulture)}: {message}.");

        /// <summary>Escapes one RFC 6901 reference token: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>, in that order.</summary>
        private static string EscapePointerToken(string propertyName) =>
            propertyName.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        /// <summary>One recognized scalar: its source text, its decoded text, and where its content lives.</summary>
        private readonly record struct Token(string Raw, string Text, int LineIndex, int ContentStart, int ContentLength);
    }
}
