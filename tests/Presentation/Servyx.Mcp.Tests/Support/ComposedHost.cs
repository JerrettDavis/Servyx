using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Servyx.Composition;

namespace Servyx.Mcp.Tests.Support;

/// <summary>
/// Builds a real <see cref="ServyxCoreComposition"/> via the same <c>AddServyxCore</c> extension every host
/// calls — mirroring <c>tests/Presentation/Servyx.Web.Tests/Services/AddServyxCoreBootstrapLoggerTests.cs</c>
/// — with <c>Servyx:Definitions:Path</c> pointed at a controlled directory, so a test can ask for exactly
/// zero, one, or several loaded game definitions without touching the real composition root's construction
/// logic (which is <see langword="internal"/> and deliberately not reflected around).
/// </summary>
internal static class ComposedHost
{
    /// <summary>The repo's real bundled definitions — four <c>*.yaml</c> files — used to force <see cref="DefinitionCatalogMode.Multiple"/>.</summary>
    public static string RepoDefinitionsDirectory => Path.Combine(RepoRootLocator.Find().FullName, "definitions");

    /// <summary>Builds a composition with zero game definitions loaded (<see cref="DefinitionCatalogMode.None"/>).</summary>
    public static ServyxCoreComposition BuildWithNoDefinitions()
    {
        var empty = Directory.CreateTempSubdirectory("servyx-mcp-tests-none-");
        return Build(empty.FullName);
    }

    /// <summary>Builds a composition with exactly one game definition loaded (<see cref="DefinitionCatalogMode.Single"/>).</summary>
    public static ServyxCoreComposition BuildWithOneDefinition()
    {
        var dir = Directory.CreateTempSubdirectory("servyx-mcp-tests-single-");
        var source = Directory.EnumerateFiles(RepoDefinitionsDirectory, "*.yaml").First();
        File.Copy(source, Path.Combine(dir.FullName, Path.GetFileName(source)));
        return Build(dir.FullName);
    }

    /// <summary>Builds a composition with two or more game definitions loaded (<see cref="DefinitionCatalogMode.Multiple"/>).</summary>
    public static ServyxCoreComposition BuildWithMultipleDefinitions() => Build(RepoDefinitionsDirectory);

    private static ServyxCoreComposition Build(string definitionsPath)
    {
        // Fully qualified: this project also declares a Servyx.Mcp.Tests.Host namespace (Host/*Tests.cs),
        // which shadows the unqualified `Host` class from Microsoft.Extensions.Hosting within this file's
        // sibling-namespace tree.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration["Servyx:Definitions:Path"] = definitionsPath;
        return builder.AddServyxCore(NullLoggerFactory.Instance);
    }
}
