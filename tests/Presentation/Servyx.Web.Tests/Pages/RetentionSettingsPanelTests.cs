using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Composition;
using Servyx.Web.Components.Pages.AppSettings;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the change plan retention panel.
/// </summary>
/// <remarks>
/// Two properties matter here. The window itself is read-only and the panel says <em>why</em> and names the
/// exact configuration keys, rather than offering an editor that would write to a store nothing reads — the
/// same treatment <c>WriteModeControl</c> gives <c>Servyx:Provisioning:Enabled</c>. And the one thing that is
/// a runtime action — sweeping now — is destructive (a swept plan can never be reverted), so it takes two
/// deliberate clicks and the confirmation copy says so.
/// </remarks>
public class RetentionSettingsPanelTests : BunitContext
{
    [Fact]
    public void An_uncomposed_sweeper_is_reported_rather_than_the_panel_vanishing()
    {
        Arrange();

        var cut = RenderPanel(RetentionSettingsSection.Unavailable(ChangePlanRetentionOptions.SectionKey));

        cut.Find("[data-testid=retention-unavailable]").Should().NotBeNull();
        cut.FindAll("[data-testid=retention-sweep-review]").Should().BeEmpty();
    }

    [Fact]
    public void The_effective_window_and_schedule_are_rendered_in_units_an_operator_reads()
    {
        Arrange();

        var cut = RenderPanel(Section(retention: TimeSpan.FromDays(30), sweep: TimeSpan.FromHours(6)));

        cut.Find("[data-testid=retention-window]").TextContent.Should().Contain("30 days");
        cut.Find("[data-testid=retention-interval]").TextContent.Should().Contain("6 hours");
        cut.Find("[data-testid=retention-enabled]").TextContent.Should().Contain("Enabled");
    }

    [Fact]
    public void The_window_is_read_only_and_the_panel_names_the_keys_that_change_it()
    {
        Arrange();

        var cut = RenderPanel(Section());

        // Hiding the one control that would explain the lock is what this product's README forbids; naming
        // the keys is the alternative it takes everywhere else.
        cut.FindAll("input").Should().BeEmpty();
        var note = cut.Find("[data-testid=retention-configuration-note]").TextContent;
        note.Should().Contain($"{ChangePlanRetentionOptions.SectionKey}:ImageRetentionDays");
        note.Should().Contain($"{ChangePlanRetentionOptions.SectionKey}:SweepMinutes");
        note.Should().Contain($"{ChangePlanRetentionOptions.SectionKey}:Enabled");
    }

    [Fact]
    public void A_switched_off_sweep_is_called_out_as_keeping_plaintext_indefinitely()
    {
        Arrange();

        var cut = RenderPanel(Section(enabled: false));

        var warning = cut.Find("[data-testid=retention-disabled-warning]").TextContent;
        warning.Should().Contain("plaintext");
        warning.Should().Contain("indefinitely");
    }

    [Fact]
    public void Sweeping_takes_two_deliberate_clicks_and_the_confirmation_says_what_is_lost()
    {
        var service = Arrange();

        var cut = RenderPanel(Section());

        cut.Find("[data-testid=retention-sweep-review]").Click();
        service.SweepCalls.Should().Be(0, "reviewing must never be the act itself");

        var body = cut.Find("[data-testid=retention-sweep-confirm-body]").TextContent;
        body.Should().Contain("Nothing has been purged yet");
        body.Should().Contain("no longer be reverted");

        cut.Find("[data-testid=retention-sweep-confirm]").Click();
        service.SweepCalls.Should().Be(1);
    }

    [Fact]
    public void Cancelling_the_confirmation_sweeps_nothing()
    {
        var service = Arrange();

        var cut = RenderPanel(Section());

        cut.Find("[data-testid=retention-sweep-review]").Click();
        cut.Find("[data-testid=retention-sweep-cancel]").Click();

        service.SweepCalls.Should().Be(0);
        cut.FindAll("[data-testid=retention-sweep-confirm-step]").Should().BeEmpty();
    }

    [Fact]
    public void A_completed_sweep_reports_exactly_what_it_discarded()
    {
        var service = Arrange();
        service.SweepResult = new RetentionSweepResult(RetentionSweepOutcome.Swept, 4, 2, 9, null);

        var cut = RenderPanel(Section());
        cut.Find("[data-testid=retention-sweep-review]").Click();
        cut.Find("[data-testid=retention-sweep-confirm]").Click();

        var applied = cut.Find("[data-testid=retention-sweep-applied]").TextContent;
        applied.Should().Contain("4");
        applied.Should().Contain("9");
        applied.Should().Contain("2");
        cut.FindAll("[data-testid=retention-sweep-error]").Should().BeEmpty();
    }

    [Fact]
    public void A_sweep_refused_because_retention_is_disabled_is_never_reported_as_a_successful_one()
    {
        var service = Arrange();
        service.SweepResult = RetentionSweepResult.Disabled;

        var cut = RenderPanel(Section(enabled: false));
        cut.Find("[data-testid=retention-sweep-review]").Click();
        cut.Find("[data-testid=retention-sweep-confirm]").Click();

        cut.Find("[data-testid=retention-sweep-error]").TextContent.Should().Contain("switched off");
        cut.FindAll("[data-testid=retention-sweep-applied]").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_sweep_says_nothing_was_purged()
    {
        var service = Arrange();
        service.SweepResult = new RetentionSweepResult(RetentionSweepOutcome.Failed, 0, 0, 0, "the database is locked");

        var cut = RenderPanel(Section());
        cut.Find("[data-testid=retention-sweep-review]").Click();
        cut.Find("[data-testid=retention-sweep-confirm]").Click();

        var error = cut.Find("[data-testid=retention-sweep-error]").TextContent;
        error.Should().Contain("nothing was purged");
        error.Should().Contain("the database is locked");
    }

    private FakeSettingsDataService Arrange()
    {
        var service = new FakeSettingsDataService();
        Services.AddSingleton<ISettingsDataService>(service);
        return service;
    }

    private IRenderedComponent<RetentionSettingsPanel> RenderPanel(RetentionSettingsSection section) =>
        Render<RetentionSettingsPanel>(parameters => parameters.Add(p => p.Section, section));

    private static RetentionSettingsSection Section(
        bool enabled = true, TimeSpan? retention = null, TimeSpan? sweep = null) =>
        new(Available: true,
            enabled,
            retention ?? TimeSpan.FromDays(30),
            sweep ?? TimeSpan.FromHours(1),
            ChangePlanRetentionOptions.SectionKey);
}
