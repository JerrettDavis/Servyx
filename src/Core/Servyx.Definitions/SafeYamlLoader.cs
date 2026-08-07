using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// The single chokepoint every <see cref="YamlStream.Load(TextReader)"/> call in this project must go
/// through. Both <see cref="GameDefinitionYamlParser"/> and <see cref="FileSystemGameDefinitionProvider"/>
/// route through <see cref="TryLoad"/> — see its remarks for why a second, independently-written
/// <c>YamlStream</c>/<c>.Load(</c> call site anywhere in this project is a bug, not a style preference.
/// </summary>
internal static class SafeYamlLoader
{
    /// <summary>
    /// The maximum combined structural nesting depth this project accepts — indentation levels, flow
    /// collections (<c>[</c>/<c>{</c>), and chained block-sequence dashes (<c>- - - -</c>) all count toward
    /// the same total, because they all nest inside one another in real YAML and all cost the same one
    /// recursion frame in YamlDotNet's scanner. <c>definitions/palworld-docker.yaml</c>'s own deepest combined
    /// nesting, measured under this exact metric (indentation depth + open flow-bracket depth at that point),
    /// is 7 — the <c>env</c> deployment surface's <c>locator: { kind: host-file, path: ... }</c> flow mapping,
    /// which sits 6 indentation levels deep (<c>deployments</c> → profile → <c>config</c> → <c>surfaces</c> →
    /// surface entry → <c>locator</c>) plus 1 for the <c>{</c> itself. 100 is comfortably above that — over
    /// 14x headroom — and comfortably below the ~3000-5000 depth range where YamlDotNet's own
    /// recursive-descent scanner overflows the process stack (confirmed empirically against this project's
    /// compiled library, for indentation, flow, and chained-dash nesting alike).
    /// </summary>
    private const int MaxStructuralNestingDepth = 100;

    /// <summary>
    /// Validates <paramref name="text"/>'s structural nesting depth and, only if that passes, loads it into
    /// a <see cref="YamlStream"/>. Returns <see langword="false"/> with a populated
    /// <paramref name="errorMessage"/> (and, where available, <paramref name="line"/>/<paramref name="column"/>)
    /// for either failure mode; never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists as one shared entry point.</strong> YamlDotNet's scanner is recursive-descent:
    /// a document with deeply nested indentation, flow collections (<c>[[[[...]]]]</c>), or chained
    /// block-sequence dashes (<c>- - - - -</c>) — every one of which costs only 1-2 bytes per nesting level,
    /// so a few KB of input can reach thousands of levels — recurses proportionally to that nesting and can
    /// overflow the process stack. A <see cref="StackOverflowException"/> cannot be caught in .NET
    /// (uncatchable since 2.0 SP1) and terminates the process outright, so nothing downstream of
    /// <see cref="YamlStream.Load(TextReader)"/> can defend against it — the only fix is to never call it
    /// with pathological input. That means the check must sit in front of <em>every</em> call site, not just
    /// the one this project's primary parser uses: a definition file is read by
    /// <see cref="FileSystemGameDefinitionProvider.ListAsync"/> (via its own header peek) before
    /// <see cref="GameDefinitionYamlParser"/> ever sees it, so a malicious file merely sitting in a watched
    /// directory would crash the host at listing time if that path had its own unguarded
    /// <c>YamlStream.Load</c>. Routing both call sites through this one method is what makes that
    /// impossible by construction rather than by remembering to duplicate a check.
    /// </para>
    /// <para>
    /// <strong>The depth scan is a heuristic, not a full YAML tokenizer</strong> — it tracks quoted scalars
    /// and <c>#</c> comments just well enough to avoid mis-measuring nesting inside string content, computes
    /// indentation-based block depth with the standard "pop while shallower-or-equal, then push" stack
    /// algorithm, and adds flow-bracket depth and same-line chained-dash depth on top. It intentionally does
    /// not special-case literal/folded block scalars (<c>|</c>/<c>&gt;</c>): a deeply and irregularly
    /// indented block-scalar body could, in principle, be over-rejected as "too deep" even though it is inert
    /// text with no parser recursion behind it. That is a false-positive risk, not a security gap — this
    /// project's real fixture uses no block scalars — and is a deliberate scope cut for a pre-scan whose only
    /// job is to be a reliable circuit breaker on the constructs that actually threaten the scanner.
    /// </para>
    /// </remarks>
    internal static bool TryLoad(string text, string subject, out YamlStream? stream, out string? errorMessage, out int? line, out int? column)
    {
        stream = null;
        errorMessage = null;
        line = null;
        column = null;

        if (!TryValidateStructuralNestingDepth(text, subject, out errorMessage, out var depthLine, out var depthColumn))
        {
            line = depthLine;
            column = depthColumn;
            return false;
        }

        try
        {
            var candidate = new YamlStream();
            candidate.Load(new StringReader(text));
            stream = candidate;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"The {subject} is not valid YAML: {ex.Message}";
            if (ex is YamlException ye)
            {
                line = (int)ye.Start.Line;
                column = (int)ye.Start.Column;
            }
            else
            {
                line = 1;
                column = 1;
            }

            return false;
        }
    }

    private static bool TryValidateStructuralNestingDepth(string text, string subject, out string? errorMessage, out int? line, out int? column)
    {
        errorMessage = null;
        line = null;
        column = null;

        var flowDepth = 0;
        var indentStack = new List<int>();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var curLine = 1;
        var curColumn = 1;

        var atLineStart = true;
        var lineIndentCaptured = false;
        var dashTokensThisLine = 0;

        var i = 0;
        var n = text.Length;

        // Local — not the 'out' parameter — because a local function cannot capture a ref/out parameter of
        // its enclosing method (CS1628); assigned to the real out parameters only where this method returns.
        string? tooDeepMessage = null;

        bool TooDeep()
        {
            var combined = indentStack.Count + Math.Max(0, dashTokensThisLine - 1) + flowDepth;
            if (combined <= MaxStructuralNestingDepth)
            {
                return false;
            }

            tooDeepMessage =
                $"The {subject}'s structural nesting (indentation, flow collections '[]'/'{{}}', and/or chained "
                + $"block-sequence '-' entries) exceeds the maximum supported depth of {MaxStructuralNestingDepth}. "
                + "Rejected before parsing to avoid a stack overflow in the underlying YAML scanner — this is "
                + "not a shape any real definition needs.";
            return true;
        }

        while (i < n)
        {
            var c = text[i];

            if (c == '\n')
            {
                curLine++;
                curColumn = 1;
                atLineStart = true;
                lineIndentCaptured = false;
                dashTokensThisLine = 0;
                i++;
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'')
                {
                    if (i + 1 < n && text[i + 1] == '\'')
                    {
                        i += 2;
                        curColumn += 2;
                        continue;
                    }

                    inSingleQuote = false;
                }

                i++;
                curColumn++;
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '\\' && i + 1 < n)
                {
                    i += 2;
                    curColumn += 2;
                    continue;
                }

                if (c == '"')
                {
                    inDoubleQuote = false;
                }

                i++;
                curColumn++;
                continue;
            }

            if (flowDepth == 0 && atLineStart)
            {
                if (c is ' ' or '\t')
                {
                    i++;
                    curColumn++;
                    continue;
                }

                if (!lineIndentCaptured)
                {
                    lineIndentCaptured = true;

                    if (c == '#')
                    {
                        var commentNewline = text.IndexOf('\n', i);
                        if (commentNewline < 0)
                        {
                            break;
                        }

                        curColumn += commentNewline - i;
                        i = commentNewline;
                        continue;
                    }

                    // Real (non-comment) content opens or continues a block level at this line's indentation —
                    // whether the content is a mapping key, a scalar, or a sequence dash: indentation depth is
                    // the same recursion cost in YamlDotNet's scanner regardless of what follows it.
                    var lineIndent = curColumn - 1;
                    while (indentStack.Count > 0 && lineIndent <= indentStack[^1])
                    {
                        indentStack.RemoveAt(indentStack.Count - 1);
                    }

                    indentStack.Add(lineIndent);
                    if (TooDeep())
                    {
                        errorMessage = tooDeepMessage;
                    line = curLine;
                    column = curColumn;
                    return false;
                    }
                }

                var isDashToken = c == '-' && (i + 1 >= n || text[i + 1] is ' ' or '\t' or '\n');
                if (isDashToken)
                {
                    // The first dash on a line is already accounted for by the indentation push above; each
                    // additional chained dash ("- - - - value") is compact, same-column extra nesting on top.
                    dashTokensThisLine++;
                    if (dashTokensThisLine > 1 && TooDeep())
                    {
                        errorMessage = tooDeepMessage;
                    line = curLine;
                    column = curColumn;
                    return false;
                    }

                    if (i + 1 < n && text[i + 1] != '\n')
                    {
                        i += 2;
                        curColumn += 2;
                    }
                    else
                    {
                        i++;
                        curColumn++;
                    }

                    continue;
                }

                atLineStart = false;
                continue;
            }

            if (flowDepth > 0 && atLineStart)
            {
                // Inside a still-open flow collection, a continuation line's indentation is not a new block
                // level — flow-context recursion is already bounded by flowDepth alone.
                if (c is ' ' or '\t')
                {
                    i++;
                    curColumn++;
                    continue;
                }

                atLineStart = false;
                continue;
            }

            if (c == '#')
            {
                var nextNewline = text.IndexOf('\n', i);
                if (nextNewline < 0)
                {
                    break;
                }

                curColumn += nextNewline - i;
                i = nextNewline;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                i++;
                curColumn++;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                i++;
                curColumn++;
                continue;
            }

            if (c is '[' or '{')
            {
                flowDepth++;
                if (TooDeep())
                {
                    errorMessage = tooDeepMessage;
                    line = curLine;
                    column = curColumn;
                    return false;
                }
            }
            else if (c is ']' or '}' && flowDepth > 0)
            {
                flowDepth--;
            }

            i++;
            curColumn++;
        }

        return true;
    }
}
