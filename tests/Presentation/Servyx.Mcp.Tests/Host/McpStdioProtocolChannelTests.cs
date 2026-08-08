using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Host;

/// <summary>
/// Source-scans <c>src/Hosting/Servyx.Mcp.Stdio/Program.cs</c> and the whole MCP assemblies' source for the
/// one property that matters most for a stdio-transport server: stdout carries only JSON-RPC. A single
/// stray write to stdout anywhere in either assembly corrupts the protocol stream.
/// </summary>
public sealed class McpStdioProtocolChannelTests
{
    private static string StdioProgramText() => File.ReadAllText(Path.Combine(
        RepoRootLocator.Find().FullName, "src", "Hosting", "Servyx.Mcp.Stdio", "Program.cs"));

    [Fact]
    public void Stdio_host_clears_logging_providers_before_adding_its_own()
    {
        var text = StdioProgramText();

        var clearIndex = text.IndexOf("builder.Logging.ClearProviders()", StringComparison.Ordinal);
        var addConsoleIndex = text.IndexOf("builder.Logging.AddConsole(", StringComparison.Ordinal);

        clearIndex.Should().BeGreaterThan(-1, "the default host builder installs a stdout console provider that must be removed");
        addConsoleIndex.Should().BeGreaterThan(-1, "a stderr-routed console provider must replace it");
        clearIndex.Should().BeLessThan(addConsoleIndex, "providers must be cleared BEFORE the replacement is added, or the stray default briefly coexists");
    }

    [Fact]
    public void Stdio_host_routes_console_logging_to_stderr()
    {
        var text = StdioProgramText();

        text.Should().Contain(
            "LogToStandardErrorThreshold",
            "the host-level console logger must be configured to write to stderr, not stdout");
    }

    [Fact]
    public void Stdio_host_supplies_a_bootstrap_logger_factory_to_the_composition()
    {
        var text = StdioProgramText();

        text.Should().Contain(
            "AddServyxCore(bootstrapLoggerFactory)",
            "AddServyxCore builds bootstrap-phase loggers before the DI container exists; omitting the " +
            "stderr-routed factory here would leave those loggers writing to stdout, corrupting the protocol stream");
    }

    [Fact]
    public void Stdio_host_does_not_call_AddServiceDefaults()
    {
        // Line-scanned rather than a whole-file Contains: Program.cs's own header comment explicitly states
        // "AddServiceDefaults() is deliberately NOT called", which itself contains the substring this test
        // is checking for. Comment lines (trimmed text starting with "//") are excluded so that sentence
        // does not make this assertion fail on its own explanation.
        var offendingLines = StdioProgramText()
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(line => line.Contains("AddServiceDefaults", StringComparison.Ordinal))
            .ToList();

        offendingLines.Should().BeEmpty(
            "AddServiceDefaults wires ASP.NET Core instrumentation and endpoint mapping that mean nothing in " +
            "a stdio process, and is one more place a stray write to stdout could originate — found: " +
            string.Join(" | ", offendingLines));
    }

    [Fact]
    public void No_source_file_in_the_mcp_assemblies_calls_Console_Write_or_WriteLine()
    {
        var repoRoot = RepoRootLocator.Find();
        var offenders = new List<string>();

        foreach (var directory in new[] { "Presentation/Servyx.Mcp", "Hosting/Servyx.Mcp.Stdio" })
        {
            var dir = new DirectoryInfo(Path.Combine(repoRoot.FullName, "src", directory.Replace('/', Path.DirectorySeparatorChar)));
            if (!dir.Exists)
            {
                continue;
            }

            foreach (var file in EnumerateSourceFiles(dir))
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file.FullName))
                {
                    lineNumber++;
                    var trimmed = line.TrimStart();
                    var isProse = trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal);
                    if (isProse)
                    {
                        continue;
                    }

                    if (line.Contains("Console.Write", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(repoRoot.FullName, file.FullName)}:{lineNumber}: {line.Trim()}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "stdout is the protocol channel for a stdio-transport MCP server; a single stray Console.Write* " +
            "call anywhere in either assembly corrupts the JSON-RPC stream — found: " + string.Join(" | ", offenders));
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
