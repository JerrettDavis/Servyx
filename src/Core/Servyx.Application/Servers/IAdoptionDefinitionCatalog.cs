using Servyx.Domain.Definitions;

namespace Servyx.Application.Servers;

/// <summary>
/// The definition-catalog data <see cref="ServerAdoptionService"/> needs to discover adoption candidates and
/// pin a newly-adopted server to exact definition content — the adoption-path sibling of
/// <see cref="IBoundDefinitionLookup"/>, which exists for exactly the same reason: so <c>Servyx.Application</c>
/// can consume the game-definition catalog without referencing <c>Servyx.Definitions</c> directly (the
/// project that owns <c>GameDefinitionCatalog</c>) — see that interface's own remarks for why that boundary
/// matters. <c>Servyx.Web</c>'s composition root supplies the real implementation, backed by
/// <c>GameDefinitionCatalog</c>.
/// </summary>
public interface IAdoptionDefinitionCatalog
{
    /// <summary>
    /// One <see cref="DefinitionAdoptionCriteria"/> per currently-loaded definition with a derivable docker
    /// adoption profile — what <see cref="ServerAdoptionService.ListCandidatesAsync"/> discovers against.
    /// Empty when no definition is loaded, or none has a derivable docker profile — an honest empty
    /// candidate list, never a hardcoded fallback.
    /// </summary>
    IReadOnlyList<DefinitionAdoptionCriteria> AllCriteria();

    /// <summary>
    /// The exact <see cref="GameDefinitionRef"/> currently loaded for <paramref name="definitionId"/>, or
    /// <see langword="null"/> if no definition with that id is loaded.
    /// </summary>
    GameDefinitionRef? TryGetRefById(string definitionId);
}
