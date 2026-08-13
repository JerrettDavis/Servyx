using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Composition;
using Servyx.Web.Components.Pages.AppSettings;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for <c>/settings</c> — the page that replaced a static "nothing to configure yet"
/// placeholder.
/// </summary>
/// <remarks>
/// The claim these are built around is the shape, not the styling: the page renders whatever sections the
/// settings service reports, dispatching each to the panel that understands its payload, so a later increment
/// adds a section without reshaping the page or the interface. The degraded states matter just as much —
/// a process that composed no settings service says so rather than rendering a page with nothing on it.
/// </remarks>
public class AppSettingsPageTests : BunitContext
{
    [Fact]
    public void With_no_settings_service_composed_the_page_says_so_rather_than_rendering_empty()
    {
        var cut = Render<AppSettingsPage>();

        cut.Find("[data-testid=settings-unavailable]").Should().NotBeNull();
        cut.FindAll("[data-testid=retention-settings-section]").Should().BeEmpty();
        cut.FindAll("[data-testid=host-connections-settings-section]").Should().BeEmpty();
        cut.FindAll("[data-testid=operator-password-settings-section]").Should().BeEmpty();
    }

    [Fact]
    public void A_service_that_reports_no_sections_at_all_renders_an_empty_state_not_a_blank_page()
    {
        Arrange();

        var cut = Render<AppSettingsPage>();

        cut.Find("[data-testid=settings-empty-state]").Should().NotBeNull();
    }

    [Fact]
    public void Every_reported_section_is_dispatched_to_the_panel_that_understands_its_payload()
    {
        Arrange(
            Retention(),
            new HostConnectionsSettingsSection(Available: true, 2, 2, ListingFailed: false, FailureDetail: null),
            Credential());

        var cut = Render<AppSettingsPage>();

        cut.Find("[data-testid=retention-settings-section]").Should().NotBeNull();
        cut.Find("[data-testid=host-connections-settings-section]").Should().NotBeNull();
        cut.Find("[data-testid=operator-password-settings-section]").Should().NotBeNull();
    }

    [Fact]
    public void A_section_this_page_has_no_panel_for_is_skipped_rather_than_failing_the_render()
    {
        // The extensibility property, asserted from the other direction: an ISettingsDataService that reports
        // a section type this build does not know about (a newer service behind an older page) must not take
        // the whole page down with it.
        Arrange(new UnknownSection(), Retention());

        var cut = Render<AppSettingsPage>();

        cut.Find("[data-testid=retention-settings-section]").Should().NotBeNull();
    }

    [Fact]
    public void Only_the_sections_the_service_reports_are_rendered()
    {
        Arrange(Retention());

        var cut = Render<AppSettingsPage>();

        cut.Find("[data-testid=retention-settings-section]").Should().NotBeNull();
        cut.FindAll("[data-testid=host-connections-settings-section]").Should().BeEmpty();
        cut.FindAll("[data-testid=operator-password-settings-section]").Should().BeEmpty();
    }

    [Fact]
    public void A_mutating_panel_makes_the_page_re_read_every_section_rather_than_patching_its_own()
    {
        var service = Arrange(Retention());

        var cut = Render<AppSettingsPage>();
        service.ReadCalls.Should().Be(1);

        cut.Find("[data-testid=retention-sweep-review]").Click();
        cut.Find("[data-testid=retention-sweep-confirm]").Click();

        service.SweepCalls.Should().Be(1);
        service.ReadCalls.Should().Be(2, "a panel raises OnChanged; it never mutates the page's own state");
    }

    private FakeSettingsDataService Arrange(params SettingsSection[] sections)
    {
        var service = new FakeSettingsDataService().With(sections);
        Services.AddSingleton<ISettingsDataService>(service);
        return service;
    }

    private static RetentionSettingsSection Retention() =>
        new(Available: true, Enabled: true, TimeSpan.FromDays(30), TimeSpan.FromHours(1),
            ChangePlanRetentionOptions.SectionKey);

    private static OperatorCredentialSettingsSection Credential() =>
        new(Available: true, AuthenticationEnabled: true, PasswordSet: true, 12, AuthenticationGate.ConfigurationKey);

    private sealed record UnknownSection() : SettingsSection("something-new", "A section from the future");
}
