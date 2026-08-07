using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Turns a raw player-list reply into a <see cref="PlayerListSnapshot"/>, in whichever shape the
/// definition's <c>control.players.parsers</c> block declares for the channel/operation that produced it.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Parse(string?, PlayerParserSpec?)"/> is a total function.</strong> Every reply — empty,
/// truncated, garbage, or simply the wrong shape — produces a snapshot; none produces an exception. That is
/// not politeness, it is the isolation boundary: several of the reply formats this understands are modelled
/// from unverified community reports, and a wrong guess about a reply format must be incapable of failing a
/// stop sequence, a backup, or a readiness decision. The three layers that enforce it are (1) patterns
/// compiled during definition validation, so a malformed regex is a parse error against the file;
/// (2) this method, which catches everything and degrades to <see cref="PlayerListFidelity.Unknown"/>; and
/// (3) call-site containment — only the status/query projection consumes the result, asserted by an
/// architecture test.
/// </para>
/// <para>
/// <strong>A malformed line is skipped, not guessed at.</strong> A player name legitimately containing the
/// CSV field separator cannot be recovered from a format with no quoting, so a line with the wrong field
/// count yields no player rather than a player with a truncated name and someone else's identifier.
/// </para>
/// </remarks>
public static class RconPlayerListParser
{
    /// <summary>
    /// The shape assumed by <see cref="Parse(string?)"/>, which exists for callers that predate the
    /// definition-driven overload: a three-column CSV with a header row, the shape the first shipped
    /// definition declares for <c>rcon.players</c>.
    /// </summary>
    internal static readonly PlayerParserSpec.CsvWithHeader DefaultCsvShape =
        new(["name", "playerUid", "steamId"], "name", null);

    /// <summary>Parses a player-list reply in the default three-column CSV shape.</summary>
    /// <param name="text">The raw reply text.</param>
    internal static IReadOnlyList<PlayerInfo> Parse(string? text) => Parse(text, DefaultCsvShape).Players;

    /// <summary>Parses a player-list reply in the shape the definition declares. Never throws.</summary>
    /// <param name="text">The raw reply text.</param>
    /// <param name="spec">The declared reply shape.</param>
    public static PlayerListSnapshot Parse(string? text, PlayerParserSpec? spec)
    {
        if (spec is null)
        {
            return PlayerListSnapshot.Unresolved("No player-list parser is declared for this channel operation.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return PlayerListSnapshot.Unresolved("The control channel returned an empty reply.");
        }

        try
        {
            return spec switch
            {
                PlayerParserSpec.CsvWithHeader csv => ParseCsv(text, csv),
                PlayerParserSpec.SummaryLine summary => ParseSummaryLine(text, summary),
                PlayerParserSpec.Lines lines => ParseLines(text, lines),
                PlayerParserSpec.Count count => ParseCount(text, count),
                _ => PlayerListSnapshot.Unresolved("The declared player-list parser shape is not understood."),
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return PlayerListSnapshot.Unresolved("Matching the declared pattern against the reply timed out.");
        }
        catch (Exception ex)
        {
            // Total by construction: no reply shape, however hostile, may surface as an exception to a
            // caller. The reason is kept as a diagnostic so the degradation is explainable, never thrown.
            return PlayerListSnapshot.Unresolved($"The reply could not be read: {ex.GetType().Name}.");
        }
    }

    // -- csv-with-header ------------------------------------------------------------------------------------

    private static PlayerListSnapshot ParseCsv(string text, PlayerParserSpec.CsvWithHeader spec)
    {
        var columns = spec.Columns;
        if (columns.Count == 0)
        {
            return PlayerListSnapshot.Unresolved("The declared CSV parser names no columns.");
        }

        var nameIndex = IndexOf(columns, spec.NameColumn);
        if (nameIndex < 0)
        {
            return PlayerListSnapshot.Unresolved($"The declared name column '{spec.NameColumn}' is not one of the declared columns.");
        }

        var idIndex = spec.IdColumn is null ? -1 : IndexOf(columns, spec.IdColumn);
        if (spec.IdColumn is not null && idIndex < 0)
        {
            return PlayerListSnapshot.Unresolved($"The declared id column '{spec.IdColumn}' is not one of the declared columns.");
        }

        // The identifier slots of PlayerInfo are filled from the declared columns other than the name
        // column, in declaration order, with an explicitly declared idColumn taking the first slot. For the
        // three-column shape that already ships (name, uid, steam id) with no idColumn declared, that is
        // exactly the original positional mapping — column 1 to PlayerUid, column 2 to SteamId.
        var identifierIndexes = new List<int>(columns.Count);
        if (idIndex >= 0)
        {
            identifierIndexes.Add(idIndex);
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (i != nameIndex && i != idIndex)
            {
                identifierIndexes.Add(i);
            }
        }

        var players = new List<PlayerInfo>();
        var skipped = 0;
        var sawHeader = false;

        foreach (var line in NonEmptyLines(text))
        {
            var fields = line.Split(',');

            if (!sawHeader)
            {
                // The header is discarded rather than trusted for column order: the definition states the
                // order, and a game update that renamed a header would otherwise shift every field by one.
                // Its field count IS checked, because it is the only thing distinguishing "a header and an
                // empty server" from "a reply in some entirely different format".
                sawHeader = true;
                if (fields.Length != columns.Count)
                {
                    return PlayerListSnapshot.Unresolved(
                        $"The reply's first line has {fields.Length} field(s); the declared shape has {columns.Count}.");
                }

                continue;
            }

            if (fields.Length != columns.Count)
            {
                skipped++;
                continue;
            }

            var name = fields[nameIndex].Trim();
            var uid = identifierIndexes.Count > 0 ? fields[identifierIndexes[0]].Trim() : string.Empty;
            var secondary = identifierIndexes.Count > 1 ? fields[identifierIndexes[1]].Trim() : string.Empty;

            if (name.Length == 0 && uid.Length == 0)
            {
                continue;
            }

            players.Add(new PlayerInfo(name, uid, secondary.Length == 0 ? null : secondary));
        }

        if (!sawHeader)
        {
            return PlayerListSnapshot.Unresolved("The reply carried no lines.");
        }

        var diagnostic = skipped == 0
            ? null
            : $"{skipped} reply line(s) did not have {columns.Count} field(s) and were skipped.";

        return PlayerListSnapshot.Roster(players, max: null, diagnostic);
    }

    // -- summary-line ---------------------------------------------------------------------------------------

    private static PlayerListSnapshot ParseSummaryLine(string text, PlayerParserSpec.SummaryLine spec)
    {
        var match = spec.Pattern.Regex.Match(text);
        if (!match.Success)
        {
            return PlayerListSnapshot.Unresolved("The reply did not match the declared summary pattern.");
        }

        if (!TryReadCount(match, out var count))
        {
            return PlayerListSnapshot.Unresolved("The declared summary pattern matched but captured no readable count.");
        }

        var max = TryReadInt(match.Groups[PlayerParserGroups.Max], out var parsedMax) ? parsedMax : (int?)null;

        var namesGroup = match.Groups[PlayerParserGroups.Names];
        if (!namesGroup.Success)
        {
            return PlayerListSnapshot.CountOnly(count, max, "The reply reports a count but carries no names.");
        }

        var names = namesGroup.Value
            .Split(spec.NameSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (names.Length != count)
        {
            return PlayerListSnapshot.CountOnly(
                count,
                max,
                $"The reply reports {count} player(s) but names {names.Length}; the count is used and the names are discarded.");
        }

        return PlayerListSnapshot.Roster([.. names.Select(Anonymous)], max);
    }

    // -- lines ----------------------------------------------------------------------------------------------

    private static PlayerListSnapshot ParseLines(string text, PlayerParserSpec.Lines spec)
    {
        var players = new List<PlayerInfo>();
        int? headerCount = null;
        var sawHeader = false;
        var unmatched = 0;

        foreach (var line in NonEmptyLines(text))
        {
            if (spec.IgnorePatterns.Any(p => p.Regex.IsMatch(line)))
            {
                continue;
            }

            var entry = spec.EntryPattern.Regex.Match(line);
            if (entry.Success)
            {
                var name = entry.Groups[PlayerParserGroups.Name].Value.Trim();
                if (name.Length == 0)
                {
                    unmatched++;
                    continue;
                }

                // The captured id goes to PlayerUid and nowhere else: this shape declares "an identifier",
                // not which vendor's identifier it is, so claiming it as a platform id would be a guess.
                var idGroup = entry.Groups[PlayerParserGroups.Id];
                var id = idGroup.Success ? idGroup.Value.Trim() : string.Empty;
                players.Add(id.Length == 0 ? Anonymous(name) : new PlayerInfo(name, id, null));
                continue;
            }

            if (!sawHeader && spec.HeaderPattern is { } header && header.Regex.Match(line) is { Success: true } headerMatch)
            {
                sawHeader = true;
                if (TryReadCount(headerMatch, out var declared))
                {
                    headerCount = declared;
                }

                continue;
            }

            unmatched++;
        }

        if (unmatched > 0)
        {
            var diagnostic = $"{unmatched} reply line(s) matched neither the declared entry pattern nor an ignore pattern.";
            return headerCount is { } known
                ? PlayerListSnapshot.CountOnly(known, null, diagnostic)
                : PlayerListSnapshot.Unresolved(diagnostic);
        }

        if (headerCount is { } expected && expected != players.Count)
        {
            return PlayerListSnapshot.CountOnly(
                expected,
                null,
                $"The reply header reports {expected} player(s) but {players.Count} entry line(s) were read; the header is used.");
        }

        return PlayerListSnapshot.Roster(players);
    }

    // -- count ----------------------------------------------------------------------------------------------

    private static PlayerListSnapshot ParseCount(string text, PlayerParserSpec.Count spec)
    {
        if (spec.Pattern is { } pattern)
        {
            var match = pattern.Regex.Match(text);
            if (!match.Success)
            {
                return PlayerListSnapshot.Unresolved("The reply did not match the declared count pattern.");
            }

            return TryReadCount(match, out var count)
                ? PlayerListSnapshot.CountOnly(count)
                : PlayerListSnapshot.Unresolved("The declared count pattern matched but captured no readable count.");
        }

        if (spec.JsonPointer is not { } pointer)
        {
            return PlayerListSnapshot.Unresolved("The declared count parser names neither a pattern nor a JSON pointer.");
        }

        using var document = JsonDocument.Parse(text);
        if (!TryResolvePointer(document.RootElement, pointer, out var element))
        {
            return PlayerListSnapshot.Unresolved($"The reply carries nothing at JSON pointer '{pointer}'.");
        }

        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value >= 0
            ? PlayerListSnapshot.CountOnly(value)
            : PlayerListSnapshot.Unresolved($"The value at JSON pointer '{pointer}' is not a player count.");
    }

    /// <summary>Resolves an RFC 6901 JSON pointer, returning false rather than throwing for any miss.</summary>
    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement result)
    {
        result = root;

        foreach (var rawToken in pointer.Split('/').Skip(1))
        {
            var token = rawToken.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            switch (result.ValueKind)
            {
                case JsonValueKind.Object when result.TryGetProperty(token, out var property):
                    result = property;
                    break;

                case JsonValueKind.Array
                    when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        && index < result.GetArrayLength():
                    result = result[index];
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    // -- shared ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A player known only by name. Formats that carry no separate identifier are not lying about one: the
    /// name IS the identifier a command like a kick takes on those servers.
    /// </summary>
    private static PlayerInfo Anonymous(string name) => new(name, name, null);

    private static IEnumerable<string> NonEmptyLines(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static int IndexOf(IReadOnlyList<string> columns, string column)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadCount(Match match, out int count) =>
        TryReadInt(match.Groups[PlayerParserGroups.Count], out count);

    private static bool TryReadInt(Group group, out int value)
    {
        if (group.Success
            && int.TryParse(group.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
    }
}
