using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// <see cref="DefinitionFault.Path"/> must always identify the real definition file when one exists — the
/// whole point of the fault card <c>/games</c> renders is telling a third-party definition author which file
/// to open. Before this suite, <see cref="GameDefinitionCatalog"/> synthesized <c>"{sourceId}:{id}"</c> for
/// every fault it recorded while loading a winning reference (semantic validation failures, cross-provider
/// duplicate ids, disappeared files, unexpected load errors) — the real path <see cref="FileSystemGameDefinitionProvider.ListAsync"/>
/// already knew was simply discarded. These tests pin that the real path now flows through
/// <see cref="GameDefinitionRef.SourcePath"/> into every one of those faults, with the synthesized form kept
/// only as the documented fallback for a provider that genuinely has no single-file notion of origin.
/// </summary>
public class DefinitionFaultPathTests
{
    // See the identical anchoring comment in FileSystemGameDefinitionProviderTests: the trailing newline
    // keeps the substitution from also touching "id: palworldsettings" (the surface id both deployment
    // profiles declare).
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    [Fact]
    public async Task RefreshAsync_SemanticValidationError_FaultPath_IsRealFile_WithLineAndColumn()
    {
        using var dir = new TempDefinitionsDirectory();

        // A dangling backup.quiesce channel reference — syntactically fine YAML, schema-valid shape, but a
        // semantic rule (GameDefinitionYamlParser.Semantics's PendingChannelCommandRefs check) rejects it
        // because no 'control.channels' entry declares 'bogus-channel'.
        var yaml = ValidYamlWithId("dangling-channel-game").Replace(
            "- { kind: control, channel: rcon, command: save, timeout: 30s }",
            "- { kind: control, channel: bogus-channel, command: save, timeout: 30s }",
            StringComparison.Ordinal);
        var path = dir.WriteFlat("dangling-channel.yaml", yaml);

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();

        catalog.TryGetById("dangling-channel-game").Should().BeNull("the definition failed validation and was never previously good");

        var fault = catalog.Faults.Should().ContainSingle().Subject;
        fault.Path.Should().Be(path, "the fault must name the real file, not a synthesized 'sourceId:id' identifier");
        fault.Message.Should().Contain("dangling-channel-game");

        // Independently re-parse the same bytes to find the actual offending Error and its source position —
        // the fixture also happens to trip several pre-existing Warnings (template-token and backup.adopt
        // notices) earlier in parse order, so the fault's Line/Column must specifically match the Error, not
        // merely "some issue somewhere in the file".
        var parsed = new GameDefinitionYamlParser().Parse(File.ReadAllBytes(path), path);
        var channelError = parsed.Report.Issues.Should().ContainSingle(
            i => i.Severity == ValidationSeverity.Error && i.Message.Contains("bogus-channel", StringComparison.Ordinal)).Subject;

        fault.Line.Should().Be(channelError.Line, "the fault must point at the offending 'channel: bogus-channel' node, not an earlier, unrelated warning");
        fault.Column.Should().Be(channelError.Column);
    }

    [Fact]
    public async Task RefreshAsync_SyntaxError_FaultPath_IsRealFile_WithLineAndColumn()
    {
        using var dir = new TempDefinitionsDirectory();
        var path = dir.WriteFlat("bad.yaml", "apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated");

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();

        var fault = catalog.Faults.Should().ContainSingle().Subject;
        fault.Path.Should().Be(path);
        fault.Line.Should().NotBeNull();
        fault.Column.Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshAsync_DuplicateIdWithinOneProvider_FaultNamesBothFiles()
    {
        using var dir = new TempDefinitionsDirectory();
        var pathA = dir.WriteFlat("a-first.yaml", ValidYamlWithId("dup-same-provider"));
        var pathB = dir.WriteFlat("z-second.yaml", ValidYamlWithId("dup-same-provider"));

        var winner = string.CompareOrdinal(pathA, pathB) <= 0 ? pathA : pathB;
        var loser = winner == pathA ? pathB : pathA;

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();

        catalog.TryGetById("dup-same-provider").Should().NotBeNull();

        var fault = catalog.Faults.Should().ContainSingle().Subject;
        fault.Path.Should().Be(loser, "the losing file is what the fault is 'about'");
        fault.Message.Should().Contain(winner, "the winning file must also be named so the author can see which one 'won'");
    }

    [Fact]
    public async Task RefreshAsync_DuplicateIdAcrossProviders_FaultNamesBothRealPaths()
    {
        // Two independently-rooted FileSystemGameDefinitionProvider instances, standing in for two different
        // registered providers (e.g. a first-party directory and a second, lower-priority one) that both
        // happen to declare the same metadata.id. Provider order is priority order — the first provider's
        // file wins.
        using var winningDir = new TempDefinitionsDirectory();
        using var losingDir = new TempDefinitionsDirectory();

        var winningPath = winningDir.WriteFlat("winner.yaml", ValidYamlWithId("dup-cross-provider"));
        var losingPath = losingDir.WriteFlat("loser.yaml", ValidYamlWithId("dup-cross-provider"));

        var winningProvider = new FileSystemGameDefinitionProvider(winningDir.Root);
        var losingProvider = new FileSystemGameDefinitionProvider(losingDir.Root);
        var catalog = new GameDefinitionCatalog([winningProvider, losingProvider]);

        await catalog.RefreshAsync();

        catalog.TryGetById("dup-cross-provider").Should().NotBeNull();

        var fault = catalog.Faults.Should().ContainSingle(f => f.Message.Contains("Duplicate", StringComparison.Ordinal)).Subject;
        fault.Path.Should().Be(losingPath, "the fault is 'about' the losing provider's real file, not a synthesized 'sourceId:id' identifier");
        fault.Message.Should().Contain(winningPath, "the winning provider's real file must also be named so both sides of the collision are legible");
    }

    [Fact]
    public async Task RefreshAsync_ValidationFailure_ProviderWithNoSourcePath_FallsBackToSourceIdAndId_NeverCrashes()
    {
        // The hypothetical non-filesystem provider the brief calls out: it has no single-file notion of
        // origin at all, so GameDefinitionRef.SourcePath is null. The fault must still be produced, never
        // throw, and fall back to the documented "{sourceId}:{id}" identifier rather than an empty or null
        // Path.
        var reference = new GameDefinitionRef("no-path-game", "hash-1", "memory");
        reference.SourcePath.Should().BeNull("this is exactly the 'no originating path' case the fallback exists for");

        var provider = new FakeNoPathProvider(
            "memory",
            reference,
            new DefinitionValidationException(
                "Definition 'no-path-game' failed validation.",
                [new ValidationIssue("Something is wrong.", 3, 5, ValidationSeverity.Error)]));

        var catalog = new GameDefinitionCatalog([provider]);

        var act = () => catalog.RefreshAsync();
        await act.Should().NotThrowAsync();

        var fault = catalog.Faults.Should().ContainSingle().Subject;
        fault.Path.Should().Be("memory:no-path-game");
    }

    /// <summary>
    /// A minimal, purpose-built <see cref="IGameDefinitionProvider"/> (rather than reusing
    /// <c>FakeGameDefinitionProvider</c>) so this test reads standalone: it always lists exactly one
    /// path-less reference and always fails to load it with a supplied exception.
    /// </summary>
    private sealed class FakeNoPathProvider(string sourceId, GameDefinitionRef reference, Exception loadFailure)
        : IGameDefinitionProvider
    {
        public string SourceId { get; } = sourceId;

        public Task<IReadOnlyList<GameDefinitionRef>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameDefinitionRef>>([reference]);

        public Task<LoadedDefinition> LoadAsync(GameDefinitionRef reference, CancellationToken ct = default) =>
            throw loadFailure;

        public async IAsyncEnumerable<GameDefinitionRef> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
