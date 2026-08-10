using Servyx.Definitions;

namespace Servyx.Web.Tests.Fakes;

/// <summary>
/// A controllable <see cref="IDefinitionImportService"/> fake for <c>ImportDefinitionDialog</c> bUnit
/// tests. Every call is recorded in <see cref="Calls"/>; the result returned is
/// <see cref="ResultFactory"/>'s answer when set, or a fixed "imported" result otherwise.
/// </summary>
public sealed class FakeDefinitionImportService : IDefinitionImportService
{
    /// <summary>Every <c>(yaml, sourceName, overwrite)</c> tuple <see cref="ImportAsync"/> was called with, in call order.</summary>
    public List<(string Yaml, string? SourceName, bool Overwrite)> Calls { get; } = [];

    /// <summary>Overrides the result <see cref="ImportAsync"/> returns; defaults to always reporting success.</summary>
    public Func<string, string?, bool, DefinitionImportResult>? ResultFactory { get; set; }

    /// <inheritdoc />
    public Task<DefinitionImportResult> ImportAsync(
        string yaml,
        string? sourceName = null,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        Calls.Add((yaml, sourceName, overwrite));

        var result = ResultFactory?.Invoke(yaml, sourceName, overwrite)
            ?? new DefinitionImportResult(
                DefinitionImportOutcome.Imported,
                Report: null,
                DefinitionId: "fake-game",
                FilePath: "fake-game.yaml",
                "'fake-game' was imported and is now available in the catalog.");

        return Task.FromResult(result);
    }
}
