using System.Text.RegularExpressions;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// A static source-text scan that stands in for the architecture test the security audit expected to find
/// guarding <c>tests\Servyx.Remote.Tests</c> — a suite that runs its <c>[SkippableFact]</c>s against a REAL,
/// LIVE, production game server over SSH — and did not find. That suite carries a single RUNTIME assertion
/// (<c>Every_command_this_suite_issues_is_read_only</c>) gated behind <c>SERVYX_REMOTE_*</c> environment
/// variables that are never set in CI or a bare <c>dotnet test</c>, so it never actually executes outside a
/// real production run. This class fills that gap at build time, unconditionally, for every commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here, not in <c>tests\Servyx.Remote.Tests</c> itself.</b> That project is quadruple-gated
/// specifically so nothing in it ever runs outside an operator-initiated production session — it is absent
/// from <c>Servyx.sln</c> AND from <c>.github\workflows\ci.yml</c>'s run list on purpose (see both
/// files' own comments). A safety net that lived inside the suite it is meant to police would inherit both
/// exclusions and never run either. <c>Servyx.Web.Tests</c> is already in the solution, already in CI, and
/// already home to <see cref="MockDataSafetyTests"/> and <see cref="GuideCoverageTests"/> — other tests whose
/// entire job is scanning repository source text for forbidden content — so this class extends an existing,
/// already-wired pattern rather than inventing a new one.
/// </para>
/// <para>
/// <b>What this is, and is not.</b> This is a text scan, not a compiler or a semantic analyzer. It cannot see
/// through a type alias, a reflection-based call, or an indirection deliberately designed to defeat it. What
/// it catches is the realistic failure mode named in the brief: a future edit that adds an ordinary,
/// directly-written mutating call (<c>DockerCli.Start(...)</c>, a new <c>IContainerLifecycle</c> dependency,
/// a raw RCON write) to a test file in this suite. Each check below states its own precision limits inline.
/// </para>
/// </remarks>
public sealed class RemoteTestsWriteSafetyTests
{
    /// <summary>
    /// Identifiers that would give a test in this suite the ABILITY to mutate a live production game server
    /// or its host container, paired with why each one is dangerous here. <c>DockerCli.ExecReadOnly</c> is
    /// deliberately absent: it is the read-only escape hatch, and <see cref="ForbiddenExecCall"/> below is
    /// built specifically so it does not match that name.
    /// </summary>
    private static readonly (string Literal, string Reason)[] ForbiddenIdentifiers =
    [
        ("IContainerLifecycle", "the write-capable container lifecycle abstraction (start/stop/restart/kill a real container)"),
        ("ContainerLifecycleRequest", "the request type that drives IContainerLifecycle's mutations"),
        ("ContainerLifecycleVerb", "the verb enum that selects which container mutation to perform"),
        ("SendRawAsync", "the RCON escape hatch for arbitrary, unaudited raw commands against the live game server"),
    ];

    /// <summary>
    /// <c>DockerCli.Start</c>, <c>.Restart</c>, <c>.Kill</c>, and <c>.Pull</c> are unambiguous substrings, so
    /// a plain literal match is enough. <c>DockerCli.Exec</c> is handled separately by
    /// <see cref="ForbiddenExecCall"/> because <c>DockerCli.ExecReadOnly</c> — the permitted read-only
    /// escape hatch — contains it as a prefix.
    /// </summary>
    private static readonly string[] ForbiddenDockerCliMembers = ["Start", "Restart", "Kill", "Pull"];

    /// <summary>
    /// Matches <c>DockerCli.Exec</c> but NOT <c>DockerCli.ExecReadOnly</c>: <c>\b</c> only asserts a boundary
    /// between a word character and a non-word character, and every character of "ReadOnly" is a word
    /// character, so there is no boundary directly after "Exec" in "ExecReadOnly" for <c>\b</c> to match.
    /// After "Exec" in "DockerCli.Exec(" the next character is "(", which is not a word character, so the
    /// boundary — and the match — is there. No negative lookahead is needed for this distinction.
    /// </summary>
    private static readonly Regex ForbiddenExecCall = new(@"DockerCli\.Exec\b", RegexOptions.Compiled);

    private static readonly Regex ForbiddenMutatingMemberCall =
        new(@"DockerCli\.(Start|Restart|Kill|Pull)\b", RegexOptions.Compiled);

    /// <summary>
    /// Every docker CLI top-level verb this repository's <c>DockerCli</c> does NOT expose as read-only,
    /// matched only when it appears as the FIRST element of a bracketed argv literal — i.e. shaped like
    /// <c>["stop", ...]</c> or <c>["exec", ...]</c> — which is how a caller would construct a
    /// <c>CommandSpec</c> directly, bypassing <c>DockerCli</c> entirely. This intentionally does not flag
    /// the same word used as an ordinary comparison value (e.g. <c>spec.Arguments[0] == "stop"</c>, which
    /// RemoteReadOnlySmokeTests.cs uses legitimately to assert a stop spec was NEVER recorded — the `[` there
    /// is followed by an index, not a quote, so it does not match this pattern). This list is the full
    /// standard docker CLI verb surface minus the always-read-only ones (version, container, ls, inspect,
    /// logs, stats); it is not derived mechanically from the docker CLI's own definition, so a brand-new verb
    /// docker ships in the future would not be caught until added here.
    /// </summary>
    private static readonly Regex ForbiddenDockerVerbArgvLiteral = new(
        "\\[\\s*\"(?:start|stop|restart|kill|pull|exec|rm|rmi|create|update|pause|unpause|rename|commit|push|" +
        "save|load|import|export|prune|network|volume|swarm|service|stack|secret|config|plugin|node|system|" +
        "tag|top|trust|wait|attach|cp|diff|events|history|info|login|logout|port|run|search|build)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Marks the start of a test method whose body is fenced off for the DockerCli.Stop proximity check.</summary>
    private static readonly Regex TestAttributeLine = new(@"^\s*\[(?:Skippable)?Fact\]\s*$", RegexOptions.Compiled);

    /// <summary>Matches an actual DockerCli.Stop(...) call, not a mention inside a comment.</summary>
    private static readonly Regex DockerCliStopCall = new(@"DockerCli\.Stop\(", RegexOptions.Compiled);

    /// <summary>
    /// The number of DockerCli.Stop(...) CODE occurrences (comments excluded) permitted anywhere under
    /// <c>tests\Servyx.Remote.Tests</c>, determined by reading RemoteReadOnlySmokeTests.cs at the time this
    /// test was written: exactly one, inside <c>Stopping_the_container_is_refused_before_any_io</c>. Raising
    /// this number is a deliberate, reviewable act, not something that happens by accident.
    /// </summary>
    private const int MaxAllowedDockerCliStopCalls = 1;

    private static DirectoryInfo RepoRoot => RepoRootLocator.Find();

    private static DirectoryInfo RemoteTestsDirectory =>
        new(Path.Combine(RepoRoot.FullName, "tests", "Servyx.Remote.Tests"));

    [Fact]
    public void Remote_tests_directory_is_found_and_its_source_files_are_non_empty()
    {
        // Guards against the scan vacuously passing because the directory move, got renamed, or the glob
        // below silently matched nothing - a scan of zero files is strictly worse than no scan, because it
        // reports green while checking nothing.
        RemoteTestsDirectory.Exists.Should().BeTrue(
            $"the suite this test protects is expected at '{RemoteTestsDirectory.FullName}'; if it moved, " +
            "this scan (and the CI/.sln/.csproj gate assertions below) must move with it");

        var files = EnumerateSourceFiles(RemoteTestsDirectory).ToList();

        files.Should().NotBeEmpty(
            "tests\\Servyx.Remote.Tests must contain at least one .cs file for this scan to check anything");
        files.Select(f => f.Name).Should().Contain("RemoteReadOnlySmokeTests.cs",
            "the known smoke-test file - its absence would mean the enumeration itself is broken");
    }

    [Fact]
    public void Remote_tests_never_reference_a_forbidden_mutating_identifier()
    {
        var offenses = new List<string>();

        foreach (var file in EnumerateSourceFiles(RemoteTestsDirectory))
        {
            var text = File.ReadAllText(file.FullName);
            var relative = Path.GetRelativePath(RepoRoot.FullName, file.FullName);

            foreach (var (literal, reason) in ForbiddenIdentifiers)
            {
                if (text.Contains(literal, StringComparison.Ordinal))
                {
                    offenses.Add($"{relative} references '{literal}' ({reason})");
                }
            }

            if (ForbiddenExecCall.IsMatch(text))
            {
                offenses.Add($"{relative} calls DockerCli.Exec (a mutating, arbitrary-argv docker exec) - " +
                              "use DockerCli.ExecReadOnly for provably side-effect-free argv, if this needs exec at all");
            }

            if (ForbiddenMutatingMemberCall.IsMatch(text))
            {
                var members = ForbiddenMutatingMemberCall.Matches(text)
                    .Select(m => m.Groups[1].Value)
                    .Distinct()
                    .OrderBy(m => m, StringComparer.Ordinal);
                offenses.Add($"{relative} calls DockerCli.{string.Join("/DockerCli.", members)}");
            }
        }

        offenses.Should().BeEmpty(
            because: "tests\\Servyx.Remote.Tests runs its [SkippableFact]s against a REAL production game " +
                     $"server over SSH; any of these references would let a test mutate it. Found: {string.Join("; ", offenses)}");
    }

    [Fact]
    public void Remote_tests_never_construct_a_forbidden_docker_verb_argv_literal()
    {
        var offenses = new List<string>();

        foreach (var file in EnumerateSourceFiles(RemoteTestsDirectory))
        {
            var text = File.ReadAllText(file.FullName);
            var relative = Path.GetRelativePath(RepoRoot.FullName, file.FullName);

            foreach (Match match in ForbiddenDockerVerbArgvLiteral.Matches(text))
            {
                offenses.Add($"{relative}: argv literal starting with {match.Value.Trim()}...]");
            }
        }

        offenses.Should().BeEmpty(
            because: "a docker verb outside {version, container ls, container inspect, logs, stats} " +
                     "constructed as a raw argv literal would bypass DockerCli entirely and could mutate the " +
                     $"live production game server this suite is connected to. Found: {string.Join("; ", offenses)}");
    }

    /// <summary>
    /// The one deliberate exception: <c>DockerCli.Stop</c> is allowed to appear, but only up to
    /// <see cref="MaxAllowedDockerCliStopCalls"/> times in actual code (comments/doc-comments don't count -
    /// they never execute), and every such call must sit inside a test method that also asserts
    /// <c>WritesDisabledException</c>, proving the call is there to demonstrate a refusal, not to perform one.
    /// </summary>
    /// <remarks>
    /// "Inside the same test method" is approximated by proximity to <c>[Fact]</c>/<c>[SkippableFact]</c>
    /// attribute lines: the call's enclosing method is everything from the nearest preceding attribute line
    /// to the nearest following one (or end of file). This matches this codebase's own convention, visible in
    /// RemoteReadOnlySmokeTests.cs, of one attribute immediately preceding one test method. It would not
    /// correctly scope a DockerCli.Stop call sitting in a private helper method called BY a test rather than
    /// written directly in one - that is a known, accepted limitation of a line-proximity/text-based check
    /// rather than a real parse, stated here rather than left implicit.
    /// </remarks>
    [Fact]
    public void DockerCli_Stop_appears_only_inside_the_one_deliberate_refusal_test()
    {
        var totalCalls = 0;
        var unconfined = new List<string>();

        foreach (var file in EnumerateSourceFiles(RemoteTestsDirectory))
        {
            var lines = File.ReadAllLines(file.FullName);
            var relative = Path.GetRelativePath(RepoRoot.FullName, file.FullName);

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue; // doc comments and line comments never execute; not a code occurrence
                }

                if (!DockerCliStopCall.IsMatch(lines[i]))
                {
                    continue;
                }

                totalCalls++;

                var methodStart = -1;
                for (var b = i; b >= 0; b--)
                {
                    if (!TestAttributeLine.IsMatch(lines[b]))
                    {
                        continue;
                    }

                    methodStart = b;
                    break;
                }

                var methodEnd = lines.Length;
                for (var f = i + 1; f < lines.Length; f++)
                {
                    if (!TestAttributeLine.IsMatch(lines[f]))
                    {
                        continue;
                    }

                    methodEnd = f;
                    break;
                }

                if (methodStart < 0)
                {
                    unconfined.Add($"{relative}:{i + 1} - DockerCli.Stop(...) is not inside any " +
                                    "[Fact]/[SkippableFact]-attributed test method");
                    continue;
                }

                var methodText = string.Join('\n', lines[methodStart..methodEnd]);
                if (!methodText.Contains("WritesDisabledException", StringComparison.Ordinal))
                {
                    unconfined.Add($"{relative}:{i + 1} - DockerCli.Stop(...) call's enclosing test does not " +
                                    "assert WritesDisabledException, so it is not provably just demonstrating a refusal");
                }
            }
        }

        unconfined.Should().BeEmpty(
            because: "a DockerCli.Stop(...) call in this suite that is NOT proven-refused by an accompanying " +
                     "WritesDisabledException assertion would stop a live production game server people are " +
                     $"playing on. Offending occurrences: {string.Join("; ", unconfined)}");

        totalCalls.Should().BeLessThanOrEqualTo(MaxAllowedDockerCliStopCalls,
            $"only the one deliberate refusal test may construct a DockerCli.Stop(...) spec; found {totalCalls} " +
            "code occurrences, more than the one this test was written to allow - each additional one is a " +
            "new place a mutating call could run against a live production game server people play on, and " +
            "must be reviewed and this limit raised deliberately, not silently");
    }

    [Fact]
    public void Servyx_Remote_Tests_is_absent_from_the_solution()
    {
        var slnPath = Path.Combine(RepoRoot.FullName, "Servyx.sln");
        File.Exists(slnPath).Should().BeTrue($"Servyx.sln should exist at '{slnPath}'");

        var text = File.ReadAllText(slnPath);
        text.Should().NotContain("Servyx.Remote.Tests",
            "Servyx.Remote.Tests must stay out of Servyx.sln, so `dotnet build Servyx.sln` and IDE " +
            "\"run all tests\" never reach a suite that talks to a real production game server");
    }

    [Fact]
    public void Servyx_Remote_Tests_is_absent_from_the_ci_workflow_project_array()
    {
        var ciPath = Path.Combine(RepoRoot.FullName, ".github", "workflows", "ci.yml");
        File.Exists(ciPath).Should().BeTrue($"ci.yml should exist at '{ciPath}'");

        // The workflow's header comment names tests/Servyx.Remote.Tests explicitly, to explain why it is
        // excluded - that mention is expected and fine. Every comment line in this YAML file starts with '#'
        // (after leading whitespace), so stripping those lines before checking isolates the executable
        // project array from the explanatory prose around it.
        var executableLines = File.ReadAllLines(ciPath)
            .Where(line => !line.TrimStart().StartsWith('#'));
        var executableText = string.Join('\n', executableLines);

        executableText.Should().NotContain("Servyx.Remote.Tests",
            "Servyx.Remote.Tests must never appear in ci.yml's executable project array - CI must never " +
            "point at somebody's production host, per the workflow's own header comment");
    }

    [Fact]
    public void Servyx_Remote_Tests_csproj_still_filters_out_integration_tests_by_default()
    {
        var csprojPath = Path.Combine(RemoteTestsDirectory.FullName, "Servyx.Remote.Tests.csproj");
        File.Exists(csprojPath).Should().BeTrue($"the project file should exist at '{csprojPath}'");

        var text = File.ReadAllText(csprojPath);
        text.Should().Contain("Category!=Integration",
            "the .csproj's default VSTestTestCaseFilter must keep excluding Category=Integration tests, so " +
            "a bare `dotnet test` on this project runs zero tests against the live production game server");
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(DirectoryInfo dir)
    {
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

        return dir.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(objSegment, StringComparison.OrdinalIgnoreCase));
    }
}
