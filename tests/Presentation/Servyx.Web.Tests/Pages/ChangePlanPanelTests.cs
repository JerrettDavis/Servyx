using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Composition;
using Servyx.Domain.Configuration;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Pages.Servers;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the panel that turns recorded desired values into a previewed
/// <see cref="ConfigChangePlan"/>. No test here calls, mocks a call to, or asserts anything about
/// <c>IPlanExecutor.ApplyAsync</c> — this phase never calls it.
/// </summary>
public class ChangePlanPanelTests : BunitContext
{
    private const string ServerId = "container-1";

    private static readonly PlannedAction WriteAction = new(
        PlannedActionKind.WriteSurface,
        "env",
        "--- a/.env\n+++ b/.env\n@@ -1,1 +1,1 @@\n-PORT=8211\n+PORT=9000\n",
        true,
        TransportCapabilities.FileWrite);

    private static readonly PlannedAction ControlChannelAction = new(
        PlannedActionKind.WriteControlChannel,
        "rcon",
        "--- a/rcon\n+++ b/rcon\n@@ -1,1 +1,1 @@\n-x\n+y\n",
        false,
        TransportCapabilities.ExecuteCommand);

    private static readonly BlockedChange OneBlockedChange = new(
        "OTHER_KEY", "surface-x", "the pointer is not addressable", "add a write binding to the definition");

    private static ConfigChangePlan FullyAchievablePlan(bool withControlChannel = false) => new(
        "plan-1",
        [withControlChannel ? ControlChannelAction : WriteAction],
        [new Consequence(ConsequenceKind.RestartRequired, "The workload must be restarted.")],
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        Diagnostics = [new PlanDiagnostic(PlanDiagnosticKind.ManualRegenerationRequired, "ini", "Regenerate by hand.")],
    };

    private static ConfigChangePlan PartiallyAchievablePlan() => new(
        "plan-2",
        [WriteAction],
        [],
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        Blocked = [OneBlockedChange],
    };

    private static ConfigChangePlan BlockedPlan() => new(
        "plan-3",
        [],
        [],
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        Blocked = [OneBlockedChange],
    };

    private IRenderedComponent<ChangePlanPanel> RenderPanel(
        IPlanExecutor? executor,
        ProvisioningGate? gate = null,
        WriteMode writeMode = WriteMode.Enabled,
        IReadOnlyDictionary<string, string>? desiredValues = null,
        bool hasUnsavedEdits = false,
        IReadOnlyList<string>? unsavedKeys = null)
    {
        if (executor is not null)
        {
            Services.AddSingleton(executor);
        }

        Services.AddSingleton(gate ?? new ProvisioningGate(enabled: true));

        return Render<ChangePlanPanel>(p => p
            .Add(x => x.ServerId, ServerId)
            .Add(x => x.WriteMode, writeMode)
            .Add(x => x.DesiredValues, desiredValues ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .Add(x => x.HasUnsavedEdits, hasUnsavedEdits)
            .Add(x => x.UnsavedKeys, unsavedKeys ?? []));
    }

    private static IPlanExecutor ExecutorReturning(ConfigChangePlan plan)
    {
        var executor = Substitute.For<IPlanExecutor>();
        executor.PreviewAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(plan));
        return executor;
    }

    // ── Missing executor ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_host_that_composed_no_plan_executor_degrades_to_locked_and_explained()
    {
        var panel = RenderPanel(executor: null);

        panel.Find("[data-testid='plan-executor-unavailable']").TextContent
            .Should().Contain("plan executor",
                because: "degrading closed and visibly, never hidden, is this codebase's convention everywhere else");
        panel.FindAll("[data-testid='plan-preview-button']").Should().BeEmpty();
    }

    // ── Preview reads exactly the recorded values ────────────────────────────────────────────────────

    [Fact]
    public void Preview_passes_exactly_the_recorded_values_never_editor_state()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var desired = new Dictionary<string, string>(StringComparer.Ordinal) { ["KEY_A"] = "recorded-value" };

        var panel = RenderPanel(executor, desiredValues: desired);
        panel.Find("[data-testid='plan-preview-button']").Click();

        executor.Received(1).PreviewAsync(
            ServerId,
            Arg.Is<IReadOnlyDictionary<string, string>>(d =>
                d != null && d.Count == 1 && d.ContainsKey("KEY_A") && d["KEY_A"] == "recorded-value"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Unsaved_edits_block_preview_and_name_the_affected_rows()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());

        var panel = RenderPanel(executor, hasUnsavedEdits: true, unsavedKeys: ["PORT", "SERVER_NAME"]);

        var notice = panel.Find("[data-testid='plan-unsaved-edits-block']").TextContent;
        notice.Should().Contain("PORT");
        notice.Should().Contain("SERVER_NAME");

        panel.Find("[data-testid='plan-preview-button']").HasAttribute("disabled").Should().BeTrue();

        executor.DidNotReceive().PreviewAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReadOnly_locks_the_preview_control()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());

        var panel = RenderPanel(executor, writeMode: WriteMode.ReadOnly);

        panel.Find("[data-testid='plan-preview-button']").HasAttribute("disabled").Should().BeTrue();
    }

    // ── Feasibility banners ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FullyAchievable_renders_the_all_changes_banner_and_an_apply_affordance()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var banner = panel.Find("[data-testid='plan-feasibility-banner']").TextContent;
        banner.Should().Contain("All 1 change");
        banner.Should().Contain("can be written");

        panel.Find("[data-testid='plan-apply-affordance']").Should().NotBeNull();
    }

    [Fact]
    public void PartiallyAchievable_renders_the_N_of_M_banner()
    {
        var executor = ExecutorReturning(PartiallyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var banner = panel.Find("[data-testid='plan-feasibility-banner']").TextContent;
        banner.Should().Contain("1 of 2");
        banner.Should().Contain("the rest are blocked and applying will not perform them");

        panel.Find("[data-testid='plan-apply-affordance']").Should().NotBeNull();
    }

    [Fact]
    public void Blocked_renders_the_nothing_can_be_written_banner_and_no_apply_affordance()
    {
        var executor = ExecutorReturning(BlockedPlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var banner = panel.Find("[data-testid='plan-feasibility-banner']").TextContent;
        banner.Should().Contain("Nothing can be written");
        banner.Should().Contain("approving would do nothing");

        panel.FindAll("[data-testid='plan-apply-affordance']").Should().BeEmpty(
            because: "a Blocked plan must not offer an apply affordance at all — absent, not merely disabled");
    }

    [Fact]
    public void Every_blocked_change_remediation_hint_appears_in_the_DOM()
    {
        var executor = ExecutorReturning(PartiallyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        panel.Markup.Should().Contain(OneBlockedChange.RemediationHint);
        panel.Markup.Should().Contain(OneBlockedChange.Reason);
    }

    [Fact]
    public void ManualRegenerationRequired_diagnostics_render_as_a_warning()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var diagnostic = panel.Find("[data-testid='plan-diagnostic']");
        diagnostic.ClassList.Should().Contain("plan-diagnostic-warning");
        diagnostic.TextContent.Should().Contain("Regenerate by hand.");
    }

    [Fact]
    public void The_reversibility_note_is_always_present()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        panel.Find("[data-testid='plan-reversibility-note']").TextContent
            .Should().Contain("Revert is not implemented; the only way back is a new plan.");
    }

    [Fact]
    public void Diffs_are_rendered_for_every_action()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();

        panel.Find("[data-testid='plan-diff']").TextContent.Should().Contain("PORT=9000");
    }

    // ── Gating notices ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreviewOnly_with_an_open_gate_renders_the_overview_tab_notice()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor, gate: new ProvisioningGate(true), writeMode: WriteMode.PreviewOnly);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var affordance = panel.Find("[data-testid='plan-apply-affordance']").TextContent;
        affordance.Should().Contain("Preview only");
        affordance.Should().Contain("Raise write access on the Overview tab");
    }

    [Fact]
    public void A_closed_gate_names_the_configuration_key_instead()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor, gate: new ProvisioningGate(false), writeMode: WriteMode.PreviewOnly);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var affordance = panel.Find("[data-testid='plan-apply-affordance']").TextContent;
        affordance.Should().Contain(ProvisioningGate.ConfigurationKey);
        affordance.Should().Contain("cannot be raised in this process at all");
    }

    [Fact]
    public void A_control_channel_plan_shows_the_permanent_refusal()
    {
        var executor = ExecutorReturning(FullyAchievablePlan(withControlChannel: true));
        var panel = RenderPanel(executor, gate: new ProvisioningGate(true), writeMode: WriteMode.Enabled);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var affordance = panel.Find("[data-testid='plan-apply-affordance']").TextContent;
        affordance.Should().Contain("control-channel action");
        affordance.Should().Contain("nothing would be written");
    }

    [Fact]
    public void An_enabled_fully_achievable_plan_says_it_can_be_applied_but_offers_no_apply_button()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor, gate: new ProvisioningGate(true), writeMode: WriteMode.Enabled);

        panel.Find("[data-testid='plan-preview-button']").Click();

        panel.Find("[data-testid='plan-apply-affordance']").TextContent.Should().Contain("This plan can be applied");

        // Phase 1 never calls ApplyAsync — there is no button anywhere in this panel that could trigger it.
        panel.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Apply", StringComparison.OrdinalIgnoreCase)
            && b.GetAttribute("data-testid") != "plan-preview-button");
    }

    // ── Preview failure ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_failed_preview_is_surfaced_rather_than_silently_dropped()
    {
        var executor = Substitute.For<IPlanExecutor>();
        executor.PreviewAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ConfigChangePlan>(
                new InvalidOperationException("no game definition governs this server")));

        var panel = RenderPanel(executor);
        panel.Find("[data-testid='plan-preview-button']").Click();

        panel.Find("[data-testid='plan-error']").TextContent.Should().Contain("no game definition governs this server");
    }
}
