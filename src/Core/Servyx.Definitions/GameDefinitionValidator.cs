using Servyx.Domain.Definitions;

namespace Servyx.Definitions;

/// <summary>
/// The <c>Servyx.Definitions</c> implementation of <see cref="IGameDefinitionValidator"/>, built on
/// <see cref="GameDefinitionYamlParser"/>.
/// </summary>
/// <remarks>
/// <see cref="IGameDefinitionValidator.Validate"/> declares its parameter as <see langword="object"/> —
/// deliberately untyped, per the remarks on <see cref="LoadedDefinition.Document"/>, until this project
/// existed to give it a concrete shape. Line/column-accurate validation is this parser's whole reason to
/// exist over a POCO deserializer (see the remarks on <see cref="GameDefinitionYamlParser"/>), and that
/// accuracy depends on the original YAML node tree — a bare, already-parsed
/// <see cref="Servyx.Domain.Definitions.Model.GameDefinition"/> record carries no such tree, so this
/// implementation accepts raw YAML source instead (a <see cref="string"/> or a <see cref="byte"/>[]) and
/// parses it itself. A caller that already holds a parsed <see cref="Servyx.Domain.Definitions.Model.GameDefinition"/>
/// and only wants its <see cref="ValidationReport"/> should keep the <see cref="DefinitionParseResult"/> from
/// the original <see cref="GameDefinitionYamlParser.Parse(string,string?)"/> call rather than round-trip
/// through this interface.
/// </remarks>
public sealed class GameDefinitionValidator : IGameDefinitionValidator
{
    private readonly GameDefinitionYamlParser _parser = new();

    /// <summary>
    /// Validates raw YAML definition source. <paramref name="document"/> must be a <see cref="string"/> or a
    /// <see cref="byte"/>[] of the definition's own text — anything else throws
    /// <see cref="ArgumentException"/>, since that is a caller-contract violation, not a validation finding
    /// about the definition's content.
    /// </summary>
    public ValidationReport Validate(object document) => document switch
    {
        string yaml => _parser.Parse(yaml).Report,
        byte[] bytes => _parser.Parse(bytes).Report,
        _ => throw new ArgumentException(
            $"{nameof(GameDefinitionValidator)} validates raw YAML source (a string or byte[]); it cannot "
            + $"produce line/column-accurate results from an already-parsed '{document?.GetType().Name ?? "null"}' "
            + "value, which carries no YAML node tree.",
            nameof(document)),
    };
}
