using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Composition;

/// <summary>
/// Source-scans both <c>Program.cs</c> files (<c>Servyx.Web</c> and <c>Servyx.Mcp.Stdio</c>) so a second
/// composition root cannot grow in either one. <c>ServyxCoreCompositionExtensions.AddServyxCoreCore</c> is
/// the ONE place every safety gate (provisioning, write mode, RCON wiring, backup wiring) is built; a host
/// that re-read configuration into a second <c>ProvisioningGate</c>, or hand-built a second
/// <c>WriteGuardedTransport</c>, could silently drift from what the shared root already decided — the exact
/// hazard <c>AddServyxCore</c> was extracted to prevent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The forbidden-identifier rule this file lands on.</strong> Both files are forbidden from calling
/// <c>.FromConfiguration(</c>/<c>.ReadGrants(</c> on any of the shared gate/wiring types
/// <c>ServyxCoreCompositionExtensions</c> itself calls (<see cref="ForbiddenFactoryCalls"/>), and from
/// directly constructing (<c>new </c>) any of the guarded/composed types it itself constructs
/// (<see cref="ForbiddenConstructions"/>). That is a REGISTRATION-pattern rule, not a "no
/// <c>Servyx.Composition</c> types at all" rule — reading what the one composition root already built back
/// out of its result (<c>core.Provisioning</c>, <c>core.ImportSecretsAsync</c>, registering the
/// already-built <c>core</c> object itself into DI, resolving <c>IEnumerable&lt;WriteModeGrant&gt;</c> that
/// <c>AddServyxCoreCore</c> already populated, calling <c>StartupSafetyWarnings.LogDangerousCombinations</c>
/// over that result) is exactly the sanctioned use both files already make and must keep making.
/// </para>
/// <para>
/// <strong>One deliberate carve-out: <c>AuthenticationGate.FromConfiguration</c>.</strong> Present in
/// <c>Servyx.Web</c>'s <c>Program.cs</c> today, and NOT forbidden here, because <c>AuthenticationGate</c> is
/// never composed by <c>ServyxCoreCompositionExtensions</c> at all — it is a host-specific, web-only gate
/// (an MCP host authenticates its transport, not through an operator password), not a shared safety surface
/// this file exists to protect from duplication. Forbidding it would not close a gap; it would just break a
/// legitimate, single-host concern.
/// </para>
/// </remarks>
public sealed class CompositionRootSingleSourceTests
{
    /// <summary>Factory-method call sites that would mean a host re-derived a shared gate from configuration a second time.</summary>
    private static readonly IReadOnlyList<string> ForbiddenFactoryCalls =
    [
        "ProvisioningGate.FromConfiguration(",
        "WritableServers.FromConfiguration(",
        "ServerWriteModes.ReadGrants(",
        // Phase 2's grant-related additions. ServerWriteModes.ReadGrants no longer exists — the entry above is
        // kept so a future revival of that name cannot quietly reappear in a host — and these replace it:
        // FindIgnoredLegacyKeys is the legacy-key detection AddServyxCoreCore alone is responsible for warning
        // about, and WritableServers.Live is the live grant view exactly one place may construct.
        "ServerWriteModes.FindIgnoredLegacyKeys(",
        "WritableServers.Live(",
        "SshDockerWriteModes.ReadGrants(",
        "SshDockerWiringOptions.FromConfiguration(",
        "RconWiringOptions.FromConfiguration(",
        "BackupWiringOptions.FromConfiguration(",
        "SshBackupWiringOptions.FromConfiguration(",
        "ProvisionerWiringOptions.FromConfiguration(",
        "BackupScheduleOptions.FromConfiguration(",
    ];

    /// <summary>Direct construction of a guarded/composed type that would mean a host built its own copy rather than reading the shared one.</summary>
    private static readonly IReadOnlyList<string> ForbiddenConstructions =
    [
        "new WriteGuardedTransport(",
        "new WriteGuardedRconSession(",
        "new WriteGuardedExecutionTarget(",
        "new ServyxRconChannels(",
        "new ServyxBackupContextSource(",
        "new ServyxSshBackupContextSource(",
        "new ProvisioningDashboardService(",
        // Phase 2. The write grant now comes from the database, so a host that built its own cache, resolver or
        // grant service would be a second source of truth for the single most safety-critical decision in the
        // product — strictly worse than the configuration duplication this test was written to prevent.
        "new WriteGrantCache(",
        "new DbBackedWriteModeResolver(",
        "new WriteGrantService(",
    ];

    public static TheoryData<string> BothProgramFiles()
    {
        var data = new TheoryData<string>();
        data.Add(Path.Combine("Presentation", "Servyx.Web", "Program.cs"));
        data.Add(Path.Combine("Hosting", "Servyx.Mcp.Stdio", "Program.cs"));
        return data;
    }

    [Theory]
    [MemberData(nameof(BothProgramFiles))]
    public void Program_cs_never_re_derives_a_shared_gate_from_configuration(string relativePath)
    {
        var text = ReadProgram(relativePath);

        var offenders = ForbiddenFactoryCalls.Where(pattern => text.Contains(pattern, StringComparison.Ordinal)).ToList();

        offenders.Should().BeEmpty(
            $"{relativePath} must read every shared gate off the ServyxCoreComposition AddServyxCore already " +
            $"built, never re-derive one from configuration itself — found: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(BothProgramFiles))]
    public void Program_cs_never_directly_constructs_a_guarded_or_composed_type(string relativePath)
    {
        var text = ReadProgram(relativePath);

        var offenders = ForbiddenConstructions.Where(pattern => text.Contains(pattern, StringComparison.Ordinal)).ToList();

        offenders.Should().BeEmpty(
            $"{relativePath} must never hand-build a guarded transport, RCON session, or backup context " +
            $"source of its own — that is ServyxCoreCompositionExtensions' job alone — found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Both_program_files_still_call_AddServyxCore()
    {
        // Anti-vacuity: if a rename ever moved this call out of Program.cs, the assertions above would pass
        // having compared against a file that no longer represents what this test believes it is checking.
        foreach (var relative in new[]
                 {
                     Path.Combine("Presentation", "Servyx.Web", "Program.cs"),
                     Path.Combine("Hosting", "Servyx.Mcp.Stdio", "Program.cs"),
                 })
        {
            ReadProgram(relative).Should().Contain("AddServyxCore(", $"{relative} must still call the shared composition root");
        }
    }

    private static string ReadProgram(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRootLocator.Find().FullName, "src", relativePath));
}
