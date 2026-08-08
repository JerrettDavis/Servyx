using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Documentation;

/// <summary>
/// Scans the SOURCE OF TRUTH for the demo/screenshot data path — <see cref="MockDashboardDataService"/>'s
/// static fields and everything its public methods return, plus the raw BDD feature files and user-guide
/// docs that drive and describe it — for literals that identify a real machine, host, or credential.
/// </summary>
/// <remarks>
/// Rationale: every pixel in a committed screenshot ultimately comes from data this mock service handed to
/// the DOM. If a forbidden string cannot exist anywhere in that data, it cannot reach the DOM, and therefore
/// cannot reach a pixel. Scanning the source of truth this way is strictly stronger than scanning rendered
/// pixels/DOM after the fact (it also catches leaks in data nobody happens to screenshot this week), and it
/// is pure reflection + text I/O, so it runs in CI for free — no browser, no screenshot render required.
/// </remarks>
public sealed class MockDataSafetyTests
{
    /// <summary>
    /// Literals that must never appear in the mock/demo data path. These are real coordinates that leaked
    /// into a committed PNG once before this suite existed: a real production host IP, a real admin
    /// username, a real SSH private key filename, a real credential/token, a real machine's local filesystem
    /// path, and the operator's own project codename.
    /// </summary>
    private static readonly (string Literal, string Description)[] ForbiddenLiterals =
    [
        ("185.126.158.41", "a real production host IP address"),
        ("paladmin", "a real production admin username"),
        ("palworld_cloudnium_ed25519", "a real production SSH private key filename"),
        ("zEd1PiHaMrf67NYzCGVuYtYzkzAcK0pnW8", "a real production credential/token"),
        (@"D:\Games", "a real machine's local filesystem path"),
        ("cloudnium", "the operator's own host/project codename"),
    ];

    private static readonly Regex ServyxRemoteEnvVarPattern = new("SERVYX_REMOTE_[A-Z_]+", RegexOptions.Compiled);

    private static DirectoryInfo RepoRoot => RepoRootLocator.Find();

    [Fact]
    public async Task Mock_dashboard_data_contains_no_forbidden_real_world_literal()
    {
        var dump = await DumpMockDashboardDataAsync();

        dump.Should().NotBeEmpty("reflecting MockDashboardDataService's fields and method results should " +
                                  "yield real content — an empty dump means the reflection is broken, not " +
                                  "that the mock is empty");

        AssertNoForbiddenLiterals(dump, "MockDashboardDataService's static fields and public method results");
    }

    [Fact]
    public void Feature_files_contain_no_forbidden_real_world_literal()
    {
        var featuresDir = new DirectoryInfo(
            Path.Combine(RepoRoot.FullName, "tests", "Servyx.E2E.Bdd.Tests", "Features"));
        var files = featuresDir.GetFiles("*.feature");

        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            AssertNoForbiddenLiterals(File.ReadAllText(file.FullName), file.Name);
        }
    }

    [Fact]
    public void User_guide_docs_contain_no_forbidden_real_world_literal()
    {
        // Scoped to docs\user-guide only, NOT all of docs\. The operator's own runbook docs (e.g.
        // docs\remote-palworld-runbook.md) legitimately name the gitignored scripts\cloudnium.local.ps1 /
        // committed scripts\cloudnium.example.ps1 tooling — those files live outside docs\user-guide, so a
        // docs\user-guide-only scope excludes them without an allow-list. The forbidden set (including
        // "cloudnium") still applies in full within docs\user-guide, because guide pages are the ones
        // illustrated by screenshots and read by end users, and must never leak the operator's own
        // host/project codename.
        var guideDir = new DirectoryInfo(Path.Combine(RepoRoot.FullName, "docs", "user-guide"));
        var files = guideDir.GetFiles("*.md");

        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            AssertNoForbiddenLiterals(File.ReadAllText(file.FullName), file.Name);
        }
    }

    [Fact]
    public void No_SERVYX_REMOTE_env_var_literal_is_read_by_product_code()
    {
        // SERVYX_REMOTE_* variables belong exclusively to tests/Servyx.Remote.Tests, which carries every
        // production coordinate (endpoint, key path, container name, fingerprint) for the live production
        // test suite. Product code under src\ must never read them - doing so would mean a production
        // coordinate could reach a path that ships.
        var srcDir = new DirectoryInfo(Path.Combine(RepoRoot.FullName, "src"));
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(srcDir))
        {
            var text = File.ReadAllText(file.FullName);
            if (ServyxRemoteEnvVarPattern.IsMatch(text))
            {
                offenders.Add(Path.GetRelativePath(RepoRoot.FullName, file.FullName));
            }
        }

        var detail = string.Join("; ", offenders);
        offenders.Should().BeEmpty(
            because: $"product code under src\\ must never reference a SERVYX_REMOTE_* literal, but it was found in: {detail}");
    }

    private static void AssertNoForbiddenLiterals(string text, string sourceLabel)
    {
        var hits = ForbiddenLiterals
            .Where(f => text.Contains(f.Literal, StringComparison.OrdinalIgnoreCase))
            .Select(f => $"'{f.Literal}' ({f.Description})")
            .ToList();

        var detail = string.Join("; ", hits);
        hits.Should().BeEmpty(because: $"{sourceLabel} must not contain: {detail}");
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(DirectoryInfo dir)
    {
        string[] extensions = [".cs", ".razor", ".json", ".cshtml", ".config", ".props", ".targets"];
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";

        return dir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(binSegment, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.FullName.Contains(objSegment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reflects over <see cref="MockDashboardDataService"/>'s own static fields/constants (public and
    /// non-public — catches anything not necessarily surfaced through the public interface), then calls
    /// every public method on a default instance (using the real server ids the service itself returns) and
    /// serializes everything to text.
    /// </summary>
    private static async Task<string> DumpMockDashboardDataAsync()
    {
        var sb = new StringBuilder();
        var type = typeof(MockDashboardDataService);

        foreach (var field in type.GetFields(
                     BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            sb.Append("// field ").AppendLine(field.Name);
            sb.AppendLine(SerializeSafely(field.GetValue(null)));
        }

        var service = new MockDashboardDataService();

        sb.AppendLine(SerializeSafely(await service.GetDockerConnectionStatusAsync()));
        sb.AppendLine(SerializeSafely(await service.GetDockerConnectionInfoAsync()));
        sb.AppendLine(SerializeSafely(await service.GetDashboardSummaryAsync()));

        var servers = await service.GetServersAsync();
        sb.AppendLine(SerializeSafely(servers));
        sb.AppendLine(SerializeSafely(await service.GetServersWithStatusAsync()));
        sb.AppendLine(SerializeSafely(await service.GetAllBackupsAsync()));
        sb.AppendLine(SerializeSafely(await service.GetAllBackupsWithStatusAsync()));
        sb.AppendLine(SerializeSafely(await service.GetGamesAsync()));

        foreach (var server in servers)
        {
            sb.AppendLine(SerializeSafely(await service.GetServerDetailAsync(server.Id)));
            sb.AppendLine(SerializeSafely(await service.GetServerSettingsAsync(server.Id)));
            sb.AppendLine(SerializeSafely(await service.GetServerLogsAsync(server.Id)));
            sb.AppendLine(SerializeSafely(await service.GetServerSavesAsync(server.Id)));
            sb.AppendLine(SerializeSafely(await service.GetServerBackupsAsync(server.Id)));
        }

        return sb.ToString();
    }

    private static string SerializeSafely(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            // JSON escapes a backslash as a doubled backslash, so a leaked @"D:\Games" would serialize as
            // "D:\\Games" and silently fail a Contains(@"D:\Games") check — the scan would pass on a real
            // leak. Emit BOTH the raw JSON and a backslash-unescaped copy so path-shaped literals are
            // matchable either way. (Only the backslash needs this: none of the forbidden literals contain
            // a character the default JavaScriptEncoder escapes, such as <, >, &, + or non-ASCII.)
            var json = JsonSerializer.Serialize(value, value.GetType());
            return json + "\n" + json.Replace(@"\\", @"\", StringComparison.Ordinal);
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
