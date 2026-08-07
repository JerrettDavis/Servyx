using System.Text;
using Servyx.Domain.Definitions.Model;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// Parses a <c>servyx.dev/v1</c> <c>GameDefinition</c> YAML document into the typed
/// <see cref="Servyx.Domain.Definitions.Model.GameDefinition"/> shape, and validates it against the
/// schema and semantic rules described in <c>docs/schema.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Built directly over YamlDotNet's <see cref="YamlStream"/>/<see cref="YamlMappingNode"/> representation
/// model rather than its <c>Deserializer.Deserialize&lt;T&gt;()</c> POCO path. This is deliberate: POCO
/// deserialization discards each value's source position, and <see cref="Servyx.Domain.Definitions.ValidationIssue"/>'s
/// <c>Line</c>/<c>Column</c> fields exist specifically so a definition author's editor can jump straight to
/// the offending node. A parser that cannot see node positions cannot make that field real.
/// </para>
/// <para>
/// Every method under this class reports problems through <see cref="ParseIssues"/> and never throws for a
/// content problem — a malformed field, an unknown section, a catastrophic-backtracking regex, a 10MB file,
/// non-UTF8 bytes, duplicate keys, truncated YAML. <see cref="Parse(string,string?)"/> additionally wraps
/// the whole walk in a catch-all, so a construct this parser did not anticipate degrades to a single
/// generic <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/> issue rather than an unhandled
/// exception reaching the caller — <see cref="Servyx.Domain.Definitions.IGameDefinitionProvider.LoadAsync"/>
/// is documented to throw <see cref="Servyx.Domain.Definitions.DefinitionValidationException"/> for a bad
/// definition, never let one crash the host.
/// </para>
/// <para>
/// <strong>Unknown-field policy.</strong> <c>docs/schema.md</c>'s "Validation Rules" section states unknown
/// fields are rejected outright ("Unknown fields are rejected, not warned"), citing a misspelled
/// security-relevant key (<c>privleged</c> for <c>privileged</c>) as the motivating failure mode. This
/// parser follows that written rule: an unrecognized field within a known block, and an unrecognized
/// top-level section, are both <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/>, not
/// <see cref="Servyx.Domain.Definitions.ValidationSeverity.Warning"/>. Reported to the calling coordinator
/// as a flagged conflict: a forward-compatibility argument for Warning (a v1 parser meeting a definition
/// written for a later minor version should degrade, not refuse) was also on the table, but the doc's
/// stricter rule is the project's own prior, written decision, and predates this phase.
/// </para>
/// <para>
/// The one exception is the <c>signature</c> top-level key: <c>docs/schema.md</c> reserves it for a future
/// cryptographic-provenance block, "declared only", with <see cref="Servyx.Domain.Definitions.IDefinitionTrustEvaluator"/>
/// unimplemented. This parser recognizes <c>signature</c> as a legal top-level key — so a definition that
/// declares one does not fail the unknown-top-level-section rule — but does not parse its contents at all,
/// because neither the doc nor the shipped YAML gives it a concrete field shape, and
/// <see cref="Servyx.Domain.Definitions.Model.GameDefinition"/> (which this project must not modify) has no
/// slot to carry it. A definition declaring <c>signature</c> gets a Warning noting the block is present but
/// unparsed and unverified — deliberately so nothing downstream mistakes "the key was recognized" for "the
/// signature was checked".
/// </para>
/// </remarks>
public sealed partial class GameDefinitionYamlParser
{
    private static readonly IReadOnlySet<string> TopLevelKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "apiVersion", "kind", "metadata", "capabilities", "deployments", "lifecycle",
        "control", "settings", "backup", "saves", "mods", "signature",
    };

    private const string SupportedApiVersion = "servyx.dev/v1";
    private const string SupportedKind = "GameDefinition";

    /// <summary>
    /// Parses raw definition bytes, decoding as UTF-8 with replacement-character fallback rather than
    /// throwing on invalid sequences — a non-UTF8 file becomes a YAML/structural
    /// <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/> reported through the normal report,
    /// not a decoding exception.
    /// </summary>
    public DefinitionParseResult Parse(byte[] rawBytes, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(rawBytes);
        return Parse(text, sourceName);
    }

    /// <summary>
    /// Parses a definition document's text. Never throws: every failure mode — empty input, invalid YAML,
    /// a non-mapping root, an internal parser fault — is captured as an
    /// <see cref="Servyx.Domain.Definitions.ValidationSeverity.Error"/> in <see cref="DefinitionParseResult.Report"/>.
    /// </summary>
    public DefinitionParseResult Parse(string yaml, string? sourceName = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var issues = new ParseIssues();

        if (string.IsNullOrWhiteSpace(yaml))
        {
            issues.Error("The definition is empty.", 1, 1);
            return new DefinitionParseResult(null, issues.ToReport());
        }

        // SafeYamlLoader is the ONE place in this project allowed to call YamlStream.Load — see its remarks
        // for why a second, independent call site is a bug. It runs the structural-depth pre-scan BEFORE
        // anything reaches YamlDotNet's recursive-descent scanner, since a deeply nested document can
        // overflow the process stack — uncatchable in .NET — which would otherwise defeat every try/catch
        // below.
        if (!SafeYamlLoader.TryLoad(yaml, "definition", out var stream, out var loadError, out var loadLine, out var loadColumn) || stream is null)
        {
            issues.Error(loadError ?? "The definition could not be loaded.", loadLine ?? 1, loadColumn ?? 1);
            return new DefinitionParseResult(null, issues.ToReport());
        }

        if (stream.Documents.Count == 0)
        {
            issues.Error("The definition contains no YAML document.", 1, 1);
            return new DefinitionParseResult(null, issues.ToReport());
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            issues.Error("The definition's root must be a YAML mapping.", (int)stream.Documents[0].RootNode.Start.Line, (int)stream.Documents[0].RootNode.Start.Column);
            return new DefinitionParseResult(null, issues.ToReport());
        }

        try
        {
            var definition = ParseRoot(root, issues);
            return new DefinitionParseResult(issues.HasErrors ? null : definition, issues.ToReport());
        }
        catch (Exception ex)
        {
            // Defensive backstop: no single unanticipated node shape should let an exception escape a
            // validator whose entire job is to characterize untrusted content, not crash on it.
            issues.Error($"Unexpected error while parsing the definition: {ex.Message}", 1, 1);
            return new DefinitionParseResult(null, issues.ToReport());
        }
    }

    private GameDefinition? ParseRoot(YamlMappingNode root, ParseIssues issues)
    {
        RejectUnknownKeys(root, TopLevelKeys, issues, "The definition");

        var apiVersion = RequireString(root, "apiVersion", issues, "The definition");
        if (apiVersion is not null && !string.Equals(apiVersion, SupportedApiVersion, StringComparison.Ordinal))
        {
            root.TryGet("apiVersion", out var apiVersionNode);
            issues.Error($"Unsupported apiVersion '{apiVersion}'; only '{SupportedApiVersion}' is recognized.", apiVersionNode);
        }

        var kind = RequireString(root, "kind", issues, "The definition");
        if (kind is not null && !string.Equals(kind, SupportedKind, StringComparison.Ordinal))
        {
            root.TryGet("kind", out var kindNode);
            issues.Error($"Unsupported kind '{kind}'; only '{SupportedKind}' is recognized.", kindNode);
        }

        if (root.TryGet("signature", out var signatureNode))
        {
            issues.Warning(
                "'signature' is declared but not parsed or verified by this phase of the parser — "
                + "IDefinitionTrustEvaluator has no implementation yet, so this block currently has no effect "
                + "on the assigned trust tier. Do not treat its mere presence as a checked signature.",
                signatureNode);
        }

        var state = new ParseState(issues);

        var metadata = ParseMetadata(root, issues);
        var capabilities = ParseCapabilities(root, issues, state);
        var deployments = ParseDeployments(root, issues, state);
        var lifecycle = ParseLifecycle(root, issues, state);
        var control = ParseControl(root, issues, state);
        var settings = ParseSettings(root, issues, state);
        var backup = ParseBackup(root, issues, state);
        var saves = ParseSaves(root, issues, state);
        var mods = ParseMods(root, issues);

        ResolveDeferredChecks(state);

        if (apiVersion is null || kind is null || metadata is null || capabilities is null
            || deployments is null || deployments.Count == 0 || lifecycle is null || control is null
            || settings is null || backup is null || mods is null)
        {
            return null;
        }

        return new GameDefinition(apiVersion, metadata, capabilities, deployments, lifecycle, control, settings, backup, saves, mods);
    }
}
