using System.ComponentModel;
using ModelContextProtocol.Server;
using Servyx.Definitions;

namespace Servyx.Mcp.Tools;

/// <summary>The trust evaluation result for a loaded definition.</summary>
public sealed record TrustVerdictDto(string Tier, IReadOnlyList<string> DeniedCapabilities, string? Reason);

/// <summary>One currently-loaded game definition.</summary>
public sealed record LoadedDefinitionDto(string Id, string ContentHash, string SourceId, string? SourcePath, TrustVerdictDto Trust);

/// <summary>The result of <see cref="CatalogTools.GamesList"/>.</summary>
public sealed record GamesListResult(string Outcome, IReadOnlyList<LoadedDefinitionDto> Games);

/// <summary>One fault recorded while loading a game definition.</summary>
public sealed record DefinitionFaultDto(string Path, string Message, int? Line, int? Column);

/// <summary>The result of <see cref="CatalogTools.FaultsList"/>.</summary>
public sealed record GameDefinitionFaultsListResult(string Outcome, IReadOnlyList<DefinitionFaultDto> Faults);

/// <summary>Read-only tools over the game-definition catalog itself, independent of any adopted server.</summary>
[McpServerToolType]
public static class CatalogTools
{
    [McpServerTool(Name = "servyx_games_list", UseStructuredContent = true)]
    [Description("Lists every currently-loaded game definition: its id, content hash, source, and trust verdict.")]
    public static GamesListResult GamesList(GameDefinitionCatalog catalog, CancellationToken cancellationToken)
    {
        var games = catalog.DefinitionsById.Values
            .OrderBy(loaded => loaded.Ref.Id, StringComparer.Ordinal)
            .Select(loaded => new LoadedDefinitionDto(
                loaded.Ref.Id,
                loaded.Ref.ContentHash,
                loaded.Ref.SourceId,
                loaded.Ref.SourcePath,
                new TrustVerdictDto(KebabCase.From(loaded.Trust.Tier.ToString()), loaded.Trust.DeniedCapabilities, loaded.Trust.Reason)))
            .ToList();

        return new GamesListResult("listed", games);
    }

    [McpServerTool(Name = "servyx_game_definition_faults_list", UseStructuredContent = true)]
    [Description("Lists every fault recorded while loading a game definition — a malformed file, a validation error, or a losing side of a duplicate-id collision.")]
    public static GameDefinitionFaultsListResult FaultsList(GameDefinitionCatalog catalog, CancellationToken cancellationToken)
    {
        var faults = catalog.Faults
            .Select(fault => new DefinitionFaultDto(fault.Path, fault.Message, fault.Line, fault.Column))
            .ToList();

        return new GameDefinitionFaultsListResult("listed", faults);
    }
}
