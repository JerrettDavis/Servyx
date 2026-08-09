namespace Servyx.Domain.Definitions;

/// <summary>
/// What became of resolving a discovered server against the loaded set of game definitions' adoption
/// criteria. See <see cref="IServerDefinitionBindingStore"/> for why this is persisted rather than
/// recomputed from scratch on every read.
/// </summary>
public enum ServerDefinitionBindingState
{
    /// <summary>Exactly one definition matched (directly, or as the unambiguous most-specific winner).</summary>
    Bound,

    /// <summary>Two or more definitions matched with equal specificity — deliberately not resolved further.</summary>
    Ambiguous,

    /// <summary>
    /// This server was previously <see cref="Bound"/> to a <see cref="Servyx.Domain.Definitions.GameDefinitionRef.ContentHash"/>
    /// that no <see cref="Servyx.Definitions.GameDefinitionCatalog"/> lookup can resolve any more (the
    /// definition was edited or removed). Deliberately never silently re-bound to a different version —
    /// see <see cref="IServerDefinitionBindingStore"/>'s remarks.
    /// </summary>
    NeedsRebind,
}

/// <summary>
/// A durable record of which game definition governs a single discovered server, keyed by the discovery-
/// native <see cref="Servyx.Domain.Discovery.DiscoveredServer.ServerId"/> (e.g. a Docker container id) — not
/// yet a stable Servyx <c>Server</c> entity id, per that type's own remarks.
/// </summary>
/// <param name="ServerId">The discovery-native server id this binding is for.</param>
/// <param name="State">Whether resolution landed on exactly one definition, more than one, or a since-orphaned pin.</param>
/// <param name="Definition">
/// The definition this server is pinned to, by content hash, when <paramref name="State"/> is
/// <see cref="ServerDefinitionBindingState.Bound"/> or <see cref="ServerDefinitionBindingState.NeedsRebind"/>
/// (the pin itself is retained even once its hash stops resolving, so the operator can see what it was
/// pinned to). <see langword="null"/> when <see cref="ServerDefinitionBindingState.Ambiguous"/>.
/// </param>
/// <param name="CandidateDefinitionIds">
/// The <c>metadata.id</c> of every definition that matched with equal specificity, named so an ambiguous
/// binding is diagnosable rather than merely flagged. Empty unless <paramref name="State"/> is
/// <see cref="ServerDefinitionBindingState.Ambiguous"/>.
/// </param>
/// <param name="UpdatedAt">When this binding was last written.</param>
public sealed record ServerDefinitionBinding(
    string ServerId,
    ServerDefinitionBindingState State,
    GameDefinitionRef? Definition,
    IReadOnlyList<string> CandidateDefinitionIds,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Durable storage for the <see cref="ServerDefinitionBinding"/> a discovered server resolved to, so a
/// restart or an image retag does not need to (and must not) silently re-derive a possibly different
/// answer. <see cref="GameDefinitionRef.ContentHash"/> is the load-bearing field here: once a server is
/// bound to a specific content hash, that pin is authoritative for as long as the hash still resolves in
/// the catalog — a hot-reloaded or edited definition must never silently change the behaviour of a server
/// already running against the previous content, per <see cref="GameDefinitionRef"/>'s own remarks.
/// </summary>
public interface IServerDefinitionBindingStore
{
    /// <summary>The persisted binding for <paramref name="serverId"/>, or <see langword="null"/> if none has ever been recorded.</summary>
    Task<ServerDefinitionBinding?> TryGetAsync(string serverId, CancellationToken ct = default);

    /// <summary>Writes (creating or overwriting) the binding for <paramref name="binding"/>'s server id.</summary>
    Task SaveAsync(ServerDefinitionBinding binding, CancellationToken ct = default);

    /// <summary>
    /// Removes the binding for <paramref name="serverId"/>, if one exists. A no-op — not an error — when
    /// none does. Used by Servyx's own adoption "forget" path to clean up the binding it recorded at
    /// adoption time once the <c>Server</c> row it belonged to is itself removed, now that the two can be
    /// correlated deterministically by container id (see <c>Servyx.Domain.Entities.Server.ContainerId</c>).
    /// </summary>
    Task RemoveAsync(string serverId, CancellationToken ct = default);
}
