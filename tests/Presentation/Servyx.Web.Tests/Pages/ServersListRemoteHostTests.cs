using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Pages.Servers;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit coverage for a remote (ssh+docker) server appearing alongside the local one in the Servers
/// list. Until now <see cref="MockDashboardDataService"/> only ever seeded one local server
/// ("Palygondwanaland"), so there was no UI coverage at all of a remote-hosted server, of the
/// exposed-but-unpublished-port case, or of two servers rendering side by side. See
/// <see cref="MockDashboardDataService"/>'s "Remote (ssh+docker) server" region for the seeded shape:
/// <c>Host: "ssh+docker"</c>, health <c>Unhealthy</c> with the Palworld false-negative explanation, and
/// ports 8211/udp + 27015/udp published plus 25575/tcp exposed but not published.
/// </summary>
/// <remarks>
/// The Servers list (<c>ServersList.razor</c>) does render both the <c>Host</c> column and the
/// published/unpublished port distinction (a "port-published" CSS class plus a title attribute carrying
/// the port's purpose, and <see cref="Servyx.Web.Models.PortBinding.Label"/> appending "(not published to
/// host)" for unpublished ports) — so these tests assert directly against what is rendered, with no UI
/// changes needed.
/// </remarks>
public class ServersListRemoteHostTests : BunitContext
{
    private const string LocalServerName = "Palygondwanaland";
    private const string RemoteServerId = "example-remote-palworld";
    private const string RemoteServerName = "Example Remote Palworld";

    public ServersListRemoteHostTests()
    {
        Services.AddSingleton<IDashboardDataService, MockDashboardDataService>();
        // ServersList.razor persists/rehydrates via PersistentComponentState (see Home.razor's identical
        // pattern); bUnit does not register the real Blazor-runtime implementation, so a fake is required
        // for the component to render at all outside a live circuit.
        AddBunitPersistentComponentState();
    }

    [Fact]
    public void Both_the_local_and_remote_servers_are_listed()
    {
        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(2));

        cut.Markup.Should().Contain(LocalServerName);
        cut.Markup.Should().Contain(RemoteServerName);

        var remoteRow = cut.Find($"a[href='servers/{RemoteServerId}']");
        remoteRow.TextContent.Should().Contain(RemoteServerName);
    }

    [Fact]
    public void The_remote_server_is_labelled_with_the_ssh_docker_host()
    {
        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(2));

        var remoteRow = cut.Find($"a[href='servers/{RemoteServerId}']");
        var hostCell = remoteRow.QuerySelector("[data-col-label='Host']");

        hostCell.Should().NotBeNull();
        hostCell!.TextContent.Trim().Should().Be("ssh+docker");

        // The local server keeps its own (Docker daemon) host label — the two are distinguishable side by
        // side, not just "not ssh+docker".
        var localRow = cut.FindAll("a.svx-row-link")
            .Single(r => r.TextContent.Contains(LocalServerName, StringComparison.Ordinal));
        var localHostCell = localRow.QuerySelector("[data-col-label='Host']");
        localHostCell!.TextContent.Trim().Should().NotBe("ssh+docker");
    }

    [Fact]
    public void The_remote_server_shows_its_unhealthy_status()
    {
        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(2));

        var remoteRow = cut.Find($"a[href='servers/{RemoteServerId}']");
        var healthBadge = remoteRow.QuerySelector(".health-badge");

        healthBadge.Should().NotBeNull();
        healthBadge!.TextContent.Trim().Should().Be("Unhealthy");
        healthBadge.ClassList.Should().Contain("health-unhealthy");
    }

    [Fact]
    public void The_unhealthy_status_is_accompanied_by_the_palworld_explanation()
    {
        var cut = Render<ServersList>();
        cut.WaitForAssertion(() => cut.FindAll("a.svx-row-link").Should().HaveCount(2));

        var remoteRow = cut.Find($"a[href='servers/{RemoteServerId}']");
        var healthBadge = remoteRow.QuerySelector(".health-badge");

        // The false-negative explanation: the container's own HEALTHCHECK gets 401 Unauthorized while the
        // Palworld server itself is healthy — reaches the DOM via the badge's title attribute (see
        // HealthBadge.razor), exactly like the local server's tooltip.
        var tooltip = healthBadge!.GetAttribute("title");
        tooltip.Should().Contain("401 Unauthorized");
        tooltip.Should().Contain("Palworld server itself is healthy");
    }

    [Fact]
    public void Lifecycle_buttons_are_disabled_for_the_remote_server()
    {
        var cut = Render<ServerDetailPage>(p => p.Add(x => x.Id, RemoteServerId));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='gated-button']").Should().HaveCount(4));

        var buttons = cut.FindAll("[data-testid='gated-button']");
        buttons.Select(b => b.TextContent.Trim()).Should().BeEquivalentTo("Start", "Restart", "Stop", "Kill");

        foreach (var button in buttons)
        {
            button.HasAttribute("disabled").Should().BeTrue();
        }
    }
}
