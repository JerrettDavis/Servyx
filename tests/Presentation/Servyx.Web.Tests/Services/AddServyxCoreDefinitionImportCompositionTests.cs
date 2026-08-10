using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;
using Servyx.Definitions;
using Servyx.Domain.Definitions;
using Servyx.Web.Tests.Documentation;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Proves Phase 5's steer is actually wired the way it is documented: <see cref="IDefinitionImportService"/>
/// resolves on a fresh, provisioning-gate-closed install — the same "unconditional, not behind
/// <c>Servyx:Provisioning:Enabled</c>" placement <c>AddServyxCoreAdoptionCompositionTests</c> pins for server
/// adoption — and a real import against that composition actually reaches the catalog. See
/// <c>ServyxCoreCompositionExtensions</c>'s "Game definition import (Phase 5)" remarks for the reasoning.
/// </summary>
public class AddServyxCoreDefinitionImportCompositionTests
{
    private static (HostApplicationBuilder Builder, string DefinitionsDir) BuildFreshInstallBuilder()
    {
        var definitionsDir = Directory.CreateTempSubdirectory("servyx-web-tests-import-defs-");
        var repoDefinitionsDir = Path.Combine(RepoRootLocator.Find().FullName, "definitions");

        // Pinned to palworld-docker.yaml specifically (rather than "the first *.yaml file", whose
        // enumeration order is unspecified) because the second test below mutates this exact file's known
        // "id: palworld" line.
        var source = Path.Combine(repoDefinitionsDir, "palworld-docker.yaml");
        File.Copy(source, Path.Combine(definitionsDir.FullName, Path.GetFileName(source)));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsDir.FullName;

        // Servyx:Provisioning:Enabled is deliberately left unset — the default, gate-closed configuration a
        // fresh install actually ships with. Import must work here, not only once provisioning is opened.
        return (builder, definitionsDir.FullName);
    }

    [Fact]
    public void A_fresh_gate_closed_install_resolves_the_definition_import_service()
    {
        var (builder, _) = BuildFreshInstallBuilder();

        var composition = builder.AddServyxCore(NullLoggerFactory.Instance);
        composition.Provisioning.Enabled.Should().BeFalse();

        using var host = builder.Build();

        host.Services.GetRequiredService<IDefinitionImportService>().Should().NotBeNull();
    }

    [Fact]
    public async Task An_import_against_the_composed_host_writes_the_file_and_the_shared_catalog_observes_it()
    {
        var (builder, definitionsDir) = BuildFreshInstallBuilder();
        builder.AddServyxCore(NullLoggerFactory.Instance);
        using var host = builder.Build();

        var import = host.Services.GetRequiredService<IDefinitionImportService>();
        var catalog = host.Services.GetRequiredService<GameDefinitionCatalog>();

        // A full, realistic document (every top-level block is required — see GameDefinitionYamlParser's
        // ParseRoot), not a hand-trimmed minimal one: reuses the exact bundled definition this fresh install
        // already loaded (see BuildFreshInstallBuilder), mutated only in its id — the same "everything except
        // one deliberately-changed piece is exactly what ships" fixture technique
        // Servyx.Definitions.Tests.Support.DefinitionYamlFixture uses.
        var bundledYaml = File.ReadAllText(Directory.EnumerateFiles(definitionsDir, "*.yaml").Single());
        const string findId = "id: palworld\n";
        bundledYaml.Should().Contain(findId, "the fixture mutation below must actually match something");
        var yaml = bundledYaml.Replace(findId, "id: composition-import-game\n", StringComparison.Ordinal);

        var result = await import.ImportAsync(yaml);

        // Not asserting Imported specifically: the bundled definition's own id may or may not collide, and
        // that is not what this test is about. What matters is that the write landed inside the configured
        // directory and the SAME catalog instance every other consumer resolves now knows about it — proving
        // this is not a second, disconnected catalog.
        result.Outcome.Should().BeOneOf(DefinitionImportOutcome.Imported, DefinitionImportOutcome.ImportedButShadowed);
        result.FilePath.Should().StartWith(definitionsDir);
        File.Exists(result.FilePath).Should().BeTrue();

        catalog.DefinitionsByContentHash.Values
            .Should().Contain(d => d.Metadata.Id == "composition-import-game");
    }
}
