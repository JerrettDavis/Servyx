namespace Servyx.Definitions;

/// <summary>
/// A single reason one definition file is currently unavailable — a parse failure, a semantic validation
/// error, an unrecognized <c>apiVersion</c>, a duplicate <c>metadata.id</c>, or a file that could not be
/// read at all.
/// </summary>
/// <remarks>
/// This is what a later phase's UI renders as a "this definition failed to load" card, so every producer of
/// a <see cref="DefinitionFault"/> is expected to say enough for an author to actually fix the file — which
/// file, why, and where in it, when a position is known.
/// </remarks>
/// <param name="Path">
/// The definition file (or, for a fault that has no single file — e.g. an id collision between two
/// providers — a synthesized identifier naming what the fault concerns) this fault is about.
/// </param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Line">1-based source line, if this fault points at a specific location in the file.</param>
/// <param name="Column">1-based source column, if this fault points at a specific location in the file.</param>
public sealed record DefinitionFault(string Path, string Message, int? Line, int? Column);

/// <summary>
/// Surfaces every reason a definition is currently unavailable to a catalog consumer. Implemented by
/// <see cref="FileSystemGameDefinitionProvider"/> (faults from its own most recent listing) and by
/// <see cref="GameDefinitionCatalog"/> (the aggregate across every registered provider plus load-time
/// faults), so a caller that only cares about "what's broken right now" can depend on this interface alone
/// rather than on either concrete type.
/// </summary>
public interface IDefinitionCatalogDiagnostics
{
    /// <summary>Every fault recorded during the most recently completed listing/refresh.</summary>
    IReadOnlyList<DefinitionFault> Faults { get; }
}
