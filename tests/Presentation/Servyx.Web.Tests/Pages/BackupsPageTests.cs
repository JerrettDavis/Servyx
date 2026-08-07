using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Web.Components.Pages.Backups;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the Backups page — the surface an operator creates, inspects, restores, and prunes
/// backups from.
/// </summary>
/// <remarks>
/// The two claims these are built around: with <c>Servyx:Provisioning:Enabled</c> off the page is exactly
/// the read-only list it always was, and with it on no destructive action is one click away from a
/// listing.
/// </remarks>
public class BackupsPageTests : BunitContext
{
    private const string ServyxArtifact = "servyx-20260101T030000Z";
    private const string ForeignArtifact = "palworld-2026-01-01";

    /// <summary>The mock's single adopted server; the page's write grants are keyed on its name.</summary>
    private const string ServerName = "Palygondwanaland";

    private FakeBackupDashboard Arrange(bool gateOpen = true, bool writable = true, bool registerDashboard = true)
    {
        var dashboard = new FakeBackupDashboard()
            .With(ServyxArtifact, BackupOwnership.Servyx)
            .With(ForeignArtifact, BackupOwnership.Foreign);

        Services.AddSingleton<IDashboardDataService>(new MockDashboardDataService());
        Services.AddSingleton(new ProvisioningGate(gateOpen));
        Services.AddSingleton(writable ? new WritableServers([ServerName]) : WritableServers.None);

        if (registerDashboard)
        {
            Services.AddSingleton<IBackupDashboard>(dashboard);
        }

        return dashboard;
    }

    // ── Flag off: nothing changes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void With_the_flag_off_the_read_only_view_is_unchanged_and_offers_no_control()
    {
        Arrange(gateOpen: false);

        var cut = Render<BackupsPage>();

        // The read-only view's own copy: no backup provider is registered with the flag off, so nothing
        // here can create, restore, or prune.
        cut.Markup.Should().Contain("Servyx-owned backup creation, retention, and restore require");
        cut.Markup.Should().Contain("Servyx:Provisioning:Enabled");
        cut.Markup.Should().Contain("discovered read-only");

        // Not one control of any kind — not even a disabled one.
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty();

        // And none of the new surface leaks into the closed-gate branch.
        cut.FindAll("[data-testid=create-backup]").Should().BeEmpty();
        cut.FindAll("[data-testid=plan-restore]").Should().BeEmpty();
        cut.FindAll("[data-testid=preview-prune]").Should().BeEmpty();
        cut.FindAll("[data-testid=backup-row]").Should().BeEmpty();

        // "prune", "retention" and "restore" do occur — in the unchanged Milestone 5 sentence and the
        // foreign badge's reassurance tooltip. What must not occur is any of the managed surface's own
        // vocabulary, none of which existed on this page before.
        var lower = cut.Markup.ToLowerInvariant();
        lower.Should().NotContain("overwrite");
        lower.Should().NotContain("dry run");
        lower.Should().NotContain("data will be destroyed");
    }

    [Fact]
    public void With_the_flag_off_every_listed_archive_is_still_labelled_foreign()
    {
        Arrange(gateOpen: false);

        var cut = Render<BackupsPage>();

        var badges = cut.FindAll(".foreign-badge");
        badges.Should().NotBeEmpty();
        foreach (var badge in badges)
        {
            badge.GetAttribute("title").Should().Contain("Servyx will never prune, move, or rename");
        }
    }

    /// <summary>
    /// Regression guard: the closed-gate listing used to render every row's ownership badge as "Foreign"
    /// unconditionally, regardless of the entry's actual <see cref="Servyx.Web.Models.BackupOwnership"/> —
    /// harmless while the mock data source's five sample backups were all Foreign, but wrong the moment a
    /// real listing could contain a Servyx-owned archive too.
    /// </summary>
    [Fact]
    public void Read_only_ownership_badges_reflect_actual_ownership_not_a_hardcoded_foreign_label()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));
        Services.AddSingleton(WritableServers.None);
        Services.AddSingleton<IDashboardDataService>(new FixedBackupsListDataService(
            new Servyx.Web.Models.BackupsListResult(
                [
                    new Servyx.Web.Models.BackupEntry(
                        "srv", "Server", "owned.tar.gz", DateTimeOffset.UnixEpoch, 1024,
                        Servyx.Web.Models.BackupOwnership.ServyxOwned),
                    new Servyx.Web.Models.BackupEntry(
                        "srv", "Server", "cron.tar.gz", DateTimeOffset.UnixEpoch, 2048,
                        Servyx.Web.Models.BackupOwnership.Foreign),
                ],
                Servyx.Web.Models.BackupsAvailability.Listed,
                null)));

        var cut = Render<BackupsPage>();

        cut.FindAll(".foreign-badge").Should().ContainSingle();
        cut.Markup.Should().Contain("owned.tar.gz");
        cut.Markup.Should().Contain("cron.tar.gz");

        // The Servyx-owned row must not carry the foreign badge or its "never prune" tooltip.
        var badges = cut.FindAll(".svx-badge");
        badges.Should().Contain(b => b.TextContent.Trim() == "Servyx");
    }

    /// <summary>
    /// The three closed-gate states — no provider configured, a listing failure, and a genuinely empty
    /// listing — must render distinguishably from one another. Only the last may say "No backups found".
    /// </summary>
    [Fact]
    public void No_backup_provider_configured_renders_distinguishably_from_none_and_failed()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));
        Services.AddSingleton(WritableServers.None);
        Services.AddSingleton<IDashboardDataService>(new FixedBackupsListDataService(
            new Servyx.Web.Models.BackupsListResult([], Servyx.Web.Models.BackupsAvailability.NotConfigured, null)));

        var notConfigured = Render<BackupsPage>();

        notConfigured.Find("[data-testid=backups-not-configured]").TextContent
            .Should().Contain("No backup provider is configured");
        notConfigured.FindAll("[data-testid=backups-list-failed]").Should().BeEmpty();
        notConfigured.FindAll("[data-testid=backups-empty]").Should().BeEmpty();
    }

    [Fact]
    public void A_closed_gate_listing_failure_renders_distinguishably_from_not_configured_and_from_none()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));
        Services.AddSingleton(WritableServers.None);
        Services.AddSingleton<IDashboardDataService>(new FixedBackupsListDataService(
            new Servyx.Web.Models.BackupsListResult(
                [], Servyx.Web.Models.BackupsAvailability.Failed, "daemon unreachable")));

        var cut = Render<BackupsPage>();

        cut.Find("[data-testid=backups-list-failed]").TextContent.Should().Contain("could not be listed");
        cut.Find("[data-testid=backups-list-failure-detail]").TextContent.Should().Contain("daemon unreachable");
        cut.FindAll("[data-testid=backups-not-configured]").Should().BeEmpty();
        cut.FindAll("[data-testid=backups-empty]").Should().BeEmpty();
    }

    [Fact]
    public void A_closed_gate_genuinely_empty_listing_renders_the_original_empty_state()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: false));
        Services.AddSingleton(WritableServers.None);
        Services.AddSingleton<IDashboardDataService>(new FixedBackupsListDataService(
            new Servyx.Web.Models.BackupsListResult([], Servyx.Web.Models.BackupsAvailability.Listed, null)));

        var cut = Render<BackupsPage>();

        cut.Find("[data-testid=backups-empty]").TextContent.Should().Contain("No backups found");
        cut.FindAll("[data-testid=backups-not-configured]").Should().BeEmpty();
        cut.FindAll("[data-testid=backups-list-failed]").Should().BeEmpty();
    }

    // ── Flag on but unwired ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_the_flag_on_but_no_provider_the_page_says_so_rather_than_rendering_controls()
    {
        Arrange(registerDashboard: false);

        var cut = Render<BackupsPage>();

        cut.Find("[data-testid=backups-misconfigured]").TextContent.Should().Contain("not wired");
        cut.FindAll("[data-testid=create-backup]").Should().BeEmpty();
        cut.FindAll("[data-testid=preview-prune]").Should().BeEmpty();
    }

    // ── Ownership and the absence of a prune control ──────────────────────────────────────────────

    [Fact]
    public void Foreign_artifacts_render_as_listable_inspectable_and_restorable_with_no_prune_control()
    {
        Arrange();

        var cut = Render<BackupsPage>();

        var foreignRow = cut.FindAll("[data-testid=backup-row]")
            .Single(r => r.GetAttribute("data-ownership") == nameof(BackupOwnership.Foreign));

        // Listable, and unmistakably labelled as not ours.
        foreignRow.QuerySelector(".foreign-badge")!.GetAttribute("title")
            .Should().Contain("Servyx will never prune, move, or rename");

        // Inspectable and restorable.
        foreignRow.QuerySelectorAll("[data-testid=inspect-backup]").Should().ContainSingle();
        foreignRow.QuerySelectorAll("[data-testid=plan-restore]").Should().ContainSingle();

        // Never prunable: no prune control in the row, disabled or otherwise. The word "prune" does occur
        // in the badge's tooltip — as the promise that Servyx never will — so this asserts on controls,
        // which is the thing that could actually delete an archive.
        foreignRow.QuerySelectorAll("[data-testid=apply-prune]").Should().BeEmpty();
        foreignRow.QuerySelectorAll("[data-testid=preview-prune]").Should().BeEmpty();
        NoPruneOrDeleteControls(foreignRow);
    }

    /// <summary>Asserts that nothing clickable or editable inside <paramref name="element"/> prunes or deletes.</summary>
    private static void NoPruneOrDeleteControls(AngleSharp.Dom.IElement element)
    {
        foreach (var control in element.QuerySelectorAll("button, input, a"))
        {
            var text = (control.TextContent + " " + control.GetAttribute("data-testid") + " " +
                        control.GetAttribute("aria-label") + " " + control.GetAttribute("value")).ToLowerInvariant();

            text.Should().NotContain("prune");
            text.Should().NotContain("delete");
        }
    }

    [Fact]
    public void No_row_of_either_ownership_carries_a_prune_control()
    {
        Arrange();

        var cut = Render<BackupsPage>();

        // Retention is expressed per server, never per artifact, so there is no per-row prune to
        // accidentally show for the wrong ownership.
        foreach (var row in cut.FindAll("[data-testid=backup-row]"))
        {
            NoPruneOrDeleteControls(row);
        }

        cut.Find("[data-testid=prune-foreign-note]").TextContent
            .Should().Contain("Foreign artifacts are never pruned");
    }

    /// <summary>
    /// The existing guarantee, re-asserted after the backup-wiring changes: a dry run never proposes the
    /// foreign artifact as a candidate, and applying it deletes only the Servyx-owned one.
    /// </summary>
    [Fact]
    public void Foreign_archives_are_excluded_from_prune()
    {
        var dashboard = Arrange();
        dashboard.PruneCandidates.Add(ServyxArtifact);
        dashboard.SkippedForeign = 1;

        var cut = Render<BackupsPage>();
        cut.Find("[data-testid=preview-prune]").Click();

        var candidates = cut.Find("[data-testid=prune-candidates]");
        candidates.TextContent.Should().Contain(ServyxArtifact);
        candidates.TextContent.Should().NotContain(ForeignArtifact);
        cut.Find("[data-testid=prune-preview-summary]").TextContent.Should().Contain("1 foreign");

        cut.Find("[data-testid=apply-prune]").Click();
        dashboard.ApplyPruneCalls.Should().Be(1);

        // No control anywhere on the page — in the foreign row or elsewhere — can prune a foreign artifact.
        var foreignRow = cut.FindAll("[data-testid=backup-row]")
            .Single(r => r.GetAttribute("data-ownership") == nameof(BackupOwnership.Foreign));
        NoPruneOrDeleteControls(foreignRow);
    }

    // ── Restore: preview, then a separate confirmation ────────────────────────────────────────────

    [Fact]
    public void Previewing_a_restore_renders_the_affected_paths_and_restores_nothing()
    {
        var dashboard = Arrange();

        var cut = Render<BackupsPage>();
        cut.FindAll("[data-testid=plan-restore]")[0].Click();

        cut.Find("[data-testid=restore-affected-paths]").TextContent.Should().Contain("Level.sav");
        cut.Find("[data-testid=restore-nothing-written]").TextContent.Should().Contain("Nothing has been overwritten");

        // The claim: planning reached PlanRestoreAsync and nothing reached ApplyRestoreAsync.
        dashboard.PlanRestoreCalls.Should().Be(1);
        dashboard.ApplyRestoreCalls.Should().Be(0);
    }

    /// <summary>
    /// The full triple guard, re-asserted after the backup-wiring changes: planning never applies, the apply
    /// control is dead until the separate acknowledgement is given, and only then does a click reach
    /// <c>ApplyRestoreAsync</c>.
    /// </summary>
    [Fact]
    public void Restore_still_requires_the_plan_then_acknowledge_then_apply_sequence()
    {
        var dashboard = Arrange();

        var cut = Render<BackupsPage>();

        // Nothing to apply before a restore has even been planned.
        cut.FindAll("[data-testid=apply-restore]").Should().BeEmpty();

        cut.FindAll("[data-testid=plan-restore]")[0].Click();
        dashboard.PlanRestoreCalls.Should().Be(1);
        dashboard.ApplyRestoreCalls.Should().Be(0);

        // The control exists now, but is disabled until the acknowledgement — a separate control — is given.
        var apply = cut.Find("[data-testid=apply-restore]");
        apply.HasAttribute("disabled").Should().BeTrue();
        apply.Click();
        dashboard.ApplyRestoreCalls.Should().Be(0);

        cut.Find("[data-testid=restore-acknowledge]").Change(true);
        cut.Find("[data-testid=apply-restore]").HasAttribute("disabled").Should().BeFalse();

        cut.Find("[data-testid=apply-restore]").Click();
        dashboard.ApplyRestoreCalls.Should().Be(1);
        dashboard.PlanRestoreCalls.Should().Be(1);
    }

    [Fact]
    public void The_restore_overwrite_risk_is_unmissable()
    {
        Arrange();

        var cut = Render<BackupsPage>();
        cut.FindAll("[data-testid=plan-restore]")[0].Click();

        // The same banner the deploy page uses for a destructive update, at its highest severity.
        var banner = cut.Find("[data-testid=data-impact]");
        banner.GetAttribute("data-severity").Should().Be("danger");
        banner.GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid=data-impact-headline]").TextContent.Should().Contain("DATA WILL BE DESTROYED");

        // Plus restore-specific copy, also announced, that names the overwrite in as many words.
        var warning = cut.Find("[data-testid=restore-overwrite-warning]");
        warning.GetAttribute("role").Should().Be("alert");
        warning.TextContent.Should().Contain("overwrites live save data");
        warning.TextContent.Should().Contain("no undo");
    }

    [Fact]
    public void A_restore_requires_an_explicit_second_confirmation()
    {
        var dashboard = Arrange();

        var cut = Render<BackupsPage>();
        cut.FindAll("[data-testid=plan-restore]")[0].Click();

        // The acknowledgement is a separate control from the confirmation, and the confirmation is dead
        // until it is completed.
        cut.Find("[data-testid=restore-acknowledgement-step]").Should().NotBeNull();
        var apply = cut.Find("[data-testid=apply-restore]");
        apply.HasAttribute("disabled").Should().BeTrue();

        apply.Click();
        dashboard.ApplyRestoreCalls.Should().Be(0);

        cut.Find("[data-testid=restore-acknowledge]").Change(true);
        cut.Find("[data-testid=apply-restore]").HasAttribute("disabled").Should().BeFalse();

        cut.Find("[data-testid=apply-restore]").Click();
        dashboard.ApplyRestoreCalls.Should().Be(1);
        cut.Find("[data-testid=restore-success]").TextContent.Should().Contain("no undo");
    }

    // ── Prune: dry run first ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_prune_dry_run_shows_candidates_and_removes_nothing()
    {
        var dashboard = Arrange();
        dashboard.PruneCandidates.Add(ServyxArtifact);
        dashboard.SkippedForeign = 1;

        var cut = Render<BackupsPage>();

        // Before any dry run there is no control that deletes.
        cut.FindAll("[data-testid=apply-prune]").Should().BeEmpty();

        cut.Find("[data-testid=preview-prune]").Click();

        cut.Find("[data-testid=prune-preview-summary]").TextContent.Should().Contain("Nothing has been deleted");
        cut.Find("[data-testid=prune-candidates]").TextContent.Should().Contain(ServyxArtifact);

        dashboard.PreviewPruneCalls.Should().Be(1);
        dashboard.ApplyPruneCalls.Should().Be(0);

        // Only now does the deleting control exist at all.
        cut.Find("[data-testid=apply-prune]").Click();
        dashboard.ApplyPruneCalls.Should().Be(1);
    }

    [Fact]
    public void A_dry_run_with_no_candidates_offers_nothing_to_apply()
    {
        var dashboard = Arrange();

        var cut = Render<BackupsPage>();
        cut.Find("[data-testid=preview-prune]").Click();

        cut.Find("[data-testid=prune-no-candidates]").Should().NotBeNull();
        cut.FindAll("[data-testid=apply-prune]").Should().BeEmpty();
        dashboard.ApplyPruneCalls.Should().Be(0);
    }

    // ── Create ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Creating_a_backup_is_a_confirm_action_and_warns_about_the_quiesce()
    {
        var dashboard = Arrange();

        var cut = Render<BackupsPage>();

        cut.Find("[data-testid=create-section]").TextContent.Should().Contain("may be quiesced first");
        cut.FindAll("[data-testid=create-backup-confirm]").Should().BeEmpty();

        cut.Find("[data-testid=create-backup]").Click();
        dashboard.CreateCalls.Should().Be(0);

        cut.Find("[data-testid=create-backup-confirm]").Click();
        dashboard.CreateCalls.Should().Be(1);
        cut.Find("[data-testid=create-success]").TextContent.Should().Contain("Created backup");
    }

    [Fact]
    public void A_failing_backup_is_surfaced_rather_than_swallowed()
    {
        var dashboard = Arrange();
        dashboard.CreateResult = new BackupCreateResult.Failed(
            "Quiesce command 'save' timed out.",
            "BackupQuiesceFailedException");

        var cut = Render<BackupsPage>();
        cut.Find("[data-testid=create-backup]").Click();
        cut.Find("[data-testid=create-backup-confirm]").Click();

        cut.Find("[data-testid=create-error]").TextContent.Should().Contain("Quiesce command 'save' timed out.");
        cut.FindAll("[data-testid=create-success]").Should().BeEmpty();
    }

    // ── Read-only servers ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_read_only_server_says_so_instead_of_offering_controls_that_would_throw()
    {
        var dashboard = Arrange(writable: false);

        var cut = Render<BackupsPage>();

        var notice = cut.Find("[data-testid=server-read-only]");
        notice.TextContent.Should().Contain("read-only");
        cut.Find("[data-testid=write-mode-key]").TextContent
            .Should().Be($"Servyx:Servers:{ServerName}:WriteMode");

        // Nothing that writes is offered — not creating, not restoring, not pruning.
        cut.FindAll("[data-testid=create-backup]").Should().BeEmpty();
        cut.FindAll("[data-testid=plan-restore]").Should().BeEmpty();
        cut.FindAll("[data-testid=preview-prune]").Should().BeEmpty();
        cut.FindAll("[data-testid=apply-prune]").Should().BeEmpty();

        // Reading still works: the listing and the inspect control are both present.
        cut.FindAll("[data-testid=backup-row]").Should().HaveCount(2);
        cut.FindAll("[data-testid=inspect-backup]").Should().HaveCount(2);

        dashboard.CreateCalls.Should().Be(0);
    }

    // ── Inspect ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inspecting_shows_the_manifest_without_extracting()
    {
        Arrange();

        var cut = Render<BackupsPage>();
        cut.FindAll("[data-testid=inspect-backup]")[1].Click();

        cut.Find("[data-testid=inspect-summary]").TextContent.Should().Contain("nothing was extracted");
        cut.Find("[data-testid=inspect-entries]").TextContent.Should().Contain("Level.sav");
    }
}
