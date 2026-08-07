namespace Servyx.Domain.Definitions;

/// <summary>
/// A reference to a specific definition by content, not by mutable version. Servers pin
/// <see cref="ContentHash"/>, the SHA-256 of the definition's raw bytes, never a human-readable version
/// string — a mutated catalog entry cannot silently change a running server's behaviour.
/// </summary>
/// <param name="Id">Stable identifier for the game (e.g. "palworld").</param>
/// <param name="ContentHash">SHA-256 of the definition's raw bytes.</param>
/// <param name="SourceId">Identifier of the <see cref="IGameDefinitionProvider"/> this reference came from.</param>
/// <param name="SourcePath">
/// Where this reference's definition actually lives, in whatever form is meaningful for the originating
/// <see cref="IGameDefinitionProvider"/> — a filesystem path for <c>FileSystemGameDefinitionProvider</c>, for
/// example. <see langword="null"/> when the provider has no single-file notion of origin (a hypothetical
/// database- or network-backed provider), in which case a consumer that needs to name "where" — most
/// notably a <c>DefinitionFault</c> produced while loading this reference — falls back to
/// <c>"{SourceId}:{Id}"</c>. Providers that do have a real path are expected to always populate this rather
/// than leave callers to reconstruct it.
/// </param>
public sealed record GameDefinitionRef(string Id, string ContentHash, string SourceId, string? SourcePath = null);

/// <summary>
/// A fully parsed and validated definition, ready for use. <see cref="Document"/> is intentionally
/// untyped in this milestone; the concrete schema tree is fleshed out in <c>Servyx.Definitions</c>.
/// </summary>
/// <param name="Ref">The reference this definition was loaded from.</param>
/// <param name="Trust">The trust verdict assigned to this definition.</param>
/// <param name="Document">The parsed definition document.</param>
public sealed record LoadedDefinition(GameDefinitionRef Ref, TrustVerdict Trust, object Document);
