using Servyx.Domain.Definitions.Model;
using Servyx.Domain.Rcon;
using Servyx.Infrastructure.Rcon.Tests.Support;

namespace Servyx.Infrastructure.Rcon.Tests.PlayerParsing;

/// <summary>
/// The call-site layer of the player-list isolation boundary, and the only one that is a guarantee rather
/// than a convention.
/// </summary>
/// <remarks>
/// <para>
/// Servyx models several control-channel reply formats from unverified community reports of what a given
/// server prints. Two of the three defences against a wrong guess are structural — patterns are compiled
/// during definition validation, and <see cref="RconPlayerListParser.Parse(string?, PlayerParserSpec?)"/> is
/// total — but the third, "a mis-parsed player list cannot reach anything that starts, stops, archives, or
/// declares a server ready", is only true as long as nobody wires it there. This test is what makes that
/// true: it scans the source of every lifecycle, readiness, and backup-execution file in the solution and
/// fails the build if <see cref="PlayerListSnapshot"/>, <see cref="PlayerListFidelity"/>,
/// <see cref="RconPlayerListParser"/>, or <see cref="PlayerListPlan"/> — the gateway to that surface, since
/// resolving one is what a caller would need before it could invoke the quarantined parser at all — appears
/// in one.
/// </para>
/// <para>
/// Readiness comes from log-regex, control-probe, port, and health signals — never from a player count. A
/// server with zero players online is not "not ready", and a player list that failed to parse is not "not
/// ready" either; conflating the two is precisely the failure this boundary exists to make impossible.
/// </para>
/// <para>
/// Lines that are entirely comment prose (<c>//</c>, <c>///</c>, a block-comment continuation, or an
/// XML/Razor comment) are excluded, following the same convention as the solution's other source-scan
/// guard: a lifecycle file explaining in prose WHY it does not consult a player list is evidence for this
/// boundary, not a violation of it.
/// </para>
/// </remarks>
public class PlayerListIsolationArchitectureTests
{
    /// <summary>
    /// The types no lifecycle, readiness, or backup-execution source file may mention:
    /// <see cref="PlayerListSnapshot"/>, <see cref="PlayerListFidelity"/>, <see cref="RconPlayerListParser"/>,
    /// and <see cref="PlayerListPlan"/> — the gateway to that surface, since resolving a plan is the step
    /// that precedes invoking the quarantined parser.
    /// </summary>
    private static readonly string[] QuarantinedTypeNames =
    [
        nameof(PlayerListSnapshot),
        nameof(PlayerListFidelity),
        nameof(RconPlayerListParser),
        nameof(PlayerListPlan),
    ];

    /// <summary>
    /// Directory names that mark a source file as part of the quarantined-from surface, matched as a whole
    /// path segment anywhere under <c>src/</c> so a new provider's <c>Backups/</c> folder is covered the day
    /// it is added rather than the day someone remembers to list it.
    /// </summary>
    private static readonly string[] QuarantinedDirectories = ["Lifecycle", "Backups"];

    [Fact]
    public void No_lifecycle_readiness_or_backup_source_file_references_the_player_list_snapshot_or_its_parser()
    {
        var offenders = new List<string>();

        foreach (var (relative, file) in QuarantinedSourceFiles())
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file.FullName))
            {
                lineNumber++;
                if (IsProse(line))
                {
                    continue;
                }

                foreach (var type in QuarantinedTypeNames.Where(t => line.Contains(t, StringComparison.Ordinal)))
                {
                    offenders.Add($"src/{relative}:{lineNumber} mentions {type}: {line.Trim()}");
                }
            }
        }

        var detail = offenders.Count == 0 ? string.Empty : "\n" + string.Join("\n", offenders);
        offenders.Should().BeEmpty(
            because: "a player-list reply is parsed from formats Servyx has not verified, so it must be "
                + "unreachable from anything that starts, stops, archives, or declares a server ready — "
                + "readiness comes from log, probe, port, and health signals, never from a player count; "
                + $"found:{detail}");
    }

    /// <summary>
    /// The scan above is worthless if it silently matches nothing, so this pins that it really is looking at
    /// the readiness detectors and the backup execution path.
    /// </summary>
    [Fact]
    public void The_quarantined_surface_really_does_cover_readiness_and_backup_execution()
    {
        var scanned = QuarantinedSourceFiles().Select(f => f.File.Name).ToList();

        scanned.Should().Contain("LogRegexReadiness.cs");
        scanned.Should().Contain("ControlProbeReadiness.cs");
        scanned.Should().Contain("CompositeReadinessDetector.cs");
        scanned.Should().Contain("IReadinessDetector.cs");
        scanned.Should().Contain("LifecycleDefinition.cs");
        scanned.Should().Contain("IBackupProvider.cs");
        scanned.Should().HaveCountGreaterThan(20, "the lifecycle and backup surfaces are not two files");
    }

    /// <summary>
    /// The scan is also worthless if its needles never match anything, so this proves the same matching
    /// rules DO find both quarantined names in the one file that legitimately holds them.
    /// </summary>
    [Fact]
    public void The_scan_would_catch_a_reference_because_it_finds_both_names_where_they_belong()
    {
        var parserSource = Path.Combine(
            RepoRootLocator.Find().FullName,
            "src", "Infrastructure", "Servyx.Infrastructure.Rcon", "RconPlayerListParser.cs");

        var codeLines = File.ReadLines(parserSource).Where(line => !IsProse(line)).ToList();

        codeLines.Should().Contain(line => line.Contains(nameof(RconPlayerListParser), StringComparison.Ordinal));
        codeLines.Should().Contain(line => line.Contains(nameof(PlayerListSnapshot), StringComparison.Ordinal));
    }

    private static bool IsProse(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('*')
            || trimmed.StartsWith("<!--", StringComparison.Ordinal);
    }

    private static IReadOnlyList<(string Relative, FileInfo File)> QuarantinedSourceFiles()
    {
        var srcDir = new DirectoryInfo(Path.Combine(RepoRootLocator.Find().FullName, "src"));
        string[] extensions = [".cs", ".razor"];
        var separator = Path.DirectorySeparatorChar;
        var binSegment = $"{separator}bin{separator}";
        var objSegment = $"{separator}obj{separator}";

        return srcDir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(objSegment, StringComparison.OrdinalIgnoreCase))
            .Select(f => (Relative: Path.GetRelativePath(srcDir.FullName, f.FullName), File: f))
            .Where(entry => entry.Relative
                .Split(separator, StringSplitOptions.RemoveEmptyEntries)
                .SkipLast(1)
                .Any(segment => QuarantinedDirectories.Contains(segment, StringComparer.Ordinal)))
            .ToList();
    }
}
