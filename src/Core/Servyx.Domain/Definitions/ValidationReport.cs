namespace Servyx.Domain.Definitions;

/// <summary>Severity of a single <see cref="ValidationIssue"/>.</summary>
public enum ValidationSeverity
{
    /// <summary>A fatal problem; the definition cannot be loaded.</summary>
    Error,

    /// <summary>A non-fatal problem worth surfacing to the definition author.</summary>
    Warning,
}

/// <summary>A single validation finding, including source position for editor integration.</summary>
/// <param name="Message">Human-readable description of the issue.</param>
/// <param name="Line">1-based source line the issue applies to.</param>
/// <param name="Column">1-based source column the issue applies to.</param>
/// <param name="Severity">Whether the issue is fatal.</param>
public sealed record ValidationIssue(string Message, int Line, int Column, ValidationSeverity Severity);

/// <summary>Aggregate result of validating a definition document.</summary>
/// <param name="IsValid">True only when no <see cref="ValidationIssue"/> has <see cref="ValidationSeverity.Error"/> severity.</param>
/// <param name="Issues">All findings, both errors and warnings.</param>
public sealed record ValidationReport(bool IsValid, IReadOnlyList<ValidationIssue> Issues);
