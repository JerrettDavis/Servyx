using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// <see cref="GameDefinitionCatalog"/>'s atomic-swap refresh, fault aggregation, and the rule that a reload
/// which fails validation must never evict a previously-good version — see the class remarks on
/// <see cref="GameDefinitionCatalog"/> for the guarantee this proves.
/// </summary>
public class GameDefinitionCatalogTests
{
    // Anchored on the line ending so this never also matches the "id: palworldsettings" surface id declared
    // by both deployment profiles — see the identical comment in FileSystemGameDefinitionProviderTests.
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    [Fact]
    public async Task RefreshAsync_PopulatesByIdAndByContentHash()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("game.yaml", ValidYamlWithId("catalog-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();

        var loaded = catalog.TryGetById("catalog-game");
        loaded.Should().NotBeNull();

        var byHash = catalog.TryGetByContentHash(loaded!.Ref.ContentHash);
        byHash.Should().NotBeNull();
        byHash!.Metadata.Id.Should().Be("catalog-game");

        catalog.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_MalformedSibling_IsIsolatedAsAFault_GoodDefinitionStillCatalogued()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("good.yaml", ValidYamlWithId("catalog-good"));
        dir.WriteFlat("bad.yaml", "apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated");

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();

        catalog.TryGetById("catalog-good").Should().NotBeNull();
        catalog.Faults.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshAsync_ReloadFailsValidation_KeepsPreviousGoodVersion_RecordsFault()
    {
        using var dir = new TempDefinitionsDirectory();
        var path = dir.WriteFlat("game.yaml", ValidYamlWithId("catalog-reload-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();
        var originalLoaded = catalog.TryGetById("catalog-reload-game");
        originalLoaded.Should().NotBeNull();
        var originalHash = originalLoaded!.Ref.ContentHash;

        // Break something deep enough that the header (apiVersion/kind/metadata.id) still reads fine — so
        // this id is still listed — but the full parse fails semantic validation: an unrecognized top-level
        // key, which GameDefinitionYamlParser treats as a hard Error (see its class remarks).
        var broken = ValidYamlWithId("catalog-reload-game") + "\nbogusTopLevelField: true\n";
        File.WriteAllText(path, broken);

        await catalog.RefreshAsync();

        var afterFailedReload = catalog.TryGetById("catalog-reload-game");
        afterFailedReload.Should().NotBeNull();
        afterFailedReload!.Ref.ContentHash.Should().Be(originalHash, "a reload that fails validation must leave the previous good version in place");

        catalog.Faults.Should().ContainSingle(f => f.Message.Contains("catalog-reload-game", StringComparison.Ordinal));

        // The previously-good content is still resolvable by its own hash — pinned-server lookups survive.
        catalog.TryGetByContentHash(originalHash).Should().NotBeNull();
    }

    [Fact]
    public async Task RefreshAsync_ContentHashIndex_RetainsSupersededVersions_AcrossAHotReload()
    {
        using var dir = new TempDefinitionsDirectory();
        var path = dir.WriteFlat("game.yaml", ValidYamlWithId("catalog-pin-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);

        await catalog.RefreshAsync();
        var firstHash = catalog.TryGetById("catalog-pin-game")!.Ref.ContentHash;

        File.WriteAllText(path, ValidYamlWithId("catalog-pin-game").Replace(
            "version: 1.0.0", "version: 2.0.0", StringComparison.Ordinal));

        await catalog.RefreshAsync();
        var secondHash = catalog.TryGetById("catalog-pin-game")!.Ref.ContentHash;

        secondHash.Should().NotBe(firstHash);

        // A server that pinned the first version can still resolve it by content hash, even though
        // "current" has moved on.
        catalog.TryGetByContentHash(firstHash).Should().NotBeNull();
        catalog.TryGetByContentHash(secondHash).Should().NotBeNull();
    }

    [Fact]
    public void TryGetById_UnknownId_ReturnsNull()
    {
        var catalog = new GameDefinitionCatalog([]);

        catalog.TryGetById("nope").Should().BeNull();
        catalog.TryGetByContentHash("nope").Should().BeNull();
    }

    /// <summary>
    /// The TOCTOU window between a provider's <c>ListAsync</c> seeing a file and its <c>LoadAsync</c> call
    /// actually reading it: the file is deleted in between, and <c>LoadAsync</c> throws
    /// <see cref="FileNotFoundException"/>. A <see cref="FakeGameDefinitionProvider"/> is used because a
    /// real <c>FileSystemGameDefinitionProvider</c> cannot be made to hit this race deterministically — its
    /// own <c>LoadAsync</c> re-resolves the path from disk, so there is no reliable way to delete the file
    /// at exactly the right instant from a test.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_FileDeletedBetweenListAndLoad_EvictsRatherThanResurrects()
    {
        var reference = new GameDefinitionRef("toctou-game", "hash-1", "fake");
        var definition = ParseValidDefinition("toctou-game");
        var trust = new TrustVerdict(TrustTier.Unverified, Array.Empty<string>(), null);

        var provider = new FakeGameDefinitionProvider
        {
            SourceId = "fake",
            OnList = (_) => Task.FromResult<IReadOnlyList<GameDefinitionRef>>([reference]),
            OnLoad = (_, _) => Task.FromResult(new LoadedDefinition(reference, trust, definition)),
        };
        var catalog = new GameDefinitionCatalog([provider]);

        // First refresh: the file exists, LoadAsync succeeds, the definition is catalogued.
        await catalog.RefreshAsync();
        catalog.TryGetById("toctou-game").Should().NotBeNull();

        // Second refresh: ListAsync still sees the (about-to-be-deleted) file and returns the same
        // reference, but by the time LoadAsync runs, it is gone — exactly the race FileSystemGameDefinitionProvider.LoadAsync
        // hits when a file disappears between the two calls.
        provider.OnLoad = (_, _) => throw new FileNotFoundException("The file was deleted.", "toctou-game.yaml");

        await catalog.RefreshAsync();

        catalog.TryGetById("toctou-game").Should().BeNull("a definition whose file disappeared between listing and loading must be evicted, not resurrected from the previous snapshot");
        catalog.Faults.Should().ContainSingle(f => f.Message.Contains("toctou-game", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordFaultAsync_AppendsAFault_VisibleImmediately_WithoutTouchingById()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("game.yaml", ValidYamlWithId("record-fault-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        await catalog.RefreshAsync();
        catalog.Faults.Should().BeEmpty();

        await catalog.RecordFaultAsync(new DefinitionFault("external-source", "something external broke", null, null));

        catalog.Faults.Should().ContainSingle(f => f.Path == "external-source");
        catalog.TryGetById("record-fault-game").Should().NotBeNull("recording an out-of-band fault must not disturb the existing catalogued definitions");
    }

    private static GameDefinition ParseValidDefinition(string id)
    {
        var result = new GameDefinitionYamlParser().Parse(ValidYamlWithId(id));
        return result.Definition ?? throw new InvalidOperationException("Fixture YAML failed to parse: " + string.Join("; ", result.Report.Issues.Select(i => i.Message)));
    }
}
