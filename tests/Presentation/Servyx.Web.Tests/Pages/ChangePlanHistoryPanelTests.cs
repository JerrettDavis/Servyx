using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Servyx.Domain.Common;
using Servyx.Domain.Configuration;
using Servyx.Domain.Entities;
using Servyx.Web.Components.Pages.Servers;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the read-only panel that lists a server's recent change plans.
/// </summary>
/// <remarks>
/// <para>
/// The weight of this file is on what the panel <em>claims</em> about a plan, not on whether rows appear. A
/// history row is the only place an operator ever sees that a write reached the server and came back holding
/// bytes nobody approved — the case the read-back verification design exists to catch — so a plan rendering as
/// a clean "Applied" when its digests disagree would be a worse defect than the panel failing to render at
/// all.
/// </para>
/// <para>
/// Nothing here clicks anything that mutates, because there is nothing to click: the panel deliberately
/// carries no apply or revert affordance. <see cref="The_panel_offers_no_mutation_affordance"/> pins that.
/// </para>
/// </remarks>
public class ChangePlanHistoryPanelTests : BunitContext
{
    private static readonly ServerId TrackedServer = ServerId.Parse("11111111-1111-1111-1111-111111111111");

    private const string ApprovedDigest = "aaaaaaaaaaaa111111111111111111111111111111111111111111111111aaaa";
    private const string ObservedDigest = "bbbbbbbbbbbb222222222222222222222222222222222222222222222222bbbb";

    private static ChangePlanActionSummary Action(
        int ordinal,
        bool writeReachedServer,
        PostWriteVerification verification = PostWriteVerification.Verified,
        string? postImageHash = ApprovedDigest,
        string? observedPostImageHash = ApprovedDigest,
        string? failureReason = null,
        ChangePlanActionStatus status = ChangePlanActionStatus.Applied,
        string surfaceId = "env") => new(
        Guid.NewGuid(),
        ordinal,
        surfaceId,
        $"/srv/{surfaceId}",
        PlannedActionKind.WriteSurface,
        status,
        writeReachedServer,
        postImageHash,
        observedPostImageHash,
        verification,
        failureReason,
        writeReachedServer ? new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero) : null,
        null);

    private static ChangePlanSummary Plan(
        ChangePlanStatus status,
        params ChangePlanActionSummary[] actions) => new(
        ChangePlanId.Parse("22222222-2222-2222-2222-222222222222"),
        TrackedServer,
        status,
        new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero),
        "operator",
        status is ChangePlanStatus.Applied or ChangePlanStatus.PartiallyApplied
            ? new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero)
            : null,
        status is ChangePlanStatus.Applied or ChangePlanStatus.PartiallyApplied ? "operator" : null,
        null,
        null,
        actions);

    private static IChangePlanStore StoreReturning(params ChangePlanSummary[] plans)
    {
        var store = Substitute.For<IChangePlanStore>();
        store.ListRecentAsync(Arg.Any<ServerId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ChangePlanSummary>>(plans));
        return store;
    }

    private IRenderedComponent<ChangePlanHistoryPanel> RenderPanel(
        IChangePlanStore? store, bool tracked = true)
    {
        if (store is not null)
        {
            Services.AddSingleton(store);
        }

        return Render<ChangePlanHistoryPanel>(p => p
            .Add(x => x.ServerId, tracked ? TrackedServer : null));
    }

    // ── Degrading states ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_host_that_composed_no_change_plan_store_degrades_to_locked_and_explained()
    {
        var panel = RenderPanel(store: null);

        panel.Find("[data-testid='plan-history-store-unavailable']").TextContent
            .Should().Contain("change plan store",
                because: "degrading closed and visibly, never hidden, is this codebase's convention everywhere else");

        panel.Find("[data-testid='plan-history-panel']").Should().NotBeNull(
            because: "the panel stays on screen and explains itself rather than vanishing");
        panel.FindAll("[data-testid='plan-history-list']").Should().BeEmpty();
    }

    [Fact]
    public void An_untracked_server_says_no_plan_was_ever_recorded_rather_than_showing_an_error()
    {
        var store = StoreReturning();

        var panel = RenderPanel(store, tracked: false);

        panel.Find("[data-testid='plan-history-untracked']").TextContent
            .Should().Contain("does not track this container");
        panel.FindAll("[data-testid='plan-history-error']").Should().BeEmpty(because:
            "an untracked container has no history to fail to read — that is not an error state");

        store.DidNotReceive().ListRecentAsync(Arg.Any<ServerId>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_empty_history_renders_an_explanatory_empty_state_rather_than_a_blank_card()
    {
        var panel = RenderPanel(StoreReturning());

        panel.Find("[data-testid='plan-history-empty']").TextContent
            .Should().Contain("No change plan has ever been previewed for this server");
        panel.FindAll("[data-testid='plan-history-plan']").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_read_is_surfaced_rather_than_rendered_as_an_empty_history()
    {
        var store = Substitute.For<IChangePlanStore>();
        store.ListRecentAsync(Arg.Any<ServerId>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ChangePlanSummary>>(
                new InvalidOperationException("the database is unreachable")));

        var panel = RenderPanel(store);

        panel.Find("[data-testid='plan-history-error']").TextContent
            .Should().Contain("the database is unreachable");
        panel.FindAll("[data-testid='plan-history-empty']").Should().BeEmpty(because:
            "\"could not be read\" and \"this server was never changed\" are opposite claims");
    }

    // ── The badge tells the truth about what reached the server ──────────────────────────────────────

    [Fact]
    public void A_plan_where_only_some_actions_reached_the_server_renders_as_partially_applied()
    {
        var plan = Plan(
            ChangePlanStatus.PartiallyApplied,
            Action(0, writeReachedServer: true),
            Action(1, writeReachedServer: false, verification: PostWriteVerification.NotAttempted,
                observedPostImageHash: null, status: ChangePlanActionStatus.Skipped, surfaceId: "ini"));

        var panel = RenderPanel(StoreReturning(plan));

        var badge = panel.Find("[data-testid='plan-history-badge']");
        badge.TextContent.Trim().Should().Be("Partially applied");
        badge.ClassList.Should().Contain("plan-history-badge-partiallyapplied");

        panel.Find("[data-testid='plan-history-outcome-detail']").TextContent
            .Should().Contain("1 of 2 actions recorded a write that reached the server");
    }

    /// <summary>
    /// The case the whole read-back-verification design exists to catch: every write reached the server, so
    /// a reach-count-only view would call this a clean apply — but one surface came back hashing to something
    /// nobody approved, which means the bytes were mangled in transit or after landing. A green "Applied"
    /// here would tell an operator the opposite of what happened.
    /// </summary>
    [Fact]
    public void A_digest_mismatch_renders_as_partially_applied_even_when_every_write_reached_the_server()
    {
        var plan = Plan(
            ChangePlanStatus.Applied,
            Action(0, writeReachedServer: true),
            Action(1,
                writeReachedServer: true,
                verification: PostWriteVerification.Mismatched,
                observedPostImageHash: ObservedDigest,
                status: ChangePlanActionStatus.Failed,
                surfaceId: "ini"));

        var panel = RenderPanel(StoreReturning(plan));

        var badge = panel.Find("[data-testid='plan-history-badge']");
        badge.TextContent.Trim().Should().Be("Partially applied", because:
            "a write that landed as bytes nobody approved must never render as a clean apply, whatever the "
            + "plan row's own status says");
        badge.ClassList.Should().NotContain("plan-history-badge-applied");

        panel.Find("[data-testid='plan-history-outcome-detail']").TextContent
            .Should().Contain("holding bytes nobody approved");

        var mismatch = panel.Find("[data-testid='plan-history-digest-mismatch']").TextContent;
        mismatch.Should().Contain("These digests do not match");
        mismatch.Should().Contain("changed in transit");

        // Both digests are on screen side by side, truncated for reading but complete in the title attribute:
        // a shortened digest is enough to see that two differ, never enough to check one against a file.
        var approved = panel.FindAll("[data-testid='plan-history-approved-digest']")[1];
        var observed = panel.FindAll("[data-testid='plan-history-observed-digest']")[1];

        approved.TextContent.Should().Contain(ApprovedDigest[..12]);
        approved.GetAttribute("title").Should().Be(ApprovedDigest);
        observed.TextContent.Should().Contain(ObservedDigest[..12]);
        observed.GetAttribute("title").Should().Be(ObservedDigest);
    }

    [Fact]
    public void A_plan_no_write_reached_renders_as_not_applied()
    {
        var plan = Plan(
            ChangePlanStatus.Failed,
            Action(0, writeReachedServer: false, verification: PostWriteVerification.NotAttempted,
                observedPostImageHash: null, status: ChangePlanActionStatus.Failed));

        var panel = RenderPanel(StoreReturning(plan));

        panel.Find("[data-testid='plan-history-badge']").TextContent.Trim().Should().Be("Not applied");
        panel.Find("[data-testid='plan-history-outcome-detail']").TextContent
            .Should().Contain("nothing here changed the server");
    }

    [Fact]
    public void A_plan_whose_every_action_was_verified_renders_as_applied()
    {
        var plan = Plan(ChangePlanStatus.Applied, Action(0, writeReachedServer: true));

        var panel = RenderPanel(StoreReturning(plan));

        var badge = panel.Find("[data-testid='plan-history-badge']");
        badge.TextContent.Trim().Should().Be("Applied");
        badge.ClassList.Should().Contain("plan-history-badge-applied");

        panel.Find("[data-testid='plan-history-outcome-detail']").TextContent
            .Should().Contain("not that the workload has re-read them", because:
                "an applied plan means the bytes are on disk, never that anything picked them up");
    }

    [Fact]
    public void A_write_nobody_could_read_back_is_never_reported_as_verified()
    {
        var plan = Plan(
            ChangePlanStatus.Applied,
            Action(0,
                writeReachedServer: true,
                verification: PostWriteVerification.Unverifiable,
                observedPostImageHash: null));

        var panel = RenderPanel(StoreReturning(plan));

        panel.Find("[data-testid='plan-history-badge']").TextContent.Trim()
            .Should().Be("Applied, not verified");
        panel.Find("[data-testid='plan-history-verification']").TextContent
            .Should().Contain("could not be read back");
    }

    // ── Per-action evidence ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_action_states_whether_its_write_reached_the_server()
    {
        var plan = Plan(
            ChangePlanStatus.PartiallyApplied,
            Action(0, writeReachedServer: true),
            Action(1, writeReachedServer: false, verification: PostWriteVerification.NotAttempted,
                observedPostImageHash: null, status: ChangePlanActionStatus.Skipped, surfaceId: "ini"));

        var panel = RenderPanel(StoreReturning(plan));

        var reached = panel.FindAll("[data-testid='plan-history-reached']");
        reached.Should().HaveCount(2);
        reached[0].TextContent.Should().Contain("A write for this action reached the server");
        reached[1].TextContent.Should().Contain("No write for this action reached the server");
    }

    [Fact]
    public void A_failure_reason_is_rendered_verbatim()
    {
        const string Reason =
            "Action #1 wrote surface 'ini' at '/srv/ini', but reading it back found content hashing to "
            + "bbbb where aaaa was approved.";

        var plan = Plan(
            ChangePlanStatus.PartiallyApplied,
            Action(0,
                writeReachedServer: true,
                verification: PostWriteVerification.Mismatched,
                observedPostImageHash: ObservedDigest,
                failureReason: Reason,
                status: ChangePlanActionStatus.Failed));

        var panel = RenderPanel(StoreReturning(plan));

        panel.Find("[data-testid='plan-history-failure-reason']").TextContent.Trim()
            .Should().Be(Reason, because:
                "the engine's own account is the only precise record of what happened and is never reworded");
    }

    [Fact]
    public void An_action_with_no_failure_reason_renders_none()
    {
        var panel = RenderPanel(StoreReturning(Plan(ChangePlanStatus.Applied, Action(0, writeReachedServer: true))));

        panel.FindAll("[data-testid='plan-history-failure-reason']").Should().BeEmpty();
    }

    [Fact]
    public void A_missing_observed_digest_is_named_rather_than_rendered_as_a_blank()
    {
        var plan = Plan(
            ChangePlanStatus.Applied,
            Action(0,
                writeReachedServer: true,
                verification: PostWriteVerification.Unverifiable,
                observedPostImageHash: null));

        var panel = RenderPanel(StoreReturning(plan));

        panel.Find("[data-testid='plan-history-observed-digest']").TextContent
            .Should().Contain("(none recorded)", because:
                "an empty digest would read as \"the file is empty\", a different and more alarming claim");
        panel.FindAll("[data-testid='plan-history-digest-mismatch']").Should().BeEmpty(because:
            "nothing having been read back is not the same as something having been read back and disagreed");
    }

    // ── Reading, not acting ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_offers_no_mutation_affordance()
    {
        var plan = Plan(ChangePlanStatus.Applied, Action(0, writeReachedServer: true));

        var panel = RenderPanel(StoreReturning(plan));

        panel.FindAll("button").Should().NotContain(
            b => b.TextContent.Contains("Revert", StringComparison.OrdinalIgnoreCase)
                || b.TextContent.Contains("Apply", StringComparison.OrdinalIgnoreCase),
            because: "reverting is an unrollbackable sequence of live-server writes and needs its own "
                + "confirm-flow design; a button on a history row would be the least considered place for it");
    }

    [Fact]
    public void The_default_listing_asks_for_ten_plans_of_the_server_it_was_given()
    {
        var store = StoreReturning();

        RenderPanel(store);

        store.Received(1).ListRecentAsync(TrackedServer, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_out_of_range_limit_is_clamped_rather_than_thrown_out_of_a_render()
    {
        var store = StoreReturning();
        Services.AddSingleton(store);

        Render<ChangePlanHistoryPanel>(p => p
            .Add(x => x.ServerId, TrackedServer)
            .Add(x => x.Limit, 5000));

        store.Received(1).ListRecentAsync(TrackedServer, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Refreshing_re_reads_the_history()
    {
        var store = StoreReturning(Plan(ChangePlanStatus.Applied, Action(0, writeReachedServer: true)));

        var panel = RenderPanel(store);
        panel.Find("[data-testid='plan-history-refresh']").Click();

        store.Received(2).ListRecentAsync(TrackedServer, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void No_plan_row_renders_file_content_or_a_diff()
    {
        var plan = Plan(ChangePlanStatus.Applied, Action(0, writeReachedServer: true));

        var panel = RenderPanel(StoreReturning(plan));

        panel.FindAll("[data-testid='plan-diff']").Should().BeEmpty(because:
            "ListRecentAsync deliberately excludes the pre/post images and the unified diff — those hold whole "
            + "configuration files unmasked, including real passwords");
    }
}
