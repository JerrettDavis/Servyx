using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>
/// Parses the reply to Palworld's <c>ShowPlayers</c> in the shape the definition declares for it:
/// <c>control.players.parsers["rcon.players"] = { kind: csv-with-header, columns: [name, playerUid, steamId] }</c>.
/// </summary>
/// <remarks>
/// <para>
/// The reply is a header line followed by one comma-separated line per connected player. The header is
/// discarded rather than trusted for column order: the definition states the order, and a game update that
/// renamed a header would otherwise silently shift every field by one.
/// </para>
/// <para>
/// <strong>A malformed line is skipped, not guessed at.</strong> A player name legitimately containing a
/// comma cannot be recovered from this format — there is no quoting — so a line with the wrong field count
/// yields no player rather than a player with a truncated name and someone else's identifier.
/// </para>
/// </remarks>
internal static class RconPlayerListParser
{
    private const int ExpectedColumns = 3;

    /// <summary>Parses a <c>ShowPlayers</c> reply into players.</summary>
    /// <param name="text">The raw reply text.</param>
    internal static IReadOnlyList<PlayerInfo> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var players = new List<PlayerInfo>();
        var isFirstLine = true;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');

            if (line.Length == 0)
            {
                continue;
            }

            if (isFirstLine)
            {
                // The header line, e.g. "name,playeruid,steamid". Consumed and discarded.
                isFirstLine = false;
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length != ExpectedColumns)
            {
                continue;
            }

            var name = fields[0].Trim();
            var playerUid = fields[1].Trim();
            var steamId = fields[2].Trim();

            if (name.Length == 0 && playerUid.Length == 0)
            {
                continue;
            }

            players.Add(new PlayerInfo(name, playerUid, steamId.Length == 0 ? null : steamId));
        }

        return players;
    }
}
