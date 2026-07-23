namespace Servyx.Domain.Definitions;

/// <summary>
/// Thrown when <see cref="IGameDefinitionProvider.LoadAsync"/> encounters a definition that fails schema
/// or semantic validation. Carries the same line/column information as <see cref="ValidationIssue"/>.
/// </summary>
public sealed class DefinitionValidationException : Exception
{
    /// <summary>Creates a <see cref="DefinitionValidationException"/> with a default message and no issues.</summary>
    public DefinitionValidationException()
        : base("The definition failed validation.")
    {
        Issues = Array.Empty<ValidationIssue>();
    }

    /// <summary>Creates a <see cref="DefinitionValidationException"/> with the given message and no issues.</summary>
    public DefinitionValidationException(string message) : base(message)
    {
        Issues = Array.Empty<ValidationIssue>();
    }

    /// <summary>Creates a <see cref="DefinitionValidationException"/> with the given message and inner exception.</summary>
    public DefinitionValidationException(string message, Exception innerException) : base(message, innerException)
    {
        Issues = Array.Empty<ValidationIssue>();
    }

    /// <summary>Creates a <see cref="DefinitionValidationException"/> carrying the validation issues that caused it.</summary>
    public DefinitionValidationException(string message, IReadOnlyList<ValidationIssue> issues) : base(message)
    {
        Issues = issues;
    }

    /// <summary>The validation issues that caused this exception.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }
}
