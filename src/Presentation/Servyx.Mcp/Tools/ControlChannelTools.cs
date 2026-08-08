using System.ComponentModel;
using ModelContextProtocol.Server;
using Servyx.Application.Servers;
using Servyx.Composition;
using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Mcp.Contracts;

namespace Servyx.Mcp.Tools;

/// <summary>One control command a definition declares, and whether it may be invoked right now for one server.</summary>
public sealed record RconCommandRow(string Id, string Template, bool ReadOnly, bool InvocableNow);

/// <summary>The result of <see cref="ControlChannelTools.CommandsListAsync"/>.</summary>
public sealed record RconCommandsListResult(string Outcome, IReadOnlyList<RconCommandRow>? Commands, Unavailable? Unavailable);

/// <summary>One connected player, present only when the roster fidelity is names-and-count.</summary>
public sealed record PlayerInfoDto(string Name, string PlayerUid, string? SteamId)
{
    public static PlayerInfoDto From(PlayerInfo info) => new(info.Name, info.PlayerUid, info.SteamId);
}

/// <summary>
/// The result of <see cref="ControlChannelTools.PlayersListAsync"/>. <see cref="Fidelity"/> is always
/// present and must never be papered over: an unreadable roster reports <c>unknown</c> with a
/// <see cref="Diagnostic"/>, never an empty <see cref="Players"/> list standing in for "nobody connected".
/// </summary>
public sealed record RconPlayersListResult(
    string Outcome,
    string? Fidelity,
    IReadOnlyList<PlayerInfoDto>? Players,
    int? Count,
    int? Max,
    string? Diagnostic,
    string? Detail,
    Unavailable? Unavailable);

/// <summary>
/// The read half of the control-channel surface: the definition's declared command catalogue, and the
/// current player roster. No apply/invoke tool lives here — see <c>docs</c> on why raw RCON is withheld from
/// this build entirely.
/// </summary>
[McpServerToolType]
public static class ControlChannelTools
{
    [McpServerTool(Name = "servyx_rcon_commands_list", UseStructuredContent = true)]
    [Description(
        "Lists the control commands the single loaded game definition declares, and whether each is " +
        "invocable right now given this server's write mode. Returns 'unavailable' (never an empty list) " +
        "when two or more game definitions are loaded, since the catalogue is then unconfigured fleet-wide.")]
    public static async Task<RconCommandsListResult> CommandsListAsync(
        [Description("The server's discovery id.")] string serverId,
        ServyxCoreComposition composition,
        IServerQueryService query,
        ServyxRconChannels channels,
        WritableServers writable,
        CancellationToken cancellationToken)
    {
        var catalogueStatus = composition.Capabilities.Get(ServyxCapability.ControlCommandCatalogue);
        if (!catalogueStatus.Available)
        {
            return new RconCommandsListResult("unavailable", null, DescribeCatalogueUnavailability(catalogueStatus));
        }

        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new RconCommandsListResult("server-not-found", null, null);
        }

        var mode = writable.Mode(detail.Summary.Id, detail.Summary.Name);
        var rows = channels.Catalog.Commands
            .Select(command => new RconCommandRow(
                command.Id, command.Template, command.ReadOnly, command.ReadOnly || mode == WriteMode.Enabled))
            .ToList();

        return new RconCommandsListResult("listed", rows, null);
    }

    [McpServerTool(Name = "servyx_rcon_players_list", UseStructuredContent = true)]
    [Description(
        "Reports the current player roster over the RCON control channel, including its Fidelity " +
        "(names-and-count / count-only / unknown). An unreadable roster is reported as 'unavailable' or " +
        "'unreachable' with a diagnostic — never as an empty roster standing in for zero connected players.")]
    public static async Task<RconPlayersListResult> PlayersListAsync(
        [Description("The server's discovery id.")] string serverId,
        ServyxCoreComposition composition,
        IServerQueryService query,
        ServyxRconChannels channels,
        CancellationToken cancellationToken)
    {
        var catalogueStatus = composition.Capabilities.Get(ServyxCapability.ControlCommandCatalogue);
        if (!catalogueStatus.Available)
        {
            return new RconPlayersListResult(
                "unavailable", null, null, null, null, null, null, DescribeCatalogueUnavailability(catalogueStatus));
        }

        var detail = await query.GetServerDetailAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return new RconPlayersListResult("server-not-found", null, null, null, null, null, null, null);
        }

        IRconSession? session;
        try
        {
            session = await channels.GetSessionAsync(detail.Summary.Id, detail.Summary.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RconUnreachableException ex)
        {
            // Configured but currently unreachable — a materially different, transient fact from "no channel
            // is configured at all" (the null case below). Never folded into the same outcome.
            return new RconPlayersListResult("unreachable", null, null, null, null, null, ex.Message, null);
        }

        if (session is null)
        {
            // A configured process (the catalogue is available) with no channel configured for THIS server —
            // distinct from both "no catalogue at all" above and "configured but unreachable" above. This is
            // a per-server fact the process-level capability report cannot know (it has no notion of which
            // individual server opted into Servyx:Servers:<name>:Rcon:Enabled), so this tool is the correct
            // emitter — using the shared UnavailableReason vocabulary, never an inline string.
            return new RconPlayersListResult(
                "unavailable", null, null, null, null, null, null,
                new Unavailable(
                    "control-command-catalogue", UnavailableReason.NotConfiguredForServer,
                    $"No RCON control channel is configured for '{serverId}'.", []));
        }

        var snapshot = await session.GetPlayersAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(snapshot);
    }

    /// <summary>
    /// Maps a raw <see cref="PlayerSnapshot"/> to its wire shape. <c>Fidelity</c> always crosses, whatever
    /// its value — an unreadable roster (<see cref="PlayerListFidelity.Unknown"/>) is reported as exactly
    /// that, with its <c>Diagnostic</c>, and never quietly presented as a confirmed-empty roster. Extracted
    /// as its own internal, dependency-free function so this mapping can be tested directly without needing
    /// to acquire a real <see cref="IRconSession"/>.
    /// </summary>
    internal static RconPlayersListResult ToResult(PlayerSnapshot snapshot)
    {
        var list = snapshot.List;

        return new RconPlayersListResult(
            "observed",
            KebabCase.From(list.Fidelity.ToString()),
            list.Players.Select(PlayerInfoDto.From).ToList(),
            list.Count,
            list.Max,
            list.Diagnostic,
            null,
            null);
    }

    /// <summary>
    /// Builds the <see cref="Unavailable"/> reported when <see cref="ServyxCapability.ControlCommandCatalogue"/>
    /// is unavailable. The reason code and contributing definition ids always come straight from the shared
    /// <see cref="ServyxCapabilityReport"/> — never re-derived — but when the reason is
    /// <see cref="UnavailableReason.MultipleDefinitionsLoaded"/> the explanation is authored here, naming all
    /// three subsystems the same "no single governing definition" fact leaves unconfigured (the RCON
    /// control-command catalogue itself, the backup quiesce step, and the stop-escalation ladder), because
    /// <see cref="CapabilityStatus.Explanation"/> for this one capability only names itself — it was written
    /// for a UI rendering one capability row at a time, not for a caller that needs the full blast radius
    /// named in a single sentence.
    /// </summary>
    private static Unavailable DescribeCatalogueUnavailability(CapabilityStatus status)
    {
        if (status.ReasonCode == UnavailableReason.MultipleDefinitionsLoaded)
        {
            return new Unavailable(
                "control-command-catalogue",
                UnavailableReason.MultipleDefinitionsLoaded,
                $"{status.Contributing.Count} game definitions are loaded, so there is no single governing " +
                "definition. Three subsystems are unconfigured fleet-wide as a result: the control-command " +
                "catalogue, the backup quiesce wiring, and the stop-escalation ladder.",
                status.Contributing);
        }

        return UnavailableFactory.From(status);
    }
}
