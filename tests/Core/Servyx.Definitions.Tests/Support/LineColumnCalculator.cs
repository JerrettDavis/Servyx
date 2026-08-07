namespace Servyx.Definitions.Tests.Support;

/// <summary>
/// Computes the 1-based line/column of a substring's first occurrence within a text, independently of
/// <see cref="GameDefinitionYamlParser"/> — used so line/column-accuracy tests assert against a position
/// computed from the raw text itself, not against a number copied out of the parser's own output. Only
/// meaningful for LF-normalized text (see the remarks on <c>LineColumnAccuracyTests</c>), since it does not
/// attempt to replicate any particular CRLF-counting convention.
/// </summary>
internal static class LineColumnCalculator
{
    public static (int Line, int Column) Locate(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"'{needle}' was not found in the given text.");
        }

        var line = 1;
        var lastNewline = -1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lastNewline = i;
            }
        }

        var column = index - lastNewline;
        return (line, column);
    }
}
