using AngleSharp.Dom;
using Bunit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Servyx.Domain.Lifecycle;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the Overview tab's real write controls: Start/Restart/Stop/Kill wired to
/// <see cref="IServerLifecycle"/>, gated on <see cref="WriteMode"/>, with a two-step confirmation for
/// every destructive action and a <see cref="WriteMode.PreviewOnly"/> branch that renders the stop plan
/// and nothing else.
/// </summary>
public class ServerOverviewTabTests : BunitContext
{
    private const string ServerId = "palygondwanaland";

    private static ServerDetail SampleDetail() =>
        new MockDashboardDataService().GetServerDetailAsync(ServerId).GetAwaiter().GetResult()!;

    private static StopPlan SamplePlan() => new(
    [
        new StopStage.Rcon("shutdown", TimeSpan.FromSeconds(45)),
        new StopStage.Signal("SIGINT", TimeSpan.FromSeconds(30)),
        new StopStage.Kill(),
    ]);

    private IRenderedComponent<ServerOverviewTab> RenderOverview(
        WriteMode writeMode,
        IServerLifecycle? lifecycle,
        StopPlan? stopPlan) =>
        Render<ServerOverviewTab>(p => p
            .Add(x => x.Detail, SampleDetail())
            .Add(x => x.ServerId, ServerId)
            .Add(x => x.WriteMode, writeMode)
            .Add(x => x.Lifecycle, lifecycle)
            .Add(x => x.StopPlan, stopPlan));

    /// <summary>
    /// Every Start/Restart/Stop/Kill button keeps GatedButton's default
    /// <c>data-testid="gated-button"</c> — the same generic testid these controls rendered before write
    /// support existed, and what <c>ServersListRemoteHostTests</c> still asserts against — so the four are
    /// distinguished by their trimmed label text, not by a bespoke id per action.
    /// </summary>
    private static IElement GatedButton(IRenderedComponent<ServerOverviewTab> cut, string label) =>
        cut.FindAll("[data-testid=gated-button]").Single(b => b.TextContent.Trim() == label);

    [Fact]
    public void Lifecycle_buttons_are_disabled_for_a_read_only_server()
    {
        var cut = RenderOverview(WriteMode.ReadOnly, Substitute.For<IServerLifecycle>(), SamplePlan());

        foreach (var label in new[] { "Start", "Restart", "Stop", "Kill" })
        {
            GatedButton(cut, label).HasAttribute("disabled").Should().BeTrue(because: $"'{label}' must stay disabled on a read-only server");
        }
    }

    [Fact]
    public void Lifecycle_buttons_are_enabled_for_a_writable_server()
    {
        var cut = RenderOverview(WriteMode.Enabled, Substitute.For<IServerLifecycle>(), SamplePlan());

        foreach (var label in new[] { "Start", "Restart", "Stop", "Kill" })
        {
            GatedButton(cut, label).HasAttribute("disabled").Should().BeFalse(because: $"'{label}' must be clickable once writes are enabled");
        }
    }

    [Fact]
    public void Preview_only_renders_the_stop_plan_and_no_apply_control()
    {
        var cut = RenderOverview(WriteMode.PreviewOnly, Substitute.For<IServerLifecycle>(), SamplePlan());

        // Not one button of any kind in the Power card — not even a disabled one. PreviewOnly renders the
        // computed plan and nothing an operator could mistake for a control one click from working.
        cut.Find("[data-testid=power-card]").QuerySelectorAll("button").Should().BeEmpty();

        var stages = cut.FindAll("[data-testid=lifecycle-stop-stage]");
        stages.Should().HaveCount(3);
        stages[0].TextContent.Should().Contain("shutdown").And.Contain("45s");
        stages[1].TextContent.Should().Contain("SIGINT").And.Contain("30s");
        stages[2].TextContent.Should().Contain("Force kill");
    }

    [Fact]
    public void Stop_requires_a_second_confirmation_click()
    {
        var lifecycle = Substitute.For<IServerLifecycle>();
        var cut = RenderOverview(WriteMode.Enabled, lifecycle, SamplePlan());

        GatedButton(cut, "Stop").Click();

        lifecycle.DidNotReceive().StopAsync(Arg.Any<StopPlan>(), Arg.Any<CancellationToken>());
        cut.Find("[data-testid=stop-confirm-step]").Should().NotBeNull();
        cut.FindAll("[data-testid=gated-button]").Should().NotContain(b => b.TextContent.Trim() == "Stop"); // hidden while confirming

        cut.Find("[data-testid=stop-confirm]").Click();

        lifecycle.Received(1).StopAsync(Arg.Any<StopPlan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_writes_disabled_refusal_is_rendered_to_the_operator()
    {
        var lifecycle = Substitute.For<IServerLifecycle>();
        lifecycle.StartAsync(Arg.Any<CancellationToken>())
            .Throws(new WritesDisabledException(
                $"Refusing to run command 'docker start' on '{ServerId}': the server's write mode is ReadOnly."));

        var cut = RenderOverview(WriteMode.Enabled, lifecycle, SamplePlan());

        GatedButton(cut, "Start").Click();

        cut.Find("[data-testid=lifecycle-error]").TextContent
            .Should().Be($"Refusing to run command 'docker start' on '{ServerId}': the server's write mode is ReadOnly.");
    }

    [Fact]
    public void A_generic_lifecycle_failure_renders_as_a_wrapped_message_not_the_writes_disabled_path()
    {
        var lifecycle = Substitute.For<IServerLifecycle>();
        lifecycle.StartAsync(Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("daemon unreachable"));

        var cut = RenderOverview(WriteMode.Enabled, lifecycle, SamplePlan());

        GatedButton(cut, "Start").Click();

        cut.Find("[data-testid=lifecycle-error]").TextContent.Should().Contain("Start failed").And.Contain("daemon unreachable");
    }

    [Fact]
    public void Kill_bypasses_the_ladder_with_a_single_stage_plan()
    {
        var lifecycle = Substitute.For<IServerLifecycle>();
        var cut = RenderOverview(WriteMode.Enabled, lifecycle, SamplePlan());

        GatedButton(cut, "Kill").Click();
        cut.Find("[data-testid=kill-confirm]").Click();

        lifecycle.Received(1).StopAsync(
            Arg.Is<StopPlan>(plan => plan != null && plan.Stages.Count == 1 && plan.Stages[0] is StopStage.Kill),
            Arg.Any<CancellationToken>());
    }
}
