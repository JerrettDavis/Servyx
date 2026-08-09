using Servyx.Application.Servers;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Composition;

/// <summary>
/// The composition root's <see cref="IAdoptionDefinitionCatalog"/> implementation, backed by
/// <see cref="GameDefinitionCatalog"/> — the adoption-path sibling of <see cref="CatalogBoundDefinitionLookup"/>,
/// which exists for exactly the same "consume Servyx.Definitions without Servyx.Application referencing it"
/// reason. See <see cref="IAdoptionDefinitionCatalog"/>'s own remarks for why that boundary matters.
/// </summary>
public sealed class CatalogAdoptionDefinitionLookup : IAdoptionDefinitionCatalog
{
    private readonly GameDefinitionCatalog _catalog;

    /// <summary>Creates a lookup resolving adoption criteria/definition ids against <paramref name="catalog"/>.</summary>
    public CatalogAdoptionDefinitionLookup(GameDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
    }

    /// <inheritdoc />
    public IReadOnlyList<DefinitionAdoptionCriteria> AllCriteria() =>
        AdoptionCriteriaFactory.DeriveAll(
            _catalog.DefinitionsById.Values
                .Select(loaded => (loaded.Ref, Definition: loaded.Document as GameDefinition))
                .Where(pair => pair.Definition is not null)
                .Select(pair => (pair.Ref, Definition: pair.Definition!)));

    /// <inheritdoc />
    public GameDefinitionRef? TryGetRefById(string definitionId) =>
        _catalog.TryGetById(definitionId)?.Ref;
}
