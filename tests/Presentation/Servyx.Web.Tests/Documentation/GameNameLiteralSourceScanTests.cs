using System.Text.RegularExpressions;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Converts <c>docs/roadmap.md</c>'s milestone M6 acceptance criterion — "no C# changes outside format
/// adapters and the RCON dialect; if more is required, the abstraction has failed" — from a judgement call
/// into a build failure. Scans every <c>.cs</c>/<c>.razor</c> file under <c>src/</c> for a literal naming a
/// specific game (<c>palworld</c>, <c>thijsvanloef</c>, <c>minecraft</c>, <c>itzg</c>, <c>factorio</c>,
/// <c>valheim</c>, <c>ark</c>), outside a short, individually-justified exclusion list.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What counts as a hit.</strong> Only text that could actually influence runtime behavior or ship
/// to a user — a string literal, an identifier, a UI-visible text node. A line that is entirely a <c>///</c>
/// XML doc comment, a <c>//</c> line comment, a block-comment continuation line (starts with <c>*</c>), or
/// an XML/Razor <c>&lt;!--</c> comment is excluded outright: this codebase's own convention (see
/// <c>DeclaredConfigSurface.cs</c>, every <c>GameDefinition</c> model type, and this task's own additions to
/// them) is to cite <c>definitions/palworld-docker.yaml</c> and <c>definitions/minecraft-itzg.yaml</c> by
/// name throughout doc-comment prose as the two worked examples backing the schema — that is exactly the
/// "doc-comment prose" this task's brief says may be excluded, since including it produced ~100 hits of pure
/// narration and zero signal. <c>.csproj</c> files are not scanned at all: they are MSBuild manifests, not
/// application source with adoption/binding logic (the one genuine leak found there — see below — was an
/// item list naming exactly one definition file to copy to the output directory, fixed as a glob rather than
/// added to this test's exclusion list).
/// </para>
/// <para>
/// <strong>The exclusion list, and why each entry earns it</strong> — every other hit in the codebase was
/// fixed rather than excluded (see the M6 second-game report for the two genuine leaks this scan caught and
/// this task fixed: <c>ServyxBackupContextSource</c>'s <c>foreignAdapterId</c> fallback firing for any
/// adopt-less definition rather than only "no definition loaded", and <c>Servyx.Web.csproj</c> shipping only
/// <c>palworld-docker.yaml</c> to the output directory):
/// </para>
/// <list type="bullet">
/// <item>
/// <c>Backups/PalworldCronBackupAdopter.cs</c> — the designated, definition-referenced backup adapter for
/// <c>thijsvanloef/palworld-server-docker</c>'s own cron rotation. This is exactly the "format adapters and
/// the RCON dialect" class of legitimately game-specific code the roadmap's own M6 criterion carves out; a
/// future game with its own foreign-backup convention would add a sibling adapter here, not touch this one.
/// </item>
/// <item>
/// <c>Backups/DockerBackupServiceCollectionExtensions.cs</c> — the one line registering that same designated
/// adapter's type in DI. A second adapter is registered beside it, not instead of it.
/// </item>
/// <item>
/// <c>Hosting/Servyx.Composition/ServyxBackupContextSource.cs</c> (moved out of
/// <c>Presentation/Servyx.Web/Services/</c> when the shared composition root was extracted into its own
/// project) — one line: the legacy fallback to <c>PalworldCronBackupAdopter.Id</c> used ONLY when no game
/// definition has loaded at all (see that file's own remarks, tightened by this task's fix). Referencing the
/// one designated adapter's id as the no-definition-loaded default is the documented, intentional behavior
/// <c>docs/schema.md</c> describes for this exact case, not an ambient assumption about which game is
/// running.
/// </item>
/// <item>
/// <c>Services/MockDashboardDataService.cs</c> — the offline demo/screenshot data path
/// (<c>Servyx:DataSource=Mock</c>). Isolated from every real adoption/binding/settings code path this task
/// exists to prove is generic; illustrated with one concrete game the same way a screenshot or a tutorial
/// would be, not a production default. <see cref="MockDataSafetyTests"/> already separately guards this
/// exact file for real-world credential/host leaks — this exclusion does not weaken that.
/// </item>
/// <item>
/// <c>Services/ProvisionerFormSchema.cs</c> — <c>ProvisionerFormCatalog.FallbackContainerImage</c>, the last
/// -resort default <c>/deploy</c>'s Docker field falls back to only when nothing else supplies one: no game
/// definition is loaded at all, or the one selected declares no docker-kind deployment. Every other case —
/// one game loaded, or a specific one chosen from two or more — overrides it with that game's own image
/// (see <c>DeployPage.OnInitializedAsync</c>/<c>OnGameChanged</c>, which now select over every loaded
/// definition rather than assuming exactly one). Kept as a literal, not a lookup, because a last-resort
/// default is precisely the one value that must still mean something when there is no definition to look
/// anything up in.
/// </item>
/// </list>
/// <para>
/// <strong>Not exempt: <c>Components/Pages/Deploy/DeployPage.razor</c>.</strong> Used to carry a hardcoded
/// <c>"palworld"</c> default for exactly the case a multi-game deploy page needed to stop assuming — see the
/// game-selection work this exemption's removal accompanies. The file now selects over every loaded
/// definition instead of defaulting to one by name, and contains no remaining game-name literal outside its
/// own doc-comment prose (which this scan already excludes on its own), so it earns no place on this list
/// any more.
/// </para>
/// </remarks>
public sealed class GameNameLiteralSourceScanTests
{
    private static readonly Regex GameNamePattern = new(
        @"palworld|thijsvanloef|minecraft|itzg|factorio|valheim|\bark\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Files legitimately exempt from this scan, relative to <c>src/</c> with forward slashes — see the
    /// class remarks for why each one earns its place here. Kept intentionally short: this list is the
    /// literal evidence for this task's "leak or legitimate" call on every hit, and padding it to force a
    /// pass would make the test worthless.
    /// </summary>
    private static readonly string[] ExemptFiles =
    [
        "Infrastructure/Servyx.Infrastructure.Docker/Backups/PalworldCronBackupAdopter.cs",
        "Infrastructure/Servyx.Infrastructure.Docker/Backups/DockerBackupServiceCollectionExtensions.cs",
        "Hosting/Servyx.Composition/ServyxBackupContextSource.cs",
        "Presentation/Servyx.Web/Services/MockDashboardDataService.cs",
        "Presentation/Servyx.Web/Services/ProvisionerFormSchema.cs",
    ];

    [Fact]
    public void No_hardcoded_game_name_literal_exists_outside_the_exempt_files()
    {
        var repoRoot = RepoRootLocator.Find();
        var srcDir = new DirectoryInfo(Path.Combine(repoRoot.FullName, "src"));
        var exemptSet = ExemptFiles
            .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(srcDir))
        {
            var relative = Path.GetRelativePath(srcDir.FullName, file.FullName);
            if (exemptSet.Contains(relative))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file.FullName))
            {
                lineNumber++;

                var trimmed = line.TrimStart();
                var isProse = trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || trimmed.StartsWith("<!--", StringComparison.Ordinal);
                if (isProse)
                {
                    continue;
                }

                if (GameNamePattern.IsMatch(line))
                {
                    offenders.Add($"{Path.Combine("src", relative)}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        var detail = offenders.Count == 0 ? string.Empty : "\n" + string.Join("\n", offenders);
        offenders.Should().BeEmpty(
            because: "every hardcoded game-name literal under src/** must be either fixed or added to "
                + $"ExemptFiles with a stated reason (see this class's remarks); found:{detail}");
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(DirectoryInfo dir)
    {
        string[] extensions = [".cs", ".razor"];
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

        return dir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(objSegment, StringComparison.OrdinalIgnoreCase));
    }
}
