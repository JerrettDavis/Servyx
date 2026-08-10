using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;
using Servyx.Domain.Transport;

namespace Servyx.Definitions;

/// <summary>
/// The sanctioned implementation of <see cref="IDefinitionImportService"/>. Registered unconditionally by
/// the composition root (see <c>Servyx.Composition.ServyxCoreCompositionExtensions</c>'s "Definition
/// import" block) — importing writes only to Servyx's own definitions directory, never to a managed game
/// server, so it needs no write grant and no provisioning gate; operator authentication (enforced above
/// this service, at the host level) is the only gate. See <c>docs/plans/ui-management-surface.md</c>,
/// Phase 5, "Is definition import gated?" for the full reasoning.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Order of operations: parse, then validate the id, then check for a collision, then write, then
/// refresh.</strong> Nothing is ever written before <see cref="GameDefinitionYamlParser.Parse(string,string?)"/>
/// reports a clean <see cref="ValidationReport"/> (no <see cref="ValidationSeverity.Error"/> issue) — see
/// <see cref="ImportAsync"/>'s body. A definition that fails validation never touches disk.
/// </para>
/// <para>
/// <strong>Filename derivation — the core of this type's security posture.</strong> The on-disk file name
/// is <em>always</em> <c>{metadata.id}.yaml</c>, derived from the definition's own already-validated
/// <c>metadata.id</c> — never from <paramref name="sourceName"/>-adjacent input, an uploaded file's own
/// name, or any other operator-controlled string that has not passed through the parser. <c>metadata.id</c>
/// is further required to match <see cref="KebabCaseId"/> (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>, the same strict
/// kebab-case pattern this codebase already uses for safe identifiers elsewhere) before it is allowed
/// anywhere near a file path. That pattern alone cannot contain <c>..</c>, a path separator, a drive
/// letter, a UNC prefix, a null byte, or an NTFS alternate-data-stream colon — so path traversal is refused
/// by construction, not by a blocklist. It does NOT, however, rule out a reserved Windows device name
/// (<c>con</c>, <c>nul</c>, <c>com1</c>, ... are all valid kebab-case strings), which is exactly why the
/// derived file name is still routed through <see cref="SandboxedPathResolver"/> — the same sanctioned
/// factory <c>Servyx.Domain.Transport</c> already uses for every other operator-influenced path in this
/// codebase — rather than trusted on the strength of the regex alone.
/// </para>
/// <para>
/// <strong>Duplicate ids are refused, not silently overwritten.</strong> An import whose id already
/// resolves to a file on disk at the exact path this service would write, or is already the catalog's
/// current entry for that id under a different path, is refused with
/// <see cref="DefinitionImportOutcome.DuplicateId"/> unless the caller explicitly passes
/// <paramref name="overwrite"/><c> = true</c> — see <see cref="ImportAsync"/>'s parameter remarks.
/// Overwriting a shipped or previously-imported definition without an explicit, deliberate confirmation
/// would be exactly the kind of silent data loss this phase's brief calls out as unacceptable. Note the caveat
/// this does NOT solve: <see cref="GameDefinitionCatalog.DefinitionsByContentHash"/> only ever grows (see its
/// own remarks), so an "overwrite" replaces which file <see cref="FileSystemGameDefinitionProvider"/> serves
/// for this id going forward, but any server already pinned to the old content hash keeps resolving it —
/// this is not a true replace of prior in-memory state, only of what a fresh load returns.
/// </para>
/// <para>
/// <strong>A size limit precedes everything.</strong> <see cref="MaxYamlLength"/> is checked before a
/// single byte reaches <see cref="GameDefinitionYamlParser.Parse(string,string?)"/> (and therefore before
/// <c>SafeYamlLoader</c>'s own structural-depth scan runs), so a pathological multi-hundred-megabyte paste
/// is rejected in O(1) rather than after the process has already spent memory tokenizing it.
/// </para>
/// </remarks>
public sealed class DefinitionImportService : IDefinitionImportService
{
    /// <summary>
    /// The strict kebab-case pattern a definition's <c>metadata.id</c> must match before it is used to
    /// derive a file name. Mirrors the safe-identifier pattern already used elsewhere in this codebase (see
    /// <c>KebabCase</c> in <c>Servyx.Mcp</c>). The parser itself does not constrain <c>metadata.id</c>'s
    /// shape — <c>docs/schema.md</c> only requires it be present and non-blank — so this is an
    /// import-specific, security-motivated tightening layered on top of schema validation, not a relaxation
    /// or duplication of it.
    /// </summary>
    private static readonly Regex KebabCaseId = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// The maximum accepted input length, in UTF-16 characters. 1 MiB of text is generously above every
    /// shipped definition (the largest is a few tens of kilobytes) while still bounding how much a single
    /// paste can force this process to hold and scan. Public so a UI caller (e.g. the Games page's upload
    /// control) can apply the same ceiling at read time, before the text ever reaches
    /// <see cref="ImportAsync"/>.
    /// </summary>
    public const int MaxYamlLength = 1_048_576;

    private readonly string _root;
    private readonly GameDefinitionCatalog _catalog;
    private readonly GameDefinitionYamlParser _parser = new();
    private readonly ILogger<DefinitionImportService>? _logger;

    /// <summary>Creates a <see cref="DefinitionImportService"/> rooted at <paramref name="root"/>.</summary>
    /// <param name="root">
    /// The definitions directory to write into. Defaults to <c>{AppContext.BaseDirectory}/definitions</c>
    /// when <see langword="null"/> or blank — the same default <see cref="FileSystemGameDefinitionProvider"/>
    /// and <see cref="ServiceCollectionExtensions.PathConfigKey"/> use, so an import lands exactly where the
    /// provider that will serve it back out already looks.
    /// </param>
    /// <param name="catalog">The catalog to refresh after a successful write, and to consult for duplicate ids.</param>
    /// <param name="logger">Optional logger for write failures.</param>
    public DefinitionImportService(string? root, GameDefinitionCatalog catalog, ILogger<DefinitionImportService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(root)
            ? Path.Combine(AppContext.BaseDirectory, "definitions")
            : root);
        _catalog = catalog;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DefinitionImportResult> ImportAsync(
        string yaml,
        string? sourceName = null,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (yaml.Length > MaxYamlLength)
        {
            return new DefinitionImportResult(
                DefinitionImportOutcome.TooLarge,
                Report: null,
                DefinitionId: null,
                FilePath: null,
                $"The definition text is {yaml.Length:N0} characters, which exceeds the {MaxYamlLength:N0}-character limit. Nothing was parsed or written.");
        }

        var parsed = _parser.Parse(yaml, sourceName);

        if (parsed.Definition is null || !parsed.Report.IsValid)
        {
            return new DefinitionImportResult(
                DefinitionImportOutcome.ValidationFailed,
                parsed.Report,
                DefinitionId: null,
                FilePath: null,
                "The definition failed validation and was not written. See the issues below.");
        }

        var id = parsed.Definition.Metadata.Id;

        if (!KebabCaseId.IsMatch(id))
        {
            return new DefinitionImportResult(
                DefinitionImportOutcome.UnsafeId,
                parsed.Report,
                id,
                FilePath: null,
                $"The definition's id '{id}' is not a safe identifier — it must match '{KebabCaseId}' "
                + "(lowercase letters, digits, and single hyphens between segments) before it can be used "
                + "to name a file. Nothing was written.");
        }

        TargetPath target;
        try
        {
            target = new SandboxedPathResolver(_root).Resolve($"{id}.yaml");
        }
        catch (PathEscapesSandboxException ex)
        {
            // Defense in depth: KebabCaseId already rules out traversal and separators, but not a reserved
            // Windows device name (e.g. id "con" or "nul") — SandboxedPathResolver is what actually catches
            // that. Reaching this catch block for any other reason would mean the regex above stopped being
            // as strict as this remarks section claims; either way, refuse rather than guess.
            _logger?.LogWarning(ex, "Rejected definition id '{Id}' as an unsafe file name: {Message}", id, ex.Message);
            return new DefinitionImportResult(
                DefinitionImportOutcome.UnsafeId,
                parsed.Report,
                id,
                FilePath: null,
                $"The definition's id '{id}' cannot be used as a safe file name: {ex.Message} Nothing was written.");
        }

        var filePath = Path.Combine(_root, target.Value.Replace('/', Path.DirectorySeparatorChar));

        var existingOnDisk = File.Exists(filePath);
        var existingInCatalog = _catalog.TryGetById(id);

        if (!overwrite && (existingOnDisk || existingInCatalog is not null))
        {
            return new DefinitionImportResult(
                DefinitionImportOutcome.DuplicateId,
                parsed.Report,
                id,
                filePath,
                $"A definition with id '{id}' already exists"
                + (existingInCatalog?.Ref.SourcePath is { } existingPath ? $" (currently served from '{existingPath}')" : string.Empty)
                + ". Nothing was written. An imported definition is global — it affects every server that "
                + "matches it — so replacing it requires an explicit confirmation.");
        }

        try
        {
            Directory.CreateDirectory(_root);

            // Written verbatim as UTF-8, no BOM: exactly the bytes that were parsed and validated above, so
            // what is on disk is byte-identical to what passed review — never a re-serialized or
            // re-formatted copy that could drift from what the operator actually pasted.
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(yaml);
            await File.WriteAllBytesAsync(filePath, bytes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(ex, "Failed to write imported definition '{Id}' to '{FilePath}'.", id, filePath);
            return new DefinitionImportResult(
                DefinitionImportOutcome.WriteFailed,
                parsed.Report,
                id,
                filePath,
                $"The definition validated, but could not be written to disk: {ex.Message}");
        }

        await _catalog.RefreshAsync(ct).ConfigureAwait(false);

        // Surface the "silently lost to a bundled/other definition" caveat rather than claim success when
        // the catalog's current entry for this id, post-refresh, is not the file just written — see
        // GameDefinitionCatalog's remarks on cross-provider (and FileSystemGameDefinitionProvider's own
        // ordinal-path) collision precedence.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = _catalog.TryGetById(id);
        var isWinning = current is not null && string.Equals(current.Ref.SourcePath, filePath, comparison);

        if (!isWinning)
        {
            return new DefinitionImportResult(
                DefinitionImportOutcome.ImportedButShadowed,
                parsed.Report,
                id,
                filePath,
                $"'{id}' was written to '{filePath}' and the catalog was refreshed, but a different source "
                + $"currently wins for this id ({(current?.Ref.SourcePath is { } winner ? $"'{winner}'" : "see the catalog's faults")}) "
                + "— this import has no effect until that is resolved.");
        }

        return new DefinitionImportResult(
            DefinitionImportOutcome.Imported,
            parsed.Report,
            id,
            filePath,
            $"'{id}' was imported and is now available in the catalog.");
    }
}
