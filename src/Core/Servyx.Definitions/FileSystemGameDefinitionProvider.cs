using System.Text;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// Discovers <c>servyx.dev/v1</c> <c>GameDefinition</c> YAML files under a directory — flat files and
/// bundle directories alike — and loads them on demand.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never throws from <see cref="ListAsync"/>.</strong> A malformed file, an unreadable file, an
/// unrecognized <c>apiVersion</c>, or a duplicate <c>metadata.id</c> all degrade to a
/// <see cref="DefinitionFault"/> recorded in <see cref="Faults"/> rather than an exception — see the class
/// remarks on <see cref="GameDefinitionYamlParser"/> for the same posture one layer down, and
/// <c>PalworldDefinitionLoader.TryLoad</c> for the milestone-1 precedent this generalizes: one bad file must
/// never hide the good ones, let alone crash the host.
/// </para>
/// <para>
/// <strong>Listing is cheap on purpose.</strong> <see cref="ListAsync"/> hashes the raw bytes and reads
/// only <c>apiVersion</c>, <c>kind</c>, and <c>metadata.id</c> off the YAML node tree — it never runs
/// <see cref="GameDefinitionYamlParser"/>'s full walk (typed model construction plus every schema and
/// semantic rule), which <see cref="LoadAsync"/> does. A catalogue of a few hundred definitions can be
/// listed without fully parsing every one of them.
/// </para>
/// <para>
/// <strong>Duplicate ids.</strong> Two files that declare the same <c>metadata.id</c> are resolved
/// deterministically by ordinal comparison of their paths — the lexicographically lowest path wins — never
/// by "whichever the filesystem enumerated last", which is not reproducible across platforms or even across
/// runs on the same platform. The losing file is recorded as a fault naming both paths.
/// </para>
/// <para>
/// <strong>Trust evaluation is out of scope for this phase.</strong> <see cref="IDefinitionTrustEvaluator"/>
/// has no implementation yet; every <see cref="LoadedDefinition"/> this provider produces is
/// <see cref="TrustTier.Unverified"/> unless an evaluator is supplied to the constructor — the seam a later
/// phase wires through without this class changing.
/// </para>
/// </remarks>
public sealed partial class FileSystemGameDefinitionProvider : IGameDefinitionProvider, IDefinitionCatalogDiagnostics
{
    /// <summary>The <see cref="IGameDefinitionProvider.SourceId"/> this provider always reports.</summary>
    public const string DefaultSourceId = "directory";

    private const string SupportedApiVersion = "servyx.dev/v1";
    private const string BundleFileName = "definition.yaml";

    private readonly string _root;
    private readonly GameDefinitionYamlParser _parser = new();
    private readonly IDefinitionTrustEvaluator? _trustEvaluator;
    private readonly ILogger<FileSystemGameDefinitionProvider>? _logger;
    private volatile IReadOnlyList<DefinitionFault> _faults = Array.Empty<DefinitionFault>();

    /// <summary>
    /// Creates a provider rooted at <paramref name="root"/>.
    /// </summary>
    /// <param name="root">
    /// The directory to enumerate. Need not exist yet — a missing directory lists as empty, not a fault.
    /// Defaults to <c>{AppContext.BaseDirectory}/definitions</c>, matching
    /// <c>Servyx:Definitions:Path</c>'s own default in <see cref="ServiceCollectionExtensions.AddServyxDefinitions"/>.
    /// </param>
    /// <param name="trustEvaluator">
    /// Assigns a trust tier to each loaded definition. Trust evaluation is out of scope for this phase:
    /// <see langword="null"/> (the default) makes every definition <see cref="TrustTier.Unverified"/>, the
    /// safest reading of "not yet evaluated". This parameter is the seam a later phase wires a real
    /// evaluator through without this provider changing.
    /// </param>
    /// <param name="logger">Optional logger for degraded-but-non-fatal conditions.</param>
    public FileSystemGameDefinitionProvider(
        string? root = null,
        IDefinitionTrustEvaluator? trustEvaluator = null,
        ILogger<FileSystemGameDefinitionProvider>? logger = null)
    {
        _root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(AppContext.BaseDirectory, "definitions")
            : root;
        _trustEvaluator = trustEvaluator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceId => DefaultSourceId;

    /// <summary>The directory this provider enumerates.</summary>
    public string RootDirectory => _root;

    /// <inheritdoc />
    public IReadOnlyList<DefinitionFault> Faults => _faults;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default)
    {
        var faults = new List<DefinitionFault>();
        IReadOnlyList<GameDefinitionRef> result;

        try
        {
            result = await ListCoreAsync(faults, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defensive backstop: this method's contract is "never throw" (see the class remarks). Every
            // anticipated failure mode is already handled inside ListCoreAsync; this exists for the
            // unanticipated one, so a bug here degrades to "no definitions, one fault" rather than an
            // exception a caller relying on the documented contract never expected to see.
            _logger?.LogError(ex, "Unexpected failure while listing game definitions under '{Root}'.", _root);
            faults.Add(new DefinitionFault(_root, $"Unexpected failure while listing definitions: {ex.Message}", null, null));
            result = Array.Empty<GameDefinitionRef>();
        }

        // Published only once the whole run has finished, so a concurrent read of Faults never observes a
        // partial list from a run still in progress.
        _faults = faults;
        return result;
    }

    private async Task<IReadOnlyList<GameDefinitionRef>> ListCoreAsync(List<DefinitionFault> faults, CancellationToken ct)
    {
        List<string> files;
        try
        {
            files = DiscoverFiles();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not enumerate the definitions directory '{Root}'.", _root);
            faults.Add(new DefinitionFault(_root, $"Could not enumerate the definitions directory: {ex.Message}", null, null));
            return Array.Empty<GameDefinitionRef>();
        }

        var candidates = new List<(string Path, string Id, string ContentHash)>();

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                faults.Add(new DefinitionFault(path, $"Could not read the file: {ex.Message}", null, null));
                continue;
            }

            var contentHash = ComputeContentHash(bytes);

            if (!TryReadHeader(bytes, out var header, out var errorMessage, out var line, out var column))
            {
                faults.Add(new DefinitionFault(path, errorMessage ?? "The file could not be parsed as YAML.", line, column));
                continue;
            }

            if (string.IsNullOrWhiteSpace(header.Id))
            {
                faults.Add(new DefinitionFault(path, "The definition declares no 'metadata.id'.", null, null));
                continue;
            }

            if (!string.Equals(header.ApiVersion, SupportedApiVersion, StringComparison.Ordinal))
            {
                faults.Add(new DefinitionFault(
                    path,
                    $"Unsupported apiVersion '{header.ApiVersion ?? "(none)"}'; only '{SupportedApiVersion}' is recognized.",
                    null,
                    null));
                continue;
            }

            candidates.Add((path, header.Id, contentHash));
        }

        var refs = new List<GameDefinitionRef>();

        // Grouped and ordered by id so the returned list itself is deterministic — independent of the
        // filesystem's own (unspecified) enumeration order — not just the winner-per-id choice below.
        foreach (var group in candidates.GroupBy(c => c.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
            var winner = ordered[0];
            refs.Add(new GameDefinitionRef(winner.Id, winner.ContentHash, SourceId, winner.Path));

            foreach (var loser in ordered.Skip(1))
            {
                faults.Add(new DefinitionFault(
                    loser.Path,
                    $"Duplicate definition id '{loser.Id}': '{winner.Path}' takes precedence (ordinal path "
                    + $"comparison) over '{loser.Path}'.",
                    null,
                    null));
            }
        }

        return refs;
    }

    /// <inheritdoc />
    /// <exception cref="DefinitionValidationException">
    /// The file's content fails schema or semantic validation, carrying the full <see cref="ValidationReport"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// No file under <see cref="RootDirectory"/> currently declares <c>metadata.id</c> equal to
    /// <paramref name="reference"/>'s <see cref="GameDefinitionRef.Id"/> — the file was moved or deleted
    /// since the reference was produced.
    /// </exception>
    public async Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var path = ResolvePath(reference.Id)
            ?? throw new FileNotFoundException(
                $"No definition file for id '{reference.Id}' was found under '{_root}'.");

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        // Recomputed from what is actually on disk right now, rather than trusting the caller's
        // (potentially stale, e.g. from a hot-reload signal that raced a further edit) ContentHash. SourcePath
        // is likewise refreshed to the path just resolved, rather than inherited from a possibly-stale
        // reference (e.g. one produced by ListAsync before a since-superseded rename).
        var actualHash = ComputeContentHash(bytes);
        var actualRef = reference with { ContentHash = actualHash, SourcePath = path };

        var parsed = _parser.Parse(bytes, path);
        if (parsed.Definition is null || !parsed.Report.IsValid)
        {
            throw new DefinitionValidationException(
                $"Definition '{reference.Id}' at '{path}' failed validation.",
                parsed.Report.Issues);
        }

        var seedTrust = new TrustVerdict(
            TrustTier.Unverified,
            Array.Empty<string>(),
            "Trust evaluation is not implemented in this phase; every definition is treated as Unverified.");

        var seed = new LoadedDefinition(actualRef, seedTrust, parsed.Definition);
        var trust = _trustEvaluator?.Evaluate(seed) ?? seedTrust;

        return seed with { Trust = trust };
    }

    /// <summary>
    /// Finds the current file for <paramref name="id"/>, applying the same duplicate-id precedence as
    /// <see cref="ListCoreAsync"/> (lowest path, ordinal) if more than one file currently declares it.
    /// </summary>
    private string? ResolvePath(string id)
    {
        List<string> files;
        try
        {
            files = DiscoverFiles();
        }
        catch
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (var path in files)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch
            {
                continue;
            }

            if (TryReadHeader(bytes, out var header, out _, out _, out _)
                && string.Equals(header.Id, id, StringComparison.Ordinal))
            {
                candidates.Add(path);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort(StringComparer.Ordinal);
        return candidates[0];
    }

    /// <summary>
    /// Enumerates both recognized layouts: flat <c>*.yaml</c> files directly under <see cref="RootDirectory"/>,
    /// and bundle directories one level deep, each contributing their own <c>definition.yaml</c>.
    /// </summary>
    private List<string> DiscoverFiles()
    {
        var results = new List<string>();

        if (!Directory.Exists(_root))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*.yaml", SearchOption.TopDirectoryOnly))
        {
            results.Add(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly))
        {
            var bundleFile = Path.Combine(directory, BundleFileName);
            if (File.Exists(bundleFile))
            {
                results.Add(bundleFile);
            }
        }

        return results;
    }

    private static string ComputeContentHash(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>
    /// Reads only <c>apiVersion</c>, <c>kind</c>, and <c>metadata.id</c> off the YAML node tree — never the
    /// full document. Never throws: every failure (invalid UTF-8, invalid YAML, a non-mapping root) is
    /// reported through the <see langword="out"/> parameters, mirroring <see cref="GameDefinitionYamlParser.Parse(string,string?)"/>'s
    /// own never-throw posture one layer down.
    /// </summary>
    private static bool TryReadHeader(
        byte[] bytes,
        out DefinitionHeader header,
        out string? errorMessage,
        out int? line,
        out int? column)
    {
        header = default;
        errorMessage = null;
        line = null;
        column = null;

        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(bytes);

        if (string.IsNullOrWhiteSpace(text))
        {
            errorMessage = "The file is empty.";
            return false;
        }

        // Routed through SafeYamlLoader — the one place in this project allowed to call YamlStream.Load — so
        // this header peek gets the same structural-depth pre-scan the full parser does. Listing a directory
        // is exactly the code path that reaches a definition file first, before GameDefinitionYamlParser ever
        // sees it: a second, independently-written YamlStream.Load here would leave that path unguarded.
        if (!SafeYamlLoader.TryLoad(text, "file", out var stream, out errorMessage, out line, out column) || stream is null)
        {
            return false;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            errorMessage = "The file's root is not a YAML mapping.";
            return false;
        }

        var apiVersion = root.TryGet("apiVersion", out var apiNode) && apiNode is YamlScalarNode apiScalar
            ? apiScalar.Value
            : null;
        var kind = root.TryGet("kind", out var kindNode) && kindNode is YamlScalarNode kindScalar
            ? kindScalar.Value
            : null;

        string? id = null;
        if (root.TryGet("metadata", out var metadataNode) && metadataNode is YamlMappingNode metadataMap
            && metadataMap.TryGet("id", out var idNode) && idNode is YamlScalarNode idScalar)
        {
            id = idScalar.Value;
        }

        header = new DefinitionHeader(apiVersion, kind, id);
        return true;
    }

    private readonly record struct DefinitionHeader(string? ApiVersion, string? Kind, string? Id);
}
