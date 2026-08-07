using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Lifecycle;

namespace Servyx.Application.Servers;

/// <summary>
/// The per-definition data <see cref="ServerQueryService"/> needs once a server's binding has been
/// resolved to a specific <see cref="Servyx.Domain.Definitions.GameDefinitionRef"/>: its display name, its
/// settings catalogue, and its lifecycle block (for <see cref="LifecycleDefinition.HealthSignal"/>).
/// </summary>
public sealed record BoundDefinitionData(string GameId, string GameName, IReadOnlyList<SettingGroup> Settings, LifecycleDefinition Lifecycle);

/// <summary>
/// Resolves a definition's content hash to the data <see cref="ServerQueryService"/> needs to render a
/// server bound to it. Exists so <c>Servyx.Application</c> can consume per-server definition data without
/// referencing <c>Servyx.Definitions</c> (the project that owns <c>GameDefinitionCatalog</c>) — the same
/// "consume Domain abstractions only" boundary <see cref="IServerQueryService"/>'s own remarks describe.
/// <c>Servyx.Web</c>'s composition root supplies the real implementation, backed by
/// <c>GameDefinitionCatalog.TryGetByContentHash</c>.
/// </summary>
public interface IBoundDefinitionLookup
{
    /// <summary>
    /// The data for the definition whose content hash is <paramref name="contentHash"/>, or
    /// <see langword="null"/> if this process has never successfully loaded content with that hash (or has
    /// no record of it, e.g. a fresh process with an empty catalog). A <see langword="null"/> result for a
    /// hash a server was previously bound to is exactly the "pinned content hash no longer present" case —
    /// see <see cref="Servyx.Domain.Definitions.ServerDefinitionBindingState.NeedsRebind"/>.
    /// </summary>
    BoundDefinitionData? TryGetByContentHash(string contentHash);
}
