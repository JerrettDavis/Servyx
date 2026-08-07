using Servyx.Domain.Definitions;
using YamlDotNet.RepresentationModel;

namespace Servyx.Definitions;

/// <summary>
/// Accumulates <see cref="ValidationIssue"/>s during a parse. Every overload ultimately resolves to a
/// YAML source position — either a <see cref="YamlNode"/>'s own <see cref="YamlNode.Start"/> mark, or an
/// explicit line/column for the rare document-level issue (an empty file, non-YAML content) that has no
/// node to point at, in which case 1,1 is used.
/// </summary>
/// <remarks>
/// This is the single mechanism by which every parser and validator method in this project reports a
/// problem. Nothing under <c>Servyx.Definitions</c> throws for a content problem — see the remarks on
/// <see cref="GameDefinitionYamlParser.Parse(string,string?)"/> — so every code path that detects one
/// funnels through here instead.
/// </remarks>
internal sealed class ParseIssues
{
    private readonly List<ValidationIssue> _issues = [];

    /// <summary>Every issue recorded so far, in the order it was recorded.</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>True once at least one <see cref="ValidationSeverity.Error"/> has been recorded.</summary>
    public bool HasErrors => _issues.Any(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Records a fatal problem at the given node's source position.</summary>
    public void Error(string message, YamlNode node) =>
        _issues.Add(new ValidationIssue(message, (int)node.Start.Line, (int)node.Start.Column, ValidationSeverity.Error));

    /// <summary>Records a fatal problem at an explicit source position, for the rare case with no node to point at.</summary>
    public void Error(string message, int line, int column) =>
        _issues.Add(new ValidationIssue(message, line, column, ValidationSeverity.Error));

    /// <summary>Records a non-fatal problem at the given node's source position.</summary>
    public void Warning(string message, YamlNode node) =>
        _issues.Add(new ValidationIssue(message, (int)node.Start.Line, (int)node.Start.Column, ValidationSeverity.Warning));

    /// <summary>Records a non-fatal problem at an explicit source position, for the rare case with no node to point at.</summary>
    public void Warning(string message, int line, int column) =>
        _issues.Add(new ValidationIssue(message, line, column, ValidationSeverity.Warning));

    /// <summary>Builds the final <see cref="ValidationReport"/> from everything recorded so far.</summary>
    public ValidationReport ToReport() => new(!HasErrors, _issues);
}
