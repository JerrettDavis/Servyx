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
/// <see cref="ConfigChangePlan"/> and then, behind a two-step confirmation, applies it.
/// </summary>
/// <remarks>
/// The apply half of this file is the only test coverage of the product's one operator-reachable path to
/// <see cref="IPlanExecutor.ApplyAsync"/> — the call that writes configuration to a live game server. Its
/// tests are therefore weighted towards what the UI <em>says</em> after the call as much as whether the call
/// happened: a receipt that implied a restart Servyx never performed, a fidelity failure that read as
/// recoverable, or a second <c>ApplyAsync</c> from one double-click would each be a worse defect than a
/// missing button.
/// </remarks>
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

    private static ConfigChangePlan RecreatePlan() => new(
        "plan-4",
        [WriteAction],
        [new Consequence(ConsequenceKind.RecreateRequired, "The container must be recreated.")],
        new Dictionary<string, string>(StringComparer.Ordinal));

    private static IPlanExecutor ExecutorReturning(ConfigChangePlan plan)
    {
        var executor = Substitute.For<IPlanExecutor>();
        executor.PreviewAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(plan));
        return executor;
    }

    /// <summary>An executor that previews <paramref name="plan"/> and applies it successfully.</summary>
    private static IPlanExecutor ExecutorApplying(ConfigChangePlan plan, DateTimeOffset? appliedAt = null)
    {
        var executor = ExecutorReturning(plan);
        executor.ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChangeReceipt(
                plan.Id, appliedAt ?? new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero), plan.Actions)));
        return executor;
    }

    /// <summary>An executor that previews <paramref name="plan"/> and then fails the apply with <paramref name="failure"/>.</summary>
    private static IPlanExecutor ExecutorFailingApply(ConfigChangePlan plan, Exception failure)
    {
        var executor = ExecutorReturning(plan);
        executor.ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChangeReceipt>(failure));
        return executor;
    }

    /// <summary>Walks the whole operator path: preview, then both steps of the apply confirmation.</summary>
    private static void PreviewThenApply(IRenderedComponent<ChangePlanPanel> panel)
    {
        panel.Find("[data-testid='plan-preview-button']").Click();
        panel.Find("[data-testid='plan-apply-review']").Click();
        panel.Find("[data-testid='plan-apply-confirm']").Click();
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

        // The note now describes a revert that EXISTS, with its real preconditions. Asserting the
        // preconditions rather than a whole sentence is deliberate: this copy must stay true to what
        // IPlanExecutor.RevertAsync actually refuses on, and those are the three refusals it names.
        var note = panel.Find("[data-testid='plan-reversibility-note']").TextContent;
        note.Should().Contain("reversible");
        note.Should().Contain("retention window");
        note.Should().Contain("already been reverted");
        note.Should().NotContain("Revert is not implemented");
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
    public void An_enabled_fully_achievable_plan_says_it_can_be_applied_and_offers_the_first_confirm_step()
    {
        var executor = ExecutorReturning(FullyAchievablePlan());
        var panel = RenderPanel(executor, gate: new ProvisioningGate(true), writeMode: WriteMode.Enabled);

        panel.Find("[data-testid='plan-preview-button']").Click();

        var affordance = panel.Find("[data-testid='plan-apply-affordance']").TextContent;
        affordance.Should().Contain("This plan can be applied");
        affordance.Should().Contain("does not restart or recreate the workload", because:
            "the receipt this control leads to means \"the bytes are on disk\", never \"the workload picked "
            + "them up\", and the affordance that offers it must not imply otherwise");

        panel.Find("[data-testid='plan-apply-review']").Should().NotBeNull();
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

    // ── Apply: the two-step confirmation ──────────────────────────────────────────────────────────────

    [Fact]
    public void Reviewing_alone_never_applies_anything()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();
        panel.Find("[data-testid='plan-apply-review']").Click();

        panel.Find("[data-testid='plan-apply-confirm-step']").Should().NotBeNull();
        executor.DidNotReceive().ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_confirm_button_names_the_plan_it_would_apply()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();
        panel.Find("[data-testid='plan-apply-review']").Click();

        panel.Find("[data-testid='plan-apply-confirm']").TextContent.Should().Contain("Yes, apply plan plan-1");
    }

    [Fact]
    public void Confirming_applies_exactly_the_previewed_plan_id()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        executor.Received(1).ApplyAsync("plan-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Cancelling_the_confirmation_leaves_the_plan_unapplied()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        panel.Find("[data-testid='plan-preview-button']").Click();
        panel.Find("[data-testid='plan-apply-review']").Click();
        panel.Find("[data-testid='plan-apply-cancel']").Click();

        panel.FindAll("[data-testid='plan-apply-confirm-step']").Should().BeEmpty();
        panel.Find("[data-testid='plan-apply-review']").Should().NotBeNull();
        executor.DidNotReceive().ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The double-apply guard. A never-completing <c>ApplyAsync</c> holds the panel in its in-flight state
    /// across both clicks, which is exactly the window a real double-click lands in: without the guard the
    /// second click would issue a second write of the same plan.
    /// </summary>
    [Fact]
    public void A_double_click_on_confirm_issues_exactly_one_apply()
    {
        var inFlight = new TaskCompletionSource<ChangeReceipt>();
        var executor = ExecutorReturning(FullyAchievablePlan());
        executor.ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(inFlight.Task);

        var panel = RenderPanel(executor);
        panel.Find("[data-testid='plan-preview-button']").Click();
        panel.Find("[data-testid='plan-apply-review']").Click();

        panel.Find("[data-testid='plan-apply-confirm']").Click();
        panel.Find("[data-testid='plan-apply-confirm']").Click();

        executor.Received(1).ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        inFlight.SetResult(new ChangeReceipt("plan-1", DateTimeOffset.UnixEpoch, []));
    }

    [Fact]
    public void An_applied_plan_offers_no_second_apply()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        panel.FindAll("[data-testid='plan-apply-review']").Should().BeEmpty(
            because: "a plan is applicable exactly once — ApplyAsync refuses any plan that is no longer Previewed");
        panel.FindAll("[data-testid='plan-apply-confirm']").Should().BeEmpty();
    }

    // ── Apply: what a receipt is allowed to claim ─────────────────────────────────────────────────────

    [Fact]
    public void A_receipt_says_the_bytes_are_on_the_server_and_never_that_anything_was_restarted()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        var receipt = panel.Find("[data-testid='plan-apply-receipt']").TextContent;
        receipt.Should().Contain("Applied at 2026-08-11 09:30:00Z");
        receipt.Should().Contain("The approved bytes are on the server");
        receipt.Should().Contain("did not restart the workload");

        // FullyAchievablePlan's own consequence description reads "The workload must be restarted." — so an
        // implementation that echoed the plan's consequence text into the receipt would fail here, which is
        // the point. A ChangeReceipt means the bytes are on disk, never that a workload picked them up.
        receipt.Should().NotContain("restarted", because:
            "ApplyAsync never restarts or recreates anything (PlanExecutor.ApplyAsync), so no word on the "
            + "success path may suggest it did");
    }

    [Fact]
    public void A_restart_consequence_points_at_the_Overview_tab_rather_than_duplicating_the_control()
    {
        var executor = ExecutorApplying(FullyAchievablePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        var followUp = panel.Find("[data-testid='plan-apply-restart-followup']").TextContent;
        followUp.Should().Contain("Overview");

        panel.FindAll("button").Should().NotContain(
            b => b.TextContent.Contains("Restart", StringComparison.OrdinalIgnoreCase),
            because: "lifecycle control lives on the Overview tab; a second copy here could drift from it");
    }

    [Fact]
    public void A_recreate_consequence_says_plainly_that_Servyx_cannot_recreate_the_container()
    {
        var executor = ExecutorApplying(RecreatePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        var followUp = panel.Find("[data-testid='plan-apply-recreate-followup']").TextContent;
        followUp.Should().Contain("Servyx cannot recreate a container");
        followUp.Should().Contain("NotSupportedException");
        followUp.Should().Contain("until someone recreates it outside Servyx");

        panel.FindAll("[data-testid='plan-apply-restart-followup']").Should().BeEmpty();
    }

    [Fact]
    public void A_plan_with_no_restart_or_recreate_consequence_says_so_rather_than_going_quiet()
    {
        var executor = ExecutorApplying(PartiallyAchievablePlan());
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        panel.Find("[data-testid='plan-apply-no-followup']").TextContent
            .Should().Contain("no restart or recreate consequence");
    }

    // ── Apply: failure ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The exception each failure label stands for, built here rather than carried through
    /// <c>[MemberData]</c> so the theory's own arguments stay plain serializable strings.
    /// </summary>
    private static Exception FailureFor(string label) => label switch
    {
        "stale" => new PlanStaleException("Change plan 'plan-1' expired at 2026-08-11 09:00:00Z.", "plan-1"),
        "writes-disabled" => new WritesDisabledException("Writes are disabled for this server's current write mode."),
        "concurrency" => new ChangePlanConcurrencyException("The change plan was modified by someone else.", "plan-1"),
        "fidelity" => new PlanApplyFidelityException(
            "reading it back found different content", "plan-1", 3, "env", "aaa111", "bbb222"),
        "unrecognized" => new InvalidOperationException("Change plan 'plan-1' is Applied, not Previewed."),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "No such failure label."),
    };

    /// <summary>
    /// Every failure mode gets its own account, and none of them takes the plan off the screen. An operator
    /// reading "this write may already have landed" needs the diff that describes it still in front of them.
    /// </summary>
    [Theory]
    [InlineData("stale", "The server changed since this plan was built")]
    [InlineData("writes-disabled", "write access is not Enabled")]
    [InlineData("concurrency", "Another session got to this plan first")]
    [InlineData("fidelity", "failed its content-fidelity check")]
    [InlineData("unrecognized", "no specific handling")]
    public void Each_apply_failure_renders_its_own_account_and_keeps_the_plan_on_screen(
        string label, string expectedMarker)
    {
        var executor = ExecutorFailingApply(FullyAchievablePlan(), FailureFor(label));
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        panel.Find("[data-testid='plan-apply-failure-message']").TextContent
            .Should().Contain(expectedMarker, because: $"the {label} failure has its own operator-facing account");

        panel.FindAll("[data-testid='plan-apply-receipt']").Should().BeEmpty(
            because: "a failed apply must never render as an applied one");
        panel.Find("[data-testid='plan-feasibility-banner']").Should().NotBeNull(
            because: "the approved plan stays on screen next to the account of what happened to it");
        panel.Find("[data-testid='plan-diff']").TextContent.Should().Contain("PORT=9000");
    }

    /// <summary>
    /// The failure's kind reaches the DOM as its own class, so a stale refusal and a fidelity mismatch can
    /// never style — or be scraped — as the same thing.
    /// </summary>
    [Theory]
    [InlineData("stale", "plan-apply-failure-stale")]
    [InlineData("writes-disabled", "plan-apply-failure-writesdisabled")]
    [InlineData("concurrency", "plan-apply-failure-concurrency")]
    [InlineData("fidelity", "plan-apply-failure-fidelity")]
    [InlineData("unrecognized", "plan-apply-failure-unrecognized")]
    public void Each_apply_failure_carries_its_own_kind_into_the_markup(string label, string expectedClass)
    {
        var executor = ExecutorFailingApply(FullyAchievablePlan(), FailureFor(label));
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        panel.Find("[data-testid='plan-apply-failure']").ClassList.Should().Contain(expectedClass);
    }

    [Fact]
    public void The_stale_path_offers_a_fresh_preview()
    {
        var executor = ExecutorFailingApply(
            FullyAchievablePlan(), new PlanStaleException("A bound surface drifted.", "plan-1"));
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        var again = panel.Find("[data-testid='plan-apply-preview-again']");
        again.TextContent.Should().Contain("Preview again");

        again.Click();

        executor.Received(2).PreviewAsync(
            ServerId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        panel.FindAll("[data-testid='plan-apply-failure']").Should().BeEmpty(
            because: "a fresh plan is a fresh approval — the previous plan's failure must not sit next to it");
    }

    [Theory]
    [InlineData("writes-disabled")]
    [InlineData("concurrency")]
    [InlineData("fidelity")]
    public void Only_the_stale_path_offers_a_fresh_preview(string label)
    {
        Exception failure = label switch
        {
            "writes-disabled" => new WritesDisabledException("Writes are disabled."),
            "concurrency" => new ChangePlanConcurrencyException("Lost the race.", "plan-1"),
            _ => new PlanApplyFidelityException("mismatch", "plan-1", 0, "env", "aaa111", "bbb222"),
        };

        var executor = ExecutorFailingApply(FullyAchievablePlan(), failure);
        var panel = RenderPanel(executor);

        PreviewThenApply(panel);

        panel.FindAll("[data-testid='plan-apply-preview-again']").Should().BeEmpty(because:
            "re-previewing does not answer any of these — and after a fidelity failure it would read as the "
            + "recovery step, when the actual next step is a human looking at the file");
    }

    [Fact]
    public void The_fidelity_path_carries_both_digests_and_refuses_to_imply_a_repair()
    {
        var executor = ExecutorFailingApply(
            FullyAchievablePlan(),
            new PlanApplyFidelityException(
                "reading it back found content hashing to observed-digest-222 where approved-digest-111 was approved.",
                "plan-1",
                7,
                "compose-env",
                "approved-digest-111",
                "observed-digest-222"));

        var panel = RenderPanel(executor);
        PreviewThenApply(panel);

        var message = panel.Find("[data-testid='plan-apply-failure-message']").TextContent;

        message.Should().Contain("approved-digest-111");
        message.Should().Contain("observed-digest-222");
        message.Should().Contain("compose-env");
        message.Should().Contain("#7");

        message.Should().Contain("did not undo it, rewrite it, or retry it", because:
            "there is deliberately no auto-repair on this path — no rewrite, no retry, no rollback");
        message.Should().Contain("partially applied");
        message.Should().Contain("a human has to look at the");
    }

    [Fact]
    public void An_unrecognized_failure_is_surfaced_as_itself_rather_than_swallowed()
    {
        var executor = ExecutorFailingApply(
            FullyAchievablePlan(), new TimeoutException("the transport gave up after 30s"));

        var panel = RenderPanel(executor);
        PreviewThenApply(panel);

        var message = panel.Find("[data-testid='plan-apply-failure-message']").TextContent;

        message.Should().Contain(nameof(TimeoutException), because:
            "an unknown failure must be distinguishable from the four Servyx knows how to explain");
        message.Should().Contain("the transport gave up after 30s");
    }

    // ── Apply: the affordance is absent, not disabled, when the engine would refuse ───────────────────

    [Theory]
    [InlineData("blocked", WriteMode.Enabled, true, true)]
    [InlineData("control-channel", WriteMode.Enabled, true, false)]
    [InlineData("preview-only", WriteMode.PreviewOnly, true, false)]
    [InlineData("read-only", WriteMode.ReadOnly, true, false)]
    [InlineData("gate-closed-preview-only", WriteMode.PreviewOnly, false, false)]
    public void A_plan_that_cannot_be_applied_offers_no_apply_affordance_at_all(
        string label, WriteMode writeMode, bool gateOpen, bool blocked)
    {
        var plan = blocked
            ? BlockedPlan()
            : FullyAchievablePlan(withControlChannel: label == "control-channel");

        var executor = ExecutorApplying(plan);
        var panel = RenderPanel(executor, gate: new ProvisioningGate(gateOpen), writeMode: writeMode);

        if (writeMode != WriteMode.ReadOnly)
        {
            panel.Find("[data-testid='plan-preview-button']").Click();
        }

        panel.FindAll("[data-testid='plan-apply-review']").Should().BeEmpty(because:
            $"{label}: an apply control ApplyAsync would refuse must be absent, not disabled — a disabled one "
            + "still says approving is a thing that could work here");
        panel.FindAll("[data-testid='plan-apply-confirm']").Should().BeEmpty();
        executor.DidNotReceive().ApplyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
