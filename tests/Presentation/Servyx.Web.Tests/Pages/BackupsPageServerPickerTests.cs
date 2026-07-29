using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Backups;
using Servyx.Domain.Backups;
using Servyx.Web.Components.Pages.Backups;
using Servyx.Web.Services;
using Servyx.Web.Tests.Fakes;

namespace Servyx.Web.Tests.Pages;

/// <summary>
/// bUnit tests for the Backups page's server picker — the surface that decides which machine a create,
/// restore or prune is aimed at.
/// </summary>
/// <remarks>
/// <para>
/// The claims these are built around: an SSH-hosted server is selectable and unmistakably labelled as one;
/// selecting it reaches the SSH provider and selecting a Docker one reaches Docker's; and where the two
/// sources claim the same name, both stay on screen and the clash is stated rather than resolved.
/// </para>
/// <para>
/// The routing tests drive the <em>real</em> <see cref="ServyxBackupProviderRouter"/> behind the real
/// <see cref="BackupDashboardService"/>, with only the two providers scripted. Asserting against a stand-in
/// for the router would prove the page calls something; this proves the selection reaches the machine the
/// operator picked.
/// </para>
/// </remarks>
public class BackupsPageServerPickerTests : BunitContext
{
    private const string DockerId = "palygondwanaland";
    private const string DockerName = "Palygondwanaland";
    private const string SshKey = "valheim-host";
    private const string SshEndpoint = "steam@10.0.0.4:22";

    /// <summary>An SSH server read through the real configuration path, gate open.</summary>
    private static SshBackupWiringOptions Ssh(string serverKey = SshKey, bool gateOpen = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("Servyx:Provisioning:Enabled", "true"),
                new KeyValuePair<string, string?>($"Servyx:Servers:{serverKey}:Ssh:Enabled", "true"),
                new KeyValuePair<string, string?>($"Servyx:Servers:{serverKey}:Ssh:Host", SshEndpoint),
                new KeyValuePair<string, string?>($"Servyx:Servers:{serverKey}:Ssh:Root", "/srv/valheim"),
            ])
            .Build();

        return SshBackupWiringOptions.FromConfiguration(configuration, new ProvisioningGate(gateOpen));
    }

    /// <summary>Registers the page's dependencies. <paramref name="ssh"/> null means "nothing SSH-hosted".</summary>
    private void Arrange(
        IDashboardDataService data,
        IBackupDashboard dashboard,
        SshBackupWiringOptions? ssh = null,
        bool gateOpen = true)
    {
        Services.AddSingleton(data);
        Services.AddSingleton(new ProvisioningGate(gateOpen));
        Services.AddSingleton(WritableServers.None);
        Services.AddSingleton(dashboard);

        if (ssh is not null)
        {
            Services.AddSingleton(ssh);
        }
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Options(IRenderedComponent<BackupsPage> cut) =>
        [.. cut.Find("[data-testid=server-select]").QuerySelectorAll("option")];

    // ── Docker's list is untouched when nothing is SSH-hosted ─────────────────────────────────────

    [Fact]
    public void With_no_ssh_server_configured_the_picker_is_docker_s_list_unchanged()
    {
        var data = new StubDashboardDataService(
            StubDashboardDataService.Server("id-a", "alpha"),
            StubDashboardDataService.Server("id-b", "bravo"),
            StubDashboardDataService.Server("id-c", "charlie"));

        Arrange(data, new FakeBackupDashboard());

        var cut = Render<BackupsPage>();
        var options = Options(cut);

        // Same entries, same order, same ids.
        options.Select(o => o.GetAttribute("value")).Should().Equal("id-a", "id-b", "id-c");

        // Same text: a bare name, with no kind label bolted on. The label exists to tell two sources apart,
        // and with one source there is nothing to tell apart.
        options.Select(o => o.TextContent).Should().Equal("alpha", "bravo", "charlie");

        options.Should().AllSatisfy(o => o.HasAttribute("data-collides").Should().BeFalse());
        cut.FindAll("[data-testid=server-name-collision]").Should().BeEmpty();
        cut.Markup.Should().NotContain("SSH-hosted");
    }

    [Fact]
    public void An_explicitly_empty_ssh_configuration_is_identical_to_none_at_all()
    {
        var data = new StubDashboardDataService(
            StubDashboardDataService.Server("id-a", "alpha"),
            StubDashboardDataService.Server("id-b", "bravo"));

        // SshBackupWiringOptions.None is what a Docker-only host registers; it must be indistinguishable
        // from the options object being absent entirely.
        Arrange(data, new FakeBackupDashboard(), SshBackupWiringOptions.None);

        var cut = Render<BackupsPage>();

        Options(cut).Select(o => o.GetAttribute("value")).Should().Equal("id-a", "id-b");
        Options(cut).Select(o => o.TextContent).Should().Equal("alpha", "bravo");
        cut.FindAll("[data-testid=server-name-collision]").Should().BeEmpty();
        cut.Markup.Should().NotContain("SSH-hosted");
    }

    // ── An SSH server is selectable and labelled ──────────────────────────────────────────────────

    [Fact]
    public void A_configured_ssh_server_appears_in_the_picker_labelled_as_ssh_hosted()
    {
        var data = new StubDashboardDataService(StubDashboardDataService.Server(DockerId, DockerName));

        Arrange(data, new FakeBackupDashboard(), Ssh());

        var cut = Render<BackupsPage>();
        var options = Options(cut);

        // Docker first, in discovery order; SSH appended. Neither displaced the other.
        options.Select(o => o.GetAttribute("value")).Should().Equal(DockerId, SshKey);
        options.Select(o => o.GetAttribute("data-kind")).Should().Equal("docker", "ssh");

        // Both kinds are stated, so neither is mistaken for the other, and the SSH entry names the machine
        // it reaches.
        options[0].TextContent.Should().Contain(DockerName).And.Contain("Docker-discovered");
        options[1].TextContent.Should().Contain(SshKey).And.Contain("SSH-hosted").And.Contain(SshEndpoint);

        // Nothing here is a clash — the two names differ.
        cut.FindAll("[data-testid=server-name-collision]").Should().BeEmpty();
        options.Should().AllSatisfy(o => o.HasAttribute("data-collides").Should().BeFalse());
    }

    [Fact]
    public void An_ssh_only_host_still_offers_and_labels_its_server()
    {
        Arrange(new StubDashboardDataService(), new FakeBackupDashboard(), Ssh());

        var options = Options(Render<BackupsPage>());

        options.Should().ContainSingle();
        options[0].GetAttribute("value").Should().Be(SshKey);
        options[0].GetAttribute("data-kind").Should().Be("ssh");
        options[0].TextContent.Should().Contain("SSH-hosted");
    }

    // ── Selection reaches the right provider ─────────────────────────────────────────────────────

    /// <summary>
    /// Composes the real router over two scripted providers, each holding one uniquely-named archive, so
    /// which provider answered is readable straight off the rendered listing.
    /// </summary>
    private static (IBackupDashboard Dashboard, ScriptedBackupProvider Docker, ScriptedBackupProvider Ssh) Routed()
    {
        var docker = new ScriptedBackupProvider().With("docker-archive", BackupOwnership.Servyx);
        var ssh = new ScriptedBackupProvider().With("ssh-archive", BackupOwnership.Servyx);

        return (new BackupDashboardService(new ServyxBackupProviderRouter(docker, ssh, [SshKey])), docker, ssh);
    }

    [Fact]
    public void Selecting_a_docker_server_routes_to_the_docker_provider()
    {
        var (dashboard, _, _) = Routed();
        Arrange(new StubDashboardDataService(StubDashboardDataService.Server(DockerId, DockerName)), dashboard, Ssh());

        // The page selects the first entry on load, and Docker's is first.
        var cut = Render<BackupsPage>();

        cut.Find("[data-testid=backup-list-section]").TextContent.Should().Contain("docker-archive");
        cut.Find("[data-testid=backup-list-section]").TextContent.Should().NotContain("ssh-archive");
    }

    [Fact]
    public void Selecting_an_ssh_server_routes_to_the_ssh_provider()
    {
        var (dashboard, _, _) = Routed();
        Arrange(new StubDashboardDataService(StubDashboardDataService.Server(DockerId, DockerName)), dashboard, Ssh());

        var cut = Render<BackupsPage>();
        cut.Find("[data-testid=server-select]").Change(SshKey);

        // The selection travelled: page → BackupDashboardService → ServyxBackupProviderRouter → SSH provider.
        cut.Find("[data-testid=backup-list-section]").TextContent.Should().Contain("ssh-archive");
        cut.Find("[data-testid=backup-list-section]").TextContent.Should().NotContain("docker-archive");

        // And back again, so the routing is per-selection rather than a one-way switch.
        cut.Find("[data-testid=server-select]").Change(DockerId);
        cut.Find("[data-testid=backup-list-section]").TextContent.Should().Contain("docker-archive");
    }

    // ── A shared name is surfaced, not resolved ──────────────────────────────────────────────────

    [Fact]
    public void A_shared_id_is_surfaced_with_both_entries_kept_and_the_shadowing_named()
    {
        // The SSH key matches the Docker server's id (case-insensitively, as the router compares them), so
        // routing genuinely sends this id to the SSH provider.
        var data = new StubDashboardDataService(StubDashboardDataService.Server(SshKey, "Valheim"));

        Arrange(data, new FakeBackupDashboard(), Ssh());

        var cut = Render<BackupsPage>();
        var options = Options(cut);

        // Neither entry was dropped, renamed, or merged.
        options.Should().HaveCount(2);
        options.Select(o => o.GetAttribute("data-kind")).Should().Equal("docker", "ssh");
        options.Should().AllSatisfy(o => o.GetAttribute("data-collides").Should().Be("true"));

        // And the clash is stated in as many words, at alert severity.
        var banner = cut.Find("[data-testid=server-name-collision]");
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain("Both are listed below");

        var entry = cut.Find("[data-testid=server-name-collision-entry]");
        entry.GetAttribute("data-collision-shadows").Should().Be("true");
        entry.TextContent.Should().Contain(SshKey);
        entry.TextContent.Should().Contain("routed to the SSH provider");
    }

    [Fact]
    public void A_shared_name_that_does_not_shadow_routing_says_so_rather_than_overstating_it()
    {
        // Same displayed name, different ids: the two route correctly, and the hazard is purely that an
        // operator cannot tell the rows apart. The page must not claim a shadowing that is not happening.
        var data = new StubDashboardDataService(StubDashboardDataService.Server("container-9f3a", SshKey));

        Arrange(data, new FakeBackupDashboard(), Ssh());

        var cut = Render<BackupsPage>();
        var options = Options(cut);

        options.Select(o => o.GetAttribute("value")).Should().Equal("container-9f3a", SshKey);
        options.Should().AllSatisfy(o => o.GetAttribute("data-collides").Should().Be("true"));

        var entry = cut.Find("[data-testid=server-name-collision-entry]");
        entry.GetAttribute("data-collision-shadows").Should().Be("false");
        entry.TextContent.Should().Contain("container-9f3a");
        entry.TextContent.Should().NotContain("cannot be acted on");

        // Both remain independently selectable, and each still reaches its own id.
        cut.Find("[data-testid=server-select]").Change(SshKey);
        cut.FindAll("[data-testid=backup-list-section]").Should().ContainSingle();
    }

    // ── The gate ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void With_the_flag_off_no_picker_and_no_ssh_entry_exist_at_all()
    {
        var data = new StubDashboardDataService(StubDashboardDataService.Server(DockerId, DockerName));

        // Registered anyway, to prove the closed-gate branch does not consult it. In the real host it would
        // be None: SshBackupWiringOptions.FromConfiguration returns None whenever the gate is closed.
        Arrange(data, new FakeBackupDashboard(), Ssh(), gateOpen: false);

        var cut = Render<BackupsPage>();

        cut.FindAll("select").Should().BeEmpty();
        cut.FindAll("[data-testid=server-select]").Should().BeEmpty();
        cut.FindAll("[data-testid=server-name-collision]").Should().BeEmpty();
        cut.Markup.Should().NotContain("SSH-hosted");
        cut.Markup.Should().NotContain(SshKey);
        cut.Markup.Should().NotContain(SshEndpoint);

        // Still the unchanged read-only view.
        cut.Markup.Should().Contain("discovered read-only");
    }

    [Fact]
    public void A_closed_gate_yields_no_configured_ssh_servers_in_the_first_place()
    {
        Ssh(gateOpen: false).Any.Should().BeFalse();
        Ssh(gateOpen: false).Servers.Should().BeEmpty();
    }
}
