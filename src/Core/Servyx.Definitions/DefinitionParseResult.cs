using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions;

/// <summary>
/// The result of <see cref="GameDefinitionYamlParser.Parse(string,string?)"/>: the typed model, when the
/// document had no <see cref="ValidationSeverity.Error"/>-level issue, alongside the full report.
/// </summary>
/// <param name="Definition">
/// The parsed definition, or <see langword="null"/> if <see cref="Report"/> contains any
/// <see cref="ValidationSeverity.Error"/>. A document with only <see cref="ValidationSeverity.Warning"/>
/// issues still produces a non-null <see cref="Definition"/> — warnings are forward-compatibility notes,
/// not reasons to withhold the model.
/// </param>
/// <param name="Report">Every issue found, errors and warnings alike, each with source position.</param>
public sealed record DefinitionParseResult(GameDefinition? Definition, ValidationReport Report);
