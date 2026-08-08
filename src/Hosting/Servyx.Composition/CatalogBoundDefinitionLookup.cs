using Servyx.Application.Servers;
using Servyx.Definitions;

namespace Servyx.Composition;

/// <summary>
/// The composition root's <see cref="IBoundDefinitionLookup"/> implementation, backed by
/// <see cref="GameDefinitionCatalog.TryGetByContentHash"/>. Exists specifically so
/// <c>Servyx.Application</c> can consume per-server definition data (see <see cref="ServerQueryService"/>'s
/// multi-definition constructor) without referencing <c>Servyx.Definitions</c> — see
/// <see cref="IBoundDefinitionLookup"/>'s own remarks for why that boundary matters.
/// </summary>
public sealed class CatalogBoundDefinitionLookup : IBoundDefinitionLookup
{
    private readonly GameDefinitionCatalog _catalog;

    /// <summary>Creates a lookup resolving content hashes against <paramref name="catalog"/>.</summary>
    public CatalogBoundDefinitionLookup(GameDefinitionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
    }

    /// <inheritdoc />
    public BoundDefinitionData? TryGetByContentHash(string contentHash)
    {
        var definition = _catalog.TryGetByContentHash(contentHash);
        return definition is null
            ? null
            : new BoundDefinitionData(definition.Metadata.Id, definition.Metadata.Name, definition.Settings, definition.Lifecycle);
    }
}
