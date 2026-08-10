using Servyx.Definitions.Tests.Support;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions.Tests;

/// <summary>
/// <see cref="DefinitionImportService"/>: validates before writing, refuses a duplicate id unless
/// explicitly overwritten, refuses an id that cannot be turned into a safe file name (including the
/// path-traversal and reserved-device-name attacks the file name derivation is specifically meant to
/// defeat), and always calls <see cref="GameDefinitionCatalog.RefreshAsync"/> itself rather than relying on
/// <c>DefinitionCatalogRefreshService</c>'s (Development-only-by-default) file watcher.
/// </summary>
public class DefinitionImportServiceTests
{
    private static string ValidYamlWithId(string id) =>
        DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {id}\n");

    private static (DefinitionImportService Service, GameDefinitionCatalog Catalog, TempDefinitionsDirectory Dir) Create()
    {
        var dir = new TempDefinitionsDirectory();
        var provider = new FileSystemGameDefinitionProvider(dir.Root);
        var catalog = new GameDefinitionCatalog([provider]);
        var service = new DefinitionImportService(dir.Root, catalog);
        return (service, catalog, dir);
    }

    [Fact]
    public async Task ValidDefinition_Imports_AndAppearsInTheCatalogAfterRefresh_WithoutASecondManualRefresh()
    {
        var (service, catalog, dir) = Create();
        using var _ = dir;

        catalog.TryGetById("import-valid-game").Should().BeNull("the catalog has never been refreshed yet");

        var result = await service.ImportAsync(ValidYamlWithId("import-valid-game"));

        result.Outcome.Should().Be(DefinitionImportOutcome.Imported);
        result.DefinitionId.Should().Be("import-valid-game");
        File.Exists(result.FilePath).Should().BeTrue();

        // No caller-driven RefreshAsync() call between the import and this read — ImportAsync itself must
        // have called it, proving the import path does not depend on the (Development-only) file watcher.
        var loaded = catalog.TryGetById("import-valid-game");
        loaded.Should().NotBeNull();
        loaded!.Ref.SourcePath.Should().Be(result.FilePath);
    }

    [Fact]
    public async Task InvalidYaml_IsRejected_NotWrittenToDisk_AndSurfacesLineAndColumn()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var result = await service.ImportAsync("apiVersion: servyx.dev/v1\nkind: GameDefinition\nmetadata:\n  id: [unterminated");

        result.Outcome.Should().Be(DefinitionImportOutcome.ValidationFailed);
        result.Report.Should().NotBeNull();
        result.Report!.IsValid.Should().BeFalse();
        result.Report.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error && i.Line > 0 && i.Column > 0);

        Directory.EnumerateFiles(dir.Root, "*.yaml", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownTopLevelField_IsAHardError_AndIsNeverWritten()
    {
        // docs/schema.md: unknown fields are rejected outright, not warned — see
        // GameDefinitionYamlParser's own class remarks on "Unknown-field policy".
        var (service, _, dir) = Create();
        using var _ = dir;

        var yaml = ValidYamlWithId("import-unknown-field") + "\nbogusTopLevelField: true\n";

        var result = await service.ImportAsync(yaml);

        result.Outcome.Should().Be(DefinitionImportOutcome.ValidationFailed);
        result.Report!.Issues.Should().Contain(i => i.Severity == ValidationSeverity.Error);
        Directory.EnumerateFiles(dir.Root, "*.yaml", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateId_WithoutOverwrite_IsRefused_AndDoesNotTouchTheExistingFile()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var first = await service.ImportAsync(ValidYamlWithId("import-dup-game"));
        first.Outcome.Should().Be(DefinitionImportOutcome.Imported);
        var originalBytes = await File.ReadAllBytesAsync(first.FilePath!);

        // Content need not differ for a duplicate-id refusal to trigger — the id alone is the collision.
        var second = await service.ImportAsync(ValidYamlWithId("import-dup-game"), overwrite: false);

        second.Outcome.Should().Be(DefinitionImportOutcome.DuplicateId);
        second.DefinitionId.Should().Be("import-dup-game");
        (await File.ReadAllBytesAsync(first.FilePath!)).Should().BeEquivalentTo(originalBytes, "a refused duplicate must not touch the file already on disk");
    }

    [Fact]
    public async Task DuplicateId_WithExplicitOverwrite_ReplacesTheFile_AndCatalogServesTheNewContent()
    {
        var (service, catalog, dir) = Create();
        using var _ = dir;

        var first = await service.ImportAsync(ValidYamlWithId("import-overwrite-game"));
        first.Outcome.Should().Be(DefinitionImportOutcome.Imported);
        var originalHash = catalog.TryGetById("import-overwrite-game")!.Ref.ContentHash;

        var secondYaml = ValidYamlWithId("import-overwrite-game") + "\n# a trailing comment to change the content hash\n";
        var second = await service.ImportAsync(secondYaml, overwrite: true);

        second.Outcome.Should().Be(DefinitionImportOutcome.Imported);
        second.FilePath.Should().Be(first.FilePath);

        var afterOverwrite = catalog.TryGetById("import-overwrite-game");
        afterOverwrite.Should().NotBeNull();
        afterOverwrite!.Ref.ContentHash.Should().NotBe(originalHash);
    }

    [Fact]
    public async Task TooLargeInput_IsRejected_BeforeParsingOrWriting()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var huge = new string('a', DefinitionImportService.MaxYamlLength + 1);

        var result = await service.ImportAsync(huge);

        result.Outcome.Should().Be(DefinitionImportOutcome.TooLarge);
        result.Report.Should().BeNull("a too-large input must be rejected before it ever reaches the parser");
        Directory.EnumerateFiles(dir.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    // -- Security: path traversal and reserved-name attempts must be refused, never written -------------

    [Fact]
    public async Task SecurityTest_MetadataId_ContainingPathTraversal_IsRefused_AndNothingIsWrittenAnywhere()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var yaml = DefinitionYamlFixture.Mutate("id: palworld\n", "id: \"../../evil\"\n");

        var result = await service.ImportAsync(yaml);

        result.Outcome.Should().Be(DefinitionImportOutcome.UnsafeId);
        result.FilePath.Should().BeNull();

        // Nothing was written inside the sandbox root, and nothing escaped it either — walk two levels
        // above the sandbox root looking for a file this attempt could have planted there.
        Directory.EnumerateFiles(dir.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        var parentOfRoot = Directory.GetParent(dir.Root)!.FullName;
        File.Exists(Path.Combine(parentOfRoot, "evil.yaml")).Should().BeFalse();
        File.Exists(Path.Combine(parentOfRoot, "evil")).Should().BeFalse();
    }

    [Fact]
    public async Task SecurityTest_MetadataId_ContainingASlash_IsRefused_AndNothingIsWritten()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var yaml = DefinitionYamlFixture.Mutate("id: palworld\n", "id: \"sub/evil\"\n");

        var result = await service.ImportAsync(yaml);

        result.Outcome.Should().Be(DefinitionImportOutcome.UnsafeId);
        Directory.EnumerateFiles(dir.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateDirectories(dir.Root).Should().BeEmpty();
    }

    [Theory]
    [InlineData("con")]
    [InlineData("nul")]
    [InlineData("prn")]
    [InlineData("aux")]
    [InlineData("com1")]
    [InlineData("lpt1")]
    public async Task SecurityTest_MetadataId_MatchingAReservedWindowsDeviceName_IsRefused(string reservedId)
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var yaml = DefinitionYamlFixture.Mutate("id: palworld\n", $"id: {reservedId}\n");

        var result = await service.ImportAsync(yaml);

        result.Outcome.Should().Be(DefinitionImportOutcome.UnsafeId);
        result.FilePath.Should().BeNull();
        Directory.EnumerateFiles(dir.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task SecurityTest_MetadataId_ContainingAnAbsoluteWindowsPath_IsRefused()
    {
        var (service, _, dir) = Create();
        using var _ = dir;

        var yaml = DefinitionYamlFixture.Mutate("id: palworld\n", "id: \"C:\\\\evil\"\n");

        var result = await service.ImportAsync(yaml);

        result.Outcome.Should().Be(DefinitionImportOutcome.UnsafeId);
        Directory.EnumerateFiles(dir.Root, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
