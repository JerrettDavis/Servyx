using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;
using Servyx.Domain.Definitions.Model;

namespace Servyx.Definitions.Tests;

/// <summary>
/// Discovery, cataloguing, and fault-isolation behaviour of <see cref="FileSystemGameDefinitionProvider"/>.
/// Every fixture definition here is a targeted mutation of the real, shipped
/// <c>definitions/palworld-docker.yaml</c> (see <see cref="DefinitionYamlFixture"/>) with a distinct
/// <c>metadata.id</c>, written into a throwaway temp directory — never the repository's real
/// <c>definitions/</c> folder, per this phase's brief.
/// </summary>
public class FileSystemGameDefinitionProviderTests
{
    // The trailing newline is load-bearing: "id: palworld" (with no anchor) is also a prefix of
    // "id: palworldsettings", the surface id declared by both deployment profiles — a bare substring
    // replace would corrupt those, breaking a `bindings[].surface` reference and failing validation for an
    // unrelated reason. Anchoring on the line ending keeps the substitution to the metadata id alone.
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    private static string ValidYamlWithIdAndName(string id, string name) =>
        ValidYamlWithId(id).Replace(
            "name: Palworld Dedicated Server\n",
            $"name: {name}\n",
            StringComparison.Ordinal);

    [Fact]
    public async Task ListAsync_RealShippedDefinitionsDirectory_ListsAndLoadsPalworld()
    {
        var repoRoot = RepoRootLocator.Find();
        var definitionsDir = Path.Combine(repoRoot.FullName, "definitions");
        var provider = new FileSystemGameDefinitionProvider(definitionsDir);

        var refs = await provider.ListAsync();

        var palworldRef = refs.Should().ContainSingle(r => r.Id == "palworld").Subject;
        provider.Faults.Should().BeEmpty();

        var loaded = await provider.LoadAsync(palworldRef);

        loaded.Ref.Id.Should().Be("palworld");
        var definition = loaded.Document.Should().BeOfType<GameDefinition>().Subject;
        definition.Metadata.Name.Should().Be("Palworld Dedicated Server");
    }

    [Fact]
    public async Task ListAsync_SeveralValidDefinitions_ListsAllOfThem()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("a.yaml", ValidYamlWithId("game-a"));
        dir.WriteFlat("b.yaml", ValidYamlWithId("game-b"));
        dir.WriteFlat("c.yaml", ValidYamlWithId("game-c"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var refs = await provider.ListAsync();

        refs.Select(r => r.Id).Should().BeEquivalentTo(["game-a", "game-b", "game-c"]);
        provider.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_TwoValidOneMalformed_LoadsTheTwoGood_RecordsOneFaultWithPosition()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("good-1.yaml", ValidYamlWithId("game-good-1"));
        dir.WriteFlat("good-2.yaml", ValidYamlWithId("game-good-2"));
        var badPath = dir.WriteFlat("bad.yaml", "apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated");

        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var refs = await provider.ListAsync();

        refs.Should().HaveCount(2);
        refs.Select(r => r.Id).Should().BeEquivalentTo(["game-good-1", "game-good-2"]);

        provider.Faults.Should().ContainSingle();
        var fault = provider.Faults[0];
        fault.Path.Should().Be(badPath);
        fault.Line.Should().NotBeNull();
        fault.Column.Should().NotBeNull();

        // Both good definitions still fully load — the malformed sibling never took them down with it.
        foreach (var reference in refs)
        {
            var loaded = await provider.LoadAsync(reference);
            loaded.Document.Should().BeOfType<GameDefinition>();
        }
    }

    [Fact]
    public async Task ListAsync_DuplicateId_PicksLowestPathOrdinal_RecordsLoserAsFault_DeterministicallyAcrossRuns()
    {
        using var dir = new TempDefinitionsDirectory();
        var pathA = dir.WriteFlat("a-first.yaml", ValidYamlWithId("dup-game"));
        var pathB = dir.WriteFlat("z-second.yaml", ValidYamlWithId("dup-game"));

        var winner = string.CompareOrdinal(pathA, pathB) <= 0 ? pathA : pathB;
        var loser = winner == pathA ? pathB : pathA;

        for (var run = 0; run < 3; run++)
        {
            var provider = new FileSystemGameDefinitionProvider(dir.Root);

            var refs = await provider.ListAsync();

            var winningRef = refs.Should().ContainSingle(r => r.Id == "dup-game").Subject;
            provider.Faults.Should().ContainSingle(f => f.Path == loser && f.Message.Contains("Duplicate", StringComparison.Ordinal));

            var loaded = await provider.LoadAsync(winningRef);
            var typed = loaded.Document.Should().BeOfType<GameDefinition>().Subject;

            // Every run resolves to the same, deterministically-chosen winner file — the loser fault above
            // and this successful load together prove which file that is, independent of whatever order
            // Directory.EnumerateFiles happened to return.
            typed.Metadata.Id.Should().Be("dup-game");
        }
    }

    [Fact]
    public async Task ListAsync_UnknownApiVersion_IsAFault_NotAnException()
    {
        using var dir = new TempDefinitionsDirectory();
        var path = dir.WriteFlat("future.yaml", DefinitionYamlFixture.Mutate("apiVersion: servyx.dev/v1", "apiVersion: servyx.dev/v2"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var act = () => provider.ListAsync();
        var refs = await act.Should().NotThrowAsync();

        refs.Subject.Should().BeEmpty();
        provider.Faults.Should().ContainSingle(f => f.Path == path && f.Message.Contains("apiVersion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_PathologicallyNestedFileInTheDirectory_IsAFault_NeverKillsTheProcess()
    {
        // The exact vector a prior review round found bypassing the parser's own depth guard entirely:
        // FileSystemGameDefinitionProvider peeks a file's header (TryReadHeader) during listing, BEFORE
        // GameDefinitionYamlParser ever runs — a malicious file merely sitting in a watched directory must
        // not crash the host the moment it is listed. TryReadHeader is now routed through the same
        // SafeYamlLoader chokepoint the full parser uses, so this must degrade to a DefinitionFault, not an
        // uncatchable StackOverflowException killing the whole test process.
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("good.yaml", ValidYamlWithId("game-good"));

        var builder = new System.Text.StringBuilder("apiVersion: servyx.dev/v1\nkind: GameDefinition\ndeepField:\n");
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(' ', i * 2).Append("- \n");
        }

        var maliciousPath = dir.WriteFlat("malicious.yaml", builder.ToString());

        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var act = () => provider.ListAsync();
        var refs = await act.Should().NotThrowAsync();

        // The good sibling still lists — one pathological file never takes the whole directory down with it.
        refs.Subject.Should().ContainSingle(r => r.Id == "game-good");

        provider.Faults.Should().ContainSingle(f =>
            f.Path == maliciousPath && f.Message.Contains("exceeds the maximum supported depth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_BundleLayout_IsDiscoveredEquivalentlyToFlatLayout()
    {
        using var dir = new TempDefinitionsDirectory();
        dir.WriteFlat("flat.yaml", ValidYamlWithId("flat-game"));
        dir.WriteBundle("mygame", ValidYamlWithId("bundle-game"));

        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var refs = await provider.ListAsync();

        refs.Select(r => r.Id).Should().BeEquivalentTo(["flat-game", "bundle-game"]);
        provider.Faults.Should().BeEmpty();

        var bundleRef = refs.Single(r => r.Id == "bundle-game");
        var loaded = await provider.LoadAsync(bundleRef);
        loaded.Document.Should().BeOfType<GameDefinition>().Which.Metadata.Id.Should().Be("bundle-game");
    }

    [Fact]
    public async Task ListAsync_NonExistentDirectory_ReturnsEmpty_NeverThrows()
    {
        var missing = Path.Combine(Path.GetTempPath(), "servyx-definitions-tests-missing-" + Guid.NewGuid().ToString("N"));
        var provider = new FileSystemGameDefinitionProvider(missing);

        var act = () => provider.ListAsync();

        var refs = await act.Should().NotThrowAsync();
        refs.Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_EmptyDirectory_ReturnsEmpty_NeverThrows()
    {
        using var dir = new TempDefinitionsDirectory();
        var provider = new FileSystemGameDefinitionProvider(dir.Root);

        var act = () => provider.ListAsync();

        var refs = await act.Should().NotThrowAsync();
        refs.Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_RootThatIsActuallyAFile_DegradesToEmpty_NeverThrows()
    {
        // A root that exists but is not a usable directory (a Windows-safe, non-flaky stand-in for "the
        // directory cannot be enumerated" — a genuine ACL-denied directory is not reliably reproducible in
        // a portable test). Directory.Exists is false for a file path, so this exercises the same
        // "does not resolve to a real directory" code path as a missing root, without ever throwing.
        using var dir = new TempDefinitionsDirectory();
        var filePath = dir.WriteFlat("not-a-directory.yaml", ValidYamlWithId("irrelevant"));
        var provider = new FileSystemGameDefinitionProvider(filePath);

        var act = () => provider.ListAsync();

        var refs = await act.Should().NotThrowAsync();
        refs.Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task ContentHash_SameBytes_SameHash_ChangedBytes_ChangedHash()
    {
        using var dir = new TempDefinitionsDirectory();
        var yaml = ValidYamlWithId("hash-game");
        dir.WriteFlat("one.yaml", yaml);

        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var firstHash = (await provider.ListAsync()).Single().ContentHash;

        // Re-listing the exact same bytes yields the exact same hash.
        var secondHash = (await provider.ListAsync()).Single().ContentHash;
        secondHash.Should().Be(firstHash);

        // Changing the content (a distinct name, still valid) changes the hash.
        dir.WriteFlat("one.yaml", ValidYamlWithIdAndName("hash-game", "Palworld Dedicated Server (modified)"));
        var thirdHash = (await provider.ListAsync()).Single().ContentHash;
        thirdHash.Should().NotBe(firstHash);
    }
}
