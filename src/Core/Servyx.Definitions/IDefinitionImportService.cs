using Servyx.Domain.Definitions;

namespace Servyx.Definitions;

/// <summary>
/// Imports an operator-supplied <c>servyx.dev/v1</c> <c>GameDefinition</c> YAML document into Servyx's own
/// definitions directory (<see cref="ServiceCollectionExtensions.PathConfigKey"/>), then refreshes
/// <see cref="GameDefinitionCatalog"/> so it is picked up without waiting for the (Development-only) file
/// watcher. This is the first — and, at the time this interface was introduced, only — code path in this
/// project that writes an operator-supplied file to disk, so every implementation MUST validate before
/// writing and MUST confine the write to the configured definitions directory. See
/// <see cref="DefinitionImportService"/> for the sanctioned implementation and its security remarks.
/// </summary>
public interface IDefinitionImportService
{
    /// <summary>
    /// Validates <paramref name="yaml"/> and, only if it validates cleanly, writes it into the definitions
    /// directory and refreshes the catalog. Never throws for a content problem — every failure mode is
    /// reported through <see cref="DefinitionImportResult.Outcome"/>, mirroring
    /// <see cref="GameDefinitionYamlParser.Parse(string,string?)"/>'s own never-throw posture one layer
    /// down.
    /// </summary>
    /// <param name="yaml">The raw YAML text pasted or uploaded by the operator.</param>
    /// <param name="sourceName">
    /// A human-readable label for error messages (e.g. an uploaded file's name). Purely cosmetic — it is
    /// never used to derive the on-disk file name; see <see cref="DefinitionImportService"/>'s remarks for
    /// why.
    /// </param>
    /// <param name="overwrite">
    /// When <see langword="false"/> (the default) and a definition with the same <c>metadata.id</c> already
    /// exists, the import is refused with <see cref="DefinitionImportOutcome.DuplicateId"/> rather than
    /// silently replacing it. Pass <see langword="true"/> only after the operator has explicitly confirmed
    /// they want to replace the existing definition.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<DefinitionImportResult> ImportAsync(
        string yaml,
        string? sourceName = null,
        bool overwrite = false,
        CancellationToken ct = default);
}

/// <summary>Every outcome <see cref="IDefinitionImportService.ImportAsync"/> can report.</summary>
public enum DefinitionImportOutcome
{
    /// <summary>The definition validated cleanly, was written, and the catalog now serves it as-is.</summary>
    Imported,

    /// <summary>
    /// The definition validated cleanly and was written, but after refreshing, the catalog's current entry
    /// for this id points at a different file — the import lost a cross-provider or same-directory id
    /// collision (see <see cref="GameDefinitionCatalog"/>'s remarks on provider priority and
    /// <see cref="FileSystemGameDefinitionProvider"/>'s ordinal-path precedence). The file was written and
    /// is on disk, but it is not the version currently in effect.
    /// </summary>
    ImportedButShadowed,

    /// <summary>The input exceeds the accepted size limit and was rejected before parsing.</summary>
    TooLarge,

    /// <summary>
    /// <see cref="GameDefinitionYamlParser.Parse(string,string?)"/> reported at least one
    /// <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/>-level issue. Nothing was written.
    /// </summary>
    ValidationFailed,

    /// <summary>
    /// The definition parsed and validated, but its <c>metadata.id</c> is not a safe filename — see
    /// <see cref="DefinitionImportService"/>'s remarks for the exact pattern required. Nothing was written.
    /// </summary>
    UnsafeId,

    /// <summary>
    /// A definition with this id already exists (on disk at the exact path this service would write, or
    /// already loaded in the catalog under a different path) and <c>overwrite</c> was not set. Nothing was
    /// written.
    /// </summary>
    DuplicateId,

    /// <summary>The definition validated and passed every safety check, but the write itself failed (disk full, permissions, etc.).</summary>
    WriteFailed,
}

/// <summary>The result of a single <see cref="IDefinitionImportService.ImportAsync"/> call.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Report">
/// The full validation report, when parsing ran far enough to produce one (every outcome except
/// <see cref="DefinitionImportOutcome.TooLarge"/>). Render every issue's line, column, severity, and
/// message — do not collapse this to a single string.
/// </param>
/// <param name="DefinitionId">The definition's <c>metadata.id</c>, when parsing got far enough to read it.</param>
/// <param name="FilePath">The on-disk path written (or that would be written), when one was determined.</param>
/// <param name="Message">A human-readable summary, always populated.</param>
public sealed record DefinitionImportResult(
    DefinitionImportOutcome Outcome,
    ValidationReport? Report,
    string? DefinitionId,
    string? FilePath,
    string Message);
