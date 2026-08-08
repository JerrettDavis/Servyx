using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// Tests the server-detail Backups tab's one gate-aware behaviour: it points at the managed surface when
/// backups are actionable, and is untouched when they are not.
/// </summary>
/// <remarks>
/// The tab deliberately grows no controls of its own on either side of the gate. Creating, restoring, and
/// pruning live on <c>/backups</c>, so there is exactly one implementation of "preview, then confirm" to
/// review.
/// </remarks>
public class ServerBackupsTabGateTests : BunitContext
{
    private static IReadOnlyList<BackupEntry> SampleBackups()
        => new MockDashboardDataService().GetServerBackupsAsync("palygondwanaland").GetAwaiter().GetResult();

    [Fact]
    public void With_the_gate_closed_the_tab_offers_no_control_and_no_link()
    {
        Services.AddSingleton(ProvisioningGate.Closed);

        var cut = Render<ServerBackupsTab>(p => p.Add(x => x.Backups, SampleBackups()));

        cut.FindAll("[data-testid=backups-tab-managed-link]").Should().BeEmpty();
        cut.FindAll("a").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
    }

    [Fact]
    public void With_the_gate_open_the_tab_links_to_the_managed_surface_and_still_offers_no_control()
    {
        Services.AddSingleton(new ProvisioningGate(enabled: true));

        var cut = Render<ServerBackupsTab>(p => p.Add(x => x.Backups, SampleBackups()));

        cut.Find("[data-testid=backups-tab-managed-link] a").GetAttribute("href").Should().Be("/backups");

        // Still no second copy of the destructive flow here: the link is the whole of it.
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
    }

    [Fact]
    public void An_unregistered_gate_renders_exactly_what_a_closed_one_does()
    {
        // Separate contexts because bUnit's service collection is sealed by the first render, and the
        // whole point here is to compare a container that has a gate against one that never had one.
        using var ungatedContext = new BunitContext();
        var ungated = ungatedContext.Render<ServerBackupsTab>(p => p.Add(x => x.Backups, SampleBackups())).Markup;

        using var closedContext = new BunitContext();
        closedContext.Services.AddSingleton(ProvisioningGate.Closed);
        var closed = closedContext.Render<ServerBackupsTab>(p => p.Add(x => x.Backups, SampleBackups())).Markup;

        ungated.Should().Be(closed);
    }
}
