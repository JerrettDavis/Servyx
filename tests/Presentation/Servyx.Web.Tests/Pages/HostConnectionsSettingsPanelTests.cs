using Bunit;
using Servyx.Web.Components.Pages.AppSettings;
using Servyx.Web.Models;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for the host connections summary on <c>/settings</c>.
/// </summary>
/// <remarks>
/// It is a summary, not a second Hosts page: it offers no control of any kind and links to <c>/hosts</c>,
/// which stays the only place a host is registered or deregistered. The one claim worth pinning beyond that
/// is the honesty <c>RegisteredHostsResult</c> already draws — "could not read the host records" must never
/// render as "no hosts registered", because the obvious next action for the second is to register again.
/// </remarks>
public class HostConnectionsSettingsPanelTests : BunitContext
{
    [Fact]
    public void An_uncomposed_host_service_is_reported_rather_than_the_panel_vanishing()
    {
        var cut = RenderPanel(HostConnectionsSettingsSection.Unavailable);

        cut.Find("[data-testid=host-connections-unavailable]").Should().NotBeNull();
        cut.FindAll("[data-testid=host-connections-count]").Should().BeEmpty();
    }

    [Fact]
    public void Registered_hosts_are_summarised_as_a_count_and_how_many_are_enabled()
    {
        var cut = RenderPanel(new HostConnectionsSettingsSection(
            Available: true, RegisteredCount: 3, EnabledCount: 2, ListingFailed: false, FailureDetail: null));

        cut.Find("[data-testid=host-connections-count]").TextContent.Should().Contain("3");
        cut.Find("[data-testid=host-connections-enabled-count]").TextContent.Should().Contain("2");
        cut.FindAll("[data-testid=host-connections-empty-state]").Should().BeEmpty();
    }

    [Fact]
    public void No_registered_hosts_renders_an_empty_state_distinct_from_a_failed_read()
    {
        var cut = RenderPanel(new HostConnectionsSettingsSection(
            Available: true, RegisteredCount: 0, EnabledCount: 0, ListingFailed: false, FailureDetail: null));

        cut.Find("[data-testid=host-connections-empty-state]").Should().NotBeNull();
        cut.FindAll("[data-testid=host-connections-degraded]").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_read_is_rendered_as_degraded_and_never_as_zero_hosts()
    {
        var cut = RenderPanel(new HostConnectionsSettingsSection(
            Available: true, RegisteredCount: 0, EnabledCount: 0, ListingFailed: true, "the database is locked"));

        cut.FindAll("[data-testid=host-connections-empty-state]").Should().BeEmpty();
        var detail = cut.Find("[data-testid=host-connections-degraded-detail]").TextContent;
        detail.Should().Contain("may not be accurate");
        detail.Should().Contain("the database is locked");
    }

    [Fact]
    public void The_panel_offers_no_control_of_its_own_and_points_at_the_hosts_page()
    {
        var cut = RenderPanel(new HostConnectionsSettingsSection(
            Available: true, RegisteredCount: 1, EnabledCount: 1, ListingFailed: false, FailureDetail: null));

        cut.FindAll("button").Should().BeEmpty("registering and deregistering stay exclusively on /hosts");
        cut.FindAll("input").Should().BeEmpty();
        cut.Find("[data-testid=host-connections-link]").GetAttribute("href").Should().Be("/hosts");
    }

    private IRenderedComponent<HostConnectionsSettingsPanel> RenderPanel(HostConnectionsSettingsSection section) =>
        Render<HostConnectionsSettingsPanel>(parameters => parameters.Add(p => p.Section, section));
}
