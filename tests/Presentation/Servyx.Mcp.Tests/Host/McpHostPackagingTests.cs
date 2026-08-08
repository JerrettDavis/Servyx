using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Host;

/// <summary>
/// A <c>None</c> item's <c>CopyToOutputDirectory</c> does NOT flow across a <c>ProjectReference</c> — see
/// <c>Servyx.Mcp.Stdio.csproj</c>'s own remarks on its duplicated glob. Without that glob a host ships with
/// zero definitions, adopts nothing, and honestly reports "no definitions loaded" — which looks exactly like
/// correct behaviour rather than the packaging bug it actually is. This test makes that failure loud instead
/// of silent by asserting every executable host that calls <c>AddServyxCore</c> also ships its own copy of
/// the glob, discovered rather than hand-listed so a future host cannot join the fleet silently unpackaged.
/// </summary>
public sealed class McpHostPackagingTests
{
    [Fact]
    public void Every_executable_host_that_calls_AddServyxCore_copies_the_bundled_definitions_to_its_output()
    {
        var repoRoot = RepoRootLocator.Find();
        var srcDir = new DirectoryInfo(Path.Combine(repoRoot.FullName, "src"));

        var hostProgramFiles = srcDir.EnumerateFiles("Program.cs", SearchOption.AllDirectories)
            .Where(f => !IsUnderBinOrObj(f))
            .Where(f => File.ReadAllText(f.FullName).Contains("AddServyxCore(", StringComparison.Ordinal))
            .ToList();

        // Anti-vacuity: if discovery found nothing, the glob search below would pass having asserted nothing.
        hostProgramFiles.Should().NotBeEmpty(
            "at least the web host and the MCP stdio host must be discoverable by this scan — an empty " +
            "result means the discovery logic itself is broken, not that no host calls AddServyxCore");

        var offenders = new List<string>();

        foreach (var programFile in hostProgramFiles)
        {
            var projectDir = programFile.Directory!;
            var csproj = projectDir.EnumerateFiles("*.csproj").SingleOrDefault();

            if (csproj is null)
            {
                offenders.Add($"{Path.GetRelativePath(repoRoot.FullName, projectDir.FullName)}: no single .csproj found beside Program.cs");
                continue;
            }

            var text = File.ReadAllText(csproj.FullName);
            var hasDefinitionsGlob = text.Contains("definitions\\*.yaml", StringComparison.OrdinalIgnoreCase)
                || text.Contains("definitions/*.yaml", StringComparison.OrdinalIgnoreCase);
            var hasCopyToOutput = text.Contains("CopyToOutputDirectory", StringComparison.Ordinal);

            if (!hasDefinitionsGlob || !hasCopyToOutput)
            {
                offenders.Add(Path.GetRelativePath(repoRoot.FullName, csproj.FullName));
            }
        }

        offenders.Should().BeEmpty(
            "every host project whose Program.cs calls AddServyxCore must itself declare a definitions/*.yaml " +
            "None-item glob with CopyToOutputDirectory set — a None item does not flow across a " +
            $"ProjectReference — missing/incomplete in: {string.Join(", ", offenders)}");
    }

    private static bool IsUnderBinOrObj(FileInfo file)
    {
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        return file.FullName.Contains(binSegment, StringComparison.OrdinalIgnoreCase)
            || file.FullName.Contains(objSegment, StringComparison.OrdinalIgnoreCase);
    }
}
