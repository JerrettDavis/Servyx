using Servyx.Domain.Definitions;

namespace Servyx.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row backing <see cref="IServerDefinitionBindingStore"/>: which game definition (by content
/// hash) governs a single discovered server, keyed by the discovery-native
/// <see cref="Servyx.Domain.Discovery.DiscoveredServer.ServerId"/> rather than any Servyx-minted id — see
/// that type's own remarks on why no such stable id exists yet for an adopted-but-not-provisioned server.
/// </summary>
/// <remarks>
/// No foreign key to <c>Servers</c>/<c>Hosts</c>, deliberately, for the same reason
/// <c>ProvisionedResourceRecord</c> carries none: this row is written before, and independently of, whether
/// a Servyx <c>Server</c> entity for this container will ever exist, and it must survive that entity's
/// absence or removal rather than being constrained to it.
/// </remarks>
public sealed class ServerDefinitionBindingRecord
{
    /// <summary>The discovery-native server id (e.g. a Docker container id) this binding is for.</summary>
    public required string ServerId { get; set; }

    /// <summary>Whether resolution landed on exactly one definition, more than one, or a since-orphaned pin.</summary>
    public required ServerDefinitionBindingState State { get; set; }

    /// <summary>
    /// The bound (or previously bound, if <see cref="State"/> is <see cref="ServerDefinitionBindingState.NeedsRebind"/>)
    /// definition's <c>metadata.id</c>. Null only when <see cref="State"/> is <see cref="ServerDefinitionBindingState.Ambiguous"/>.
    /// </summary>
    public string? DefinitionId { get; set; }

    /// <summary>
    /// The content hash this server is pinned to — the field that actually anchors behaviour across a
    /// restart or a hot-reloaded definition edit. See <see cref="IServerDefinitionBindingStore"/>'s remarks.
    /// </summary>
    public string? DefinitionContentHash { get; set; }

    /// <summary>The <see cref="GameDefinitionRef.SourceId"/> the pinned definition was loaded from.</summary>
    public string? DefinitionSourceId { get; set; }

    /// <summary>The <see cref="GameDefinitionRef.SourcePath"/> the pinned definition was loaded from, if the provider has one.</summary>
    public string? DefinitionSourcePath { get; set; }

    /// <summary>
    /// The <c>metadata.id</c> of every definition tied for most-specific match, so an
    /// <see cref="ServerDefinitionBindingState.Ambiguous"/> row is diagnosable. Empty for every other state.
    /// </summary>
    public required IReadOnlyList<string> CandidateDefinitionIds { get; set; }

    /// <summary>When this binding was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; set; }
}
