namespace Servyx.Domain.Definitions;

/// <summary>
/// Supplies game definitions from a particular origin. Multiple providers may be registered; the
/// aggregate catalogue is their union.
/// </summary>
public interface IGameDefinitionProvider
{
    /// <summary>"builtin" | "directory" | "git" | "http-catalog".</summary>
    string SourceId { get; }

    /// <summary>Lists all definition references available from this provider.</summary>
    Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Loads and validates a specific definition. Throws <see cref="DefinitionValidationException"/>,
    /// with YAML line/column information, if the definition fails schema or semantic validation.
    /// </summary>
    Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default);

    /// <summary>
    /// Watches this provider's source for changes, yielding updated references as they appear, to
    /// support hot reload during development.
    /// </summary>
    IAsyncEnumerable<GameDefinitionRef> WatchAsync(CancellationToken ct = default);
}

/// <summary>Validates a definition document against the schema and semantic rules.</summary>
public interface IGameDefinitionValidator
{
    /// <summary>Validates the given parsed definition document.</summary>
    ValidationReport Validate(object document);
}

/// <summary>
/// Assigns a trust tier to a loaded definition. This is the single chokepoint through which all trust
/// decisions pass, regardless of which <see cref="IGameDefinitionProvider"/> supplied the definition.
/// </summary>
public interface IDefinitionTrustEvaluator
{
    /// <summary>Evaluates the trust tier and permitted capabilities for the given definition.</summary>
    TrustVerdict Evaluate(LoadedDefinition definition);
}
