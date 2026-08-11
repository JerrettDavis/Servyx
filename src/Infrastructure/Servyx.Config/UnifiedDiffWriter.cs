using System.Text;

namespace Servyx.Config;

/// <summary>
/// Renders a line-based unified diff between two versions of one configuration surface.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-rolled rather than taken from a package, and small on purpose.</strong> The input is never
/// arbitrary: it is one configuration file before and after an edit that only ever splices over registered
/// value spans, so the two sides differ on a handful of lines and agree everywhere else. A full diff library
/// would be a new dependency for a problem this shape does not have. The algorithm below is a standard
/// longest-common-subsequence walk, which is exact — not a heuristic — for inputs of this size.
/// </para>
/// <para>
/// <strong>Output is for a human, not for <c>patch</c>.</strong> The format is the familiar
/// <c>--- a/… / +++ b/… / @@ -l,c +l,c @@</c> one because that is what an operator recognizes, but nothing in
/// Servyx ever feeds it back to a patch tool: an apply writes the recorded
/// <c>ChangePlanActionRecord.PostImageContent</c> verbatim, and a revert writes the recorded pre-image. The
/// diff is a presentation of the change, which is exactly why it is safe for it to be the artefact secrets
/// are masked out of while the images it was rendered from stay intact.
/// </para>
/// </remarks>
internal static class UnifiedDiffWriter
{
    /// <summary>Lines of unchanged context shown either side of each changed region.</summary>
    private const int ContextLines = 3;

    /// <summary>
    /// Renders the unified diff turning <paramref name="before"/> into <paramref name="after"/>, labelled
    /// with <paramref name="path"/>. Returns the empty string when the two are identical.
    /// </summary>
    /// <param name="path">The surface path, used for the <c>---</c>/<c>+++</c> header lines.</param>
    /// <param name="before">The surface's content before the change.</param>
    /// <param name="after">The surface's content after the change.</param>
    public static string Write(string path, string before, string after)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var left = SplitLines(before);
        var right = SplitLines(after);
        var edits = Diff(left, right);

        var hunks = Group(edits);
        if (hunks.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("--- a/").Append(path).Append('\n');
        builder.Append("+++ b/").Append(path).Append('\n');

        foreach (var hunk in hunks)
        {
            // Unified-diff line numbers are 1-based; a zero-length side is conventionally written with a
            // start of 0, which is what the max(…, 1)-free arithmetic below produces naturally.
            builder.Append("@@ -")
                .Append(hunk.LeftCount == 0 ? hunk.LeftStart : hunk.LeftStart + 1)
                .Append(',')
                .Append(hunk.LeftCount)
                .Append(" +")
                .Append(hunk.RightCount == 0 ? hunk.RightStart : hunk.RightStart + 1)
                .Append(',')
                .Append(hunk.RightCount)
                .Append(" @@\n");

            foreach (var edit in hunk.Edits)
            {
                builder.Append(edit.Kind switch
                {
                    EditKind.Removed => '-',
                    EditKind.Added => '+',
                    _ => ' ',
                });

                builder.Append(edit.Text).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits on either line terminator without normalizing them, and without inventing a trailing empty
    /// line for content that ends in a newline — a diff that reported a phantom final line would be
    /// describing a change that is not there.
    /// </summary>
    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The classic LCS table walk. Exact, and O(n·m) — fine for one configuration file.</summary>
    private static List<Edit> Diff(string[] left, string[] right)
    {
        var lengths = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var edits = new List<Edit>(left.Length + right.Length);
        var x = 0;
        var y = 0;
        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                edits.Add(new Edit(EditKind.Unchanged, left[x], x, y));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                edits.Add(new Edit(EditKind.Removed, left[x], x, y));
                x++;
            }
            else
            {
                edits.Add(new Edit(EditKind.Added, right[y], x, y));
                y++;
            }
        }

        while (x < left.Length)
        {
            edits.Add(new Edit(EditKind.Removed, left[x], x, y));
            x++;
        }

        while (y < right.Length)
        {
            edits.Add(new Edit(EditKind.Added, right[y], x, y));
            y++;
        }

        return edits;
    }

    /// <summary>
    /// Collapses the full edit script into hunks: each changed region plus <see cref="ContextLines"/> of
    /// unchanged lines either side, with adjacent regions merged when their context overlaps.
    /// </summary>
    private static List<Hunk> Group(List<Edit> edits)
    {
        var interesting = new bool[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            if (edits[i].Kind == EditKind.Unchanged)
            {
                continue;
            }

            var from = Math.Max(0, i - ContextLines);
            var to = Math.Min(edits.Count - 1, i + ContextLines);
            for (var j = from; j <= to; j++)
            {
                interesting[j] = true;
            }
        }

        var hunks = new List<Hunk>();
        var index = 0;
        while (index < edits.Count)
        {
            if (!interesting[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < edits.Count && interesting[index])
            {
                index++;
            }

            var slice = edits.GetRange(start, index - start);
            hunks.Add(new Hunk(
                slice[0].LeftIndex,
                slice.Count(e => e.Kind != EditKind.Added),
                slice[0].RightIndex,
                slice.Count(e => e.Kind != EditKind.Removed),
                slice));
        }

        return hunks;
    }

    private enum EditKind
    {
        Unchanged,
        Removed,
        Added,
    }

    private sealed record Edit(EditKind Kind, string Text, int LeftIndex, int RightIndex);

    private sealed record Hunk(int LeftStart, int LeftCount, int RightStart, int RightCount, List<Edit> Edits);
}
