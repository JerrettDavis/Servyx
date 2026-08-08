using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Servyx.Mcp;
using Servyx.Mcp.Tests.Support;

namespace Servyx.Mcp.Tests.Inventory;

/// <summary>
/// Pins the tool surface's shape: every declared <c>[McpServerTool]</c> is named per the
/// <c>servyx_&lt;area&gt;_&lt;verb&gt;</c> scheme, carries a non-empty description, opts into
/// <c>UseStructuredContent</c>, and accepts a <see cref="CancellationToken"/> — and the inventory itself is
/// checked in both directions, so a rename cannot pass by being absent from both the declared and the
/// expected side at once.
/// </summary>
public sealed class McpToolInventoryTests
{
    private static readonly Assembly McpAssembly = typeof(ServyxMcpServer).Assembly;

    private sealed record DiscoveredTool(string Name, MethodInfo Method, McpServerToolAttribute Attribute);

    /// <summary>Every tool this build is expected to declare — updated deliberately, never incidentally, when a tool is added or renamed.</summary>
    private static readonly IReadOnlyList<string> ExpectedTools =
    [
        "servyx_host_describe",
        "servyx_servers_list",
        "servyx_server_get",
        "servyx_server_status_get",
        "servyx_server_metrics_get",
        "servyx_server_logs_read",
        "servyx_server_settings_list",
        "servyx_server_saves_get",
        "servyx_rcon_commands_list",
        "servyx_rcon_players_list",
        "servyx_rcon_invoke",
        "servyx_backups_list",
        "servyx_backup_inspect",
        "servyx_backup_restore_plan",
        "servyx_backup_prune_preview",
        "servyx_games_list",
        "servyx_game_definition_faults_list",
        "servyx_server_start",
        "servyx_server_stop_plan",
        "servyx_server_stop_apply",
        "servyx_server_restart_plan",
        "servyx_server_restart_apply",
        "servyx_server_kill_plan",
        "servyx_server_kill_apply",
    ];

    private static IReadOnlyList<DiscoveredTool> DiscoverTools()
    {
        var tools = new List<DiscoveredTool>();

        foreach (var type in IlScanner.LoadableTypes(McpAssembly))
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            {
                continue;
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                tools.Add(new DiscoveredTool(attribute.Name ?? method.Name, method, attribute));
            }
        }

        return tools;
    }

    public static TheoryData<string> EveryExpectedTool()
    {
        var data = new TheoryData<string>();
        foreach (var name in ExpectedTools)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Discovery_finds_at_least_one_declared_tool()
    {
        // Anti-vacuity: every theory below iterates the discovered set, so an empty discovery would make
        // every one of them pass having asserted nothing.
        DiscoverTools().Should().NotBeEmpty(
            "an empty discovered-tool set would make every per-tool theory in this file vacuously pass");
    }

    [Fact]
    public void Every_declared_tool_is_in_the_expected_inventory()
    {
        var declared = DiscoverTools().Select(t => t.Name).ToList();
        var undocumented = declared.Except(ExpectedTools, StringComparer.Ordinal).ToList();

        undocumented.Should().BeEmpty(
            "a tool was declared without this inventory being updated to name it deliberately — " +
            $"found: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void Every_expected_tool_is_declared()
    {
        // The reverse direction from the assertion above: a tool renamed or removed without updating
        // ExpectedTools must not silently disappear from coverage by being absent from both sides at once.
        var declared = DiscoverTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = ExpectedTools.Except(declared, StringComparer.Ordinal).ToList();

        missing.Should().BeEmpty(
            $"the expected inventory names a tool that is no longer declared: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(EveryExpectedTool))]
    public void Tool_name_matches_the_naming_scheme(string name)
    {
        name.Should().StartWith("servyx_", "every tool must live under the servyx_ namespace prefix");
        name.Should().MatchRegex("^[a-z0-9_]+$", "tool names must be lowercase snake_case with no dots");
        name.Should().NotContain(".", "tool names must never contain a dot");
    }

    [Theory]
    [MemberData(nameof(EveryExpectedTool))]
    public void Tool_uses_structured_content(string name)
    {
        var tool = DiscoverTools().Single(t => t.Name == name);
        tool.Attribute.UseStructuredContent.Should().BeTrue($"{name} must opt into UseStructuredContent");
    }

    [Theory]
    [MemberData(nameof(EveryExpectedTool))]
    public void Tool_has_a_non_empty_description(string name)
    {
        var tool = DiscoverTools().Single(t => t.Name == name);
        var description = tool.Method.GetCustomAttribute<DescriptionAttribute>();

        description.Should().NotBeNull($"{name} must carry a [Description]");
        description!.Description.Should().NotBeNullOrWhiteSpace($"{name}'s description must not be blank");
    }

    [Theory]
    [MemberData(nameof(EveryExpectedTool))]
    public void Tool_accepts_a_cancellation_token(string name)
    {
        var tool = DiscoverTools().Single(t => t.Name == name);
        tool.Method.GetParameters().Should().Contain(
            p => p.ParameterType == typeof(CancellationToken),
            $"{name} must accept a CancellationToken parameter");
    }

    [Fact]
    public void No_tool_accepts_a_parameter_named_confirm_or_force_or_yes()
    {
        var offenders = DiscoverTools()
            .SelectMany(t => t.Method.GetParameters().Select(p => $"{t.Name}({p.Name})"))
            .Where(entry =>
            {
                var name = entry[(entry.IndexOf('(') + 1)..^1];
                return string.Equals(name, "confirm", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "force", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "yes", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        offenders.Should().BeEmpty(
            "a confirm/force/yes parameter is exactly the shape a write-guard bypass would take; this build's " +
            $"read tools need none of them — found: {string.Join(", ", offenders)}");
    }
}
