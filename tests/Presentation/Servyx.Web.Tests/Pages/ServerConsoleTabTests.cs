using Bunit;
using NSubstitute;
using Servyx.Domain.Rcon;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Rcon;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the Console tab's catalogued command surface: a &lt;select&gt; of a definition's
/// declared RCON commands, never a free-text/raw shell prompt, with read-only commands available
/// regardless of write mode and mutating ones behind the same two-step confirmation every other
/// destructive control in this app uses.
/// </summary>
public class ServerConsoleTabTests : BunitContext
{
    private static IReadOnlyList<RconCommand> SampleCatalogue() =>
    [
        new RconCommand("info", "Info", ReadOnly: true),
        new RconCommand("players", "ShowPlayers", ReadOnly: true),
        new RconCommand("save", "Save", ReadOnly: false),
        new RconCommand("broadcast", "Broadcast {message}", ReadOnly: false),
        new RconCommand("shutdown", "Shutdown {seconds} \"{message}\"", ReadOnly: false),
    ];

    private IRenderedComponent<ServerConsoleTab> RenderConsole(
        WriteMode writeMode,
        Func<CancellationToken, Task<IRconSession?>>? resolveSession,
        IReadOnlyList<RconCommand>? commands = null) =>
        Render<ServerConsoleTab>(p => p
            .Add(x => x.Logs, Array.Empty<LogLine>())
            .Add(x => x.Commands, commands ?? SampleCatalogue())
            .Add(x => x.WriteMode, writeMode)
            .Add(x => x.ResolveSession, resolveSession));

    [Fact]
    public void The_console_offers_only_catalogued_commands()
    {
        var session = Substitute.For<IRconSession>();
        var cut = RenderConsole(WriteMode.ReadOnly, _ => Task.FromResult<IRconSession?>(session));

        var options = cut.FindAll("[data-testid=console-command-select] option")
            .Select(o => o.GetAttribute("value"))
            .ToList();

        options.Should().BeEquivalentTo(SampleCatalogue().Select(c => c.Id));

        // Not a shell prompt: no free-text raw-command box exists anywhere on the tab.
        cut.FindAll("input[placeholder*='RCON']").Should().BeEmpty();
    }

    [Fact]
    public void A_read_only_console_command_is_available_without_write_mode()
    {
        var session = Substitute.For<IRconSession>();
        session.InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RconResponse("Server info", true)));

        var cut = RenderConsole(WriteMode.ReadOnly, _ => Task.FromResult<IRconSession?>(session));

        cut.Find("[data-testid=console-command-select]").Change("info");
        cut.Find("[data-testid=console-send]").HasAttribute("disabled").Should().BeFalse();

        cut.Find("[data-testid=console-send]").Click();

        cut.FindAll("[data-testid=console-confirm-step]").Should().BeEmpty();
        session.Received(1).InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_mutating_console_command_requires_confirmation()
    {
        var session = Substitute.For<IRconSession>();
        session.InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RconResponse("Saved", true)));

        var cut = RenderConsole(WriteMode.Enabled, _ => Task.FromResult<IRconSession?>(session));

        cut.Find("[data-testid=console-command-select]").Change("save");
        cut.Find("[data-testid=console-send]").Click();

        session.DidNotReceive().InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
        cut.Find("[data-testid=console-confirm-step]").Should().NotBeNull();

        cut.Find("[data-testid=console-confirm]").Click();

        session.Received(1).InvokeAsync("save", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_mutating_console_command_is_refused_without_write_mode()
    {
        var session = Substitute.For<IRconSession>();
        var cut = RenderConsole(WriteMode.ReadOnly, _ => Task.FromResult<IRconSession?>(session));

        cut.Find("[data-testid=console-command-select]").Change("save");

        cut.Find("[data-testid=console-send]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Console_renders_the_rcon_response()
    {
        var session = Substitute.For<IRconSession>();
        session.InvokeAsync("info", Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RconResponse("Welcome to Pal Server[v0.1.0]", true)));

        var cut = RenderConsole(WriteMode.ReadOnly, _ => Task.FromResult<IRconSession?>(session));

        cut.Find("[data-testid=console-command-select]").Change("info");
        cut.Find("[data-testid=console-send]").Click();

        cut.Find("[data-testid=console-response]").TextContent.Should().Be("Welcome to Pal Server[v0.1.0]");
    }

    [Fact]
    public void An_unreachable_rcon_chain_surfaces_why_each_strategy_failed()
    {
        const string message =
            "No reachability strategy for 'rcon' could reach the endpoint: direct-tcp (port not published), " +
            "docker-exec-tool (no ssh+docker host configured).";

        var cut = RenderConsole(WriteMode.ReadOnly, _ => throw new RconUnreachableException(message));

        cut.Find("[data-testid=console-unreachable]").TextContent.Should().Be(message);
        cut.FindAll("[data-testid=console-command-select]").Should().BeEmpty();
    }
}
