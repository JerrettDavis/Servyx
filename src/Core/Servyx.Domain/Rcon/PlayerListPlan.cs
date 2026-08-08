using Servyx.Domain.Definitions.Model;

namespace Servyx.Domain.Rcon;

/// <summary>
/// What a control channel should do to answer "who is connected": which catalogued command to invoke, and
/// how to read that command's reply. Resolved once, from a definition's <c>control.players</c> block, and
/// handed to an <see cref="IRconSession"/> at construction rather than recomputed on every poll.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no default plan and no default command id.</strong> A definition-driven session invents
/// nothing: if the definition names no operation on a given channel, that channel simply has no player-list
/// source, and <see cref="Resolve"/> says so via <see cref="Diagnostic"/> rather than guessing at a command
/// id like <c>"players"</c> that a particular game's dialect may not even declare.
/// </para>
/// <para>
/// <strong><c>preferred</c> is the sole ordering authority.</strong> A key in <c>control.players.parsers</c>
/// that no entry in <c>control.players.preferred</c> names is never used by this resolver, however plausible
/// it looks sitting next to the ones that are — the definition states the order it wants tried, and a parser
/// nobody asked for is dead weight, not a fallback.
/// </para>
/// <para>
/// <strong><see cref="CommandId"/> and <see cref="Parser"/> are keyed differently, on purpose.</strong>
/// <see cref="CommandId"/> is the BARE catalogue id (e.g. <c>"players"</c>, <c>"list"</c>) — exactly the
/// string <c>RconCommandCatalog</c> indexes its commands by, and exactly what <c>IRconSession.InvokeAsync</c>
/// takes. <see cref="Parser"/>, by contrast, was looked up under the COMPOSITE <c>&lt;channel&gt;.&lt;operation&gt;</c>
/// key (e.g. <c>"rcon.players"</c>) — the same key <see cref="PlayersConfig.Parsers"/> and
/// <see cref="PlayersConfig.Preferred"/> both use, because a reply shape belongs to one particular
/// channel/operation pairing, not to the bare command id alone (two different channels could plausibly
/// declare a command with the same bare id but a different reply shape).
/// </para>
/// </remarks>
/// <param name="CommandId">
/// The bare catalogue command id to invoke (e.g. <c>"players"</c>), or <see langword="null"/> when nothing
/// resolved. Never invented — always either the tail of a matched <c>preferred</c> entry, or absent.
/// </param>
/// <param name="Parser">
/// How to read that command's reply, or <see langword="null"/> when either nothing resolved or the matched
/// <c>preferred</c> entry has no corresponding <c>parsers</c> entry. A non-null <see cref="CommandId"/> with
/// a null <see cref="Parser"/> is a valid, if degraded, outcome: the command can still be invoked, but its
/// reply cannot be read, so parsing must fall through to <see cref="PlayerListFidelity.Unknown"/>.
/// </param>
/// <param name="Diagnostic">
/// A human-readable, game-neutral explanation of how this plan was reached, or — when it resolved nothing —
/// of why not. Always non-empty, so a caller with an unresolved plan always has something to show an
/// operator or fold into a snapshot's own diagnostic.
/// </param>
public sealed record PlayerListPlan(string? CommandId, PlayerParserSpec? Parser, string Diagnostic)
{
    /// <summary>The control channel id this codebase's own RCON control channel is registered under.</summary>
    public const string RconChannelId = "rcon";

    /// <summary>
    /// The plan for a control channel composed with no game definition at all — e.g. no definition loaded,
    /// or more than one loaded with no single definition selected for this channel. Distinct from
    /// <see cref="Resolve"/> returning an unresolved plan for a definition that WAS available but simply
    /// declares no player-list source: this constant exists for the composition root, which may have no
    /// <see cref="PlayersConfig"/> to hand <see cref="Resolve"/> in the first place.
    /// </summary>
    public static readonly PlayerListPlan None = new(
        null,
        null,
        "No game definition was available when this control channel was composed, so no player-list source "
        + "is declared for it. Nothing will be invoked and no roster will be claimed.");

    /// <summary>Whether this plan names a command to invoke. <see langword="false"/> means nothing will be sent.</summary>
    public bool IsResolved => CommandId is not null;

    /// <summary>
    /// Resolves the plan for one control channel from a definition's <c>control.players</c> block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Walks <paramref name="players"/>'s <see cref="PlayersConfig.Preferred"/> list in the definition's
    /// declared order, looking for the first entry that (a) names <paramref name="channelId"/> — either
    /// exactly, or as a <c>"&lt;channelId&gt;.&lt;operation&gt;"</c> composite — and (b) has a corresponding
    /// entry in <see cref="PlayersConfig.Parsers"/>. That first fully-readable entry wins, even if an earlier
    /// entry in the list also names this channel but has no declared parser: such an entry still fixes the
    /// resolved <see cref="CommandId"/> if no later, fully-readable entry for this channel exists, but a
    /// later entry that both names the channel AND has a parser is preferred over it, matching how the loop
    /// below only returns immediately on a fully-readable match and otherwise remembers the first
    /// command-only match as a fallback.
    /// </para>
    /// </remarks>
    /// <param name="players">The definition's <c>control.players</c> block, or <see langword="null"/> when the definition declares none.</param>
    /// <param name="channelId">The control channel being resolved for, e.g. <see cref="RconChannelId"/>.</param>
    public static PlayerListPlan Resolve(PlayersConfig? players, string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        if (players is null)
        {
            return new PlayerListPlan(
                null,
                null,
                "The game definition declares no 'control.players' block, so no player-list source is "
                + $"declared for the '{channelId}' control channel.");
        }

        var prefix = channelId + ".";
        string? firstUnparsedCommand = null;
        string? firstUnparsedEntry = null;
        var namedChannelWithoutOperation = false;

        foreach (var entry in players.Preferred)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }

            if (string.Equals(entry, channelId, StringComparison.Ordinal))
            {
                namedChannelWithoutOperation = true;
                continue;
            }

            if (!entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var commandId = entry[prefix.Length..];
            if (commandId.Length == 0)
            {
                namedChannelWithoutOperation = true;
                continue;
            }

            if (players.Parsers.TryGetValue(entry, out var parser))
            {
                return new PlayerListPlan(
                    commandId,
                    parser,
                    $"Resolved from the 'control.players.preferred' entry '{entry}'.");
            }

            firstUnparsedCommand ??= commandId;
            firstUnparsedEntry ??= entry;
        }

        if (firstUnparsedEntry is not null)
        {
            return new PlayerListPlan(
                firstUnparsedCommand,
                null,
                $"The 'control.players.preferred' entry '{firstUnparsedEntry}' names an operation on the "
                + $"'{channelId}' control channel, but 'control.players.parsers' declares no reply shape for "
                + "it, so its reply cannot be read.");
        }

        if (namedChannelWithoutOperation)
        {
            return new PlayerListPlan(
                null,
                null,
                $"'control.players.preferred' names the '{channelId}' control channel but no operation on "
                + "it, so there is no command to invoke for a player list.");
        }

        return new PlayerListPlan(
            null,
            null,
            players.Preferred.Count == 0
                ? "'control.players.preferred' is empty, so no player-list source is declared for the "
                  + $"'{channelId}' control channel."
                : $"'control.players.preferred' names no operation on the '{channelId}' control channel; it "
                  + $"declares: {string.Join(", ", players.Preferred)}.");
    }
}
