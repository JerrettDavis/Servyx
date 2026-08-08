using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Servyx.Application.Servers;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;
using Servyx.Web.Components.Layout;
using Servyx.Web.Components.Pages;
using Servyx.Web.Models;
using Servyx.Web.Services;
using Servyx.Composition;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Docker being unreachable is a normal condition for a self-hosted panel, not a bug. These tests drive
/// the real <see cref="ServerQueryService"/> and <see cref="LiveDashboardDataService"/> with a
/// substituted <see cref="ITransport"/>/<see cref="IServerDiscovery"/> that fail exactly as a downed
/// daemon would, and assert the dashboard degrades to an honest state instead of throwing.
/// </summary>
public class LiveDashboardDataServiceDegradedPathTests : BunitContext
{
    public LiveDashboardDataServiceDegradedPathTests()
    {
        // MainLayout mounts ThemeToggle, which reads the stored theme choice via JS on first render.
        JSInterop.Setup<string>("servyxTheme.read").SetResult("system");
    }

    private static LiveDashboardDataService CreateUnreachableDataService()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(_ => throw new InvalidOperationException("daemon unreachable"));

        var transport = Substitute.For<ITransport>();
        transport.ProbeAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TargetHealth(false, null, "Docker engine unreachable: no such pipe")));

        var queryService = new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            transport,
            new AdoptionCriteria("palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld"),
            NullLogger<ServerQueryService>.Instance);

        return new LiveDashboardDataService(
            queryService,
            NullLogger<LiveDashboardDataService>.Instance,
            new TargetDescriptor("docker", "npipe://./pipe/docker_engine", null, null, new Dictionary<string, string>()));
    }

    /// <summary>
    /// Builds the exact "green Connected + fake-empty server list" scenario the Bug 1 report describes:
    /// the ssh+docker transport's own fresh-session probe succeeds (so the connection badge shows
    /// Connected) while discovery — running over a separate, stale cached session — throws. Before the
    /// fix, this was visually indistinguishable from a healthy remote host with zero containers.
    /// </summary>
    private static LiveDashboardDataService CreateConnectedButDiscoveryFailedDataService()
    {
        var discovery = Substitute.For<IServerDiscovery>();
        discovery.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<DiscoveredServer>>>(
                _ => throw new InvalidOperationException("docker container ls exited with status 1: permission denied"));

        var transport = Substitute.For<ITransport>();
        transport.ProbeAsync(Arg.Any<TargetDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TargetHealth(true, TimeSpan.FromMilliseconds(40), "Docker reachable over SSH. Server version 27.3.1.")));

        var queryService = new ServerQueryService(
            discovery,
            Substitute.For<IMetricsSource>(),
            Substitute.For<ILogStream>(),
            transport,
            new AdoptionCriteria("palworld", "Palworld Dedicated Server", "thijsvanloef/palworld-server-docker", "/palworld"),
            NullLogger<ServerQueryService>.Instance);

        return new LiveDashboardDataService(
            queryService,
            NullLogger<LiveDashboardDataService>.Instance,
            new TargetDescriptor("ssh+docker", "ssh://prod-host:22", null, null, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task GetServersAsync_ReturnsEmptyList_WhenTheDaemonIsUnreachable_InsteadOfThrowing()
    {
        var sut = CreateUnreachableDataService();

        var act = async () => await sut.GetServersAsync();

        (await act.Should().NotThrowAsync()).Which.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDockerConnectionStatusAsync_ReportsDisconnected_WhenTheProbeFails()
    {
        var sut = CreateUnreachableDataService();

        var status = await sut.GetDockerConnectionStatusAsync();

        status.Should().Be(ConnectionStatus.Disconnected);
    }

    [Fact]
    public void Home_RendersHonestEmptyState_InsteadOfThrowing_WhenNoServerIsAdopted()
    {
        Services.AddSingleton<IDashboardDataService>(CreateUnreachableDataService());
        // Home.razor persists/rehydrates via PersistentComponentState (fix 6); bUnit needs the fake
        // registered for the component to render at all outside a live circuit.
        AddBunitPersistentComponentState();

        var act = () => Render<Home>();

        act.Should().NotThrow();
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No servers adopted yet"));
    }

    [Fact]
    public void MainLayout_ShowsDisconnected_InsteadOfThrowing_WhenTheDaemonIsUnreachable()
    {
        Services.AddSingleton<IDashboardDataService>(CreateUnreachableDataService());

        var act = () => Render<MainLayout>();

        act.Should().NotThrow();
        var cut = Render<MainLayout>();
        cut.WaitForAssertion(() => cut.Find(".svx-connection").ClassList.Should().Contain("svx-connection-disconnected"));
    }

    /// <summary>
    /// The Bug 1 regression guard at the service layer: when the probe succeeds but discovery fails (the
    /// ssh+docker "green Connected + fake-empty list" scenario), <see cref="LiveDashboardDataService.GetServersWithStatusAsync"/>
    /// must report the failure — not silently return an empty list indistinguishable from a healthy,
    /// zero-server host.
    /// </summary>
    [Fact]
    public async Task GetServersWithStatusAsync_ReportsDiscoveryFailed_WhenProbeSucceeds_ButDiscoveryFails()
    {
        var sut = CreateConnectedButDiscoveryFailedDataService();

        var connectionStatus = await sut.GetDockerConnectionStatusAsync();
        var result = await sut.GetServersWithStatusAsync();

        connectionStatus.Should().Be(ConnectionStatus.Connected);
        result.Servers.Should().BeEmpty();
        result.DiscoveryFailed.Should().BeTrue();
        result.FailureDetail.Should().Contain("permission denied");
    }

    /// <summary>
    /// <see cref="LiveDashboardDataService.GetServersAsync"/> — the pre-existing, unwrapped list API — must
    /// still degrade to an honest empty list rather than throwing, exactly as before this fix, even for the
    /// connected-but-discovery-failed scenario.
    /// </summary>
    [Fact]
    public async Task GetServersAsync_StillReturnsEmptyList_WhenProbeSucceeds_ButDiscoveryFails()
    {
        var sut = CreateConnectedButDiscoveryFailedDataService();

        var act = async () => await sut.GetServersAsync();

        (await act.Should().NotThrowAsync()).Which.Should().BeEmpty();
    }

    /// <summary>
    /// The bUnit half of the Bug 1 regression guard: the dashboard must render *differently* for "discovery
    /// failed" than for "genuinely zero servers adopted" (see <see cref="Home_RendersHonestEmptyState_InsteadOfThrowing_WhenNoServerIsAdopted"/>
    /// for the latter) — a green "Connected" badge next to an unqualified "No servers adopted yet" is
    /// exactly the deceptive rendering this fix exists to prevent.
    /// </summary>
    [Fact]
    public void Home_RendersDiscoveryFailedWarning_InsteadOfClaimingGenuinelyEmpty_WhenConnectedButDiscoveryFails()
    {
        Services.AddSingleton<IDashboardDataService>(CreateConnectedButDiscoveryFailedDataService());
        AddBunitPersistentComponentState();

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("could not read the server list");
            cut.Find("[data-testid='servers-discovery-failed']").Should().NotBeNull();
            cut.Find(".svx-empty-state").ClassList.Should().Contain("svx-empty-state-degraded");
        });
    }

    /// <summary>
    /// The same scenario rendered through <c>MainLayout</c>: the connection badge honestly shows Connected
    /// (the ssh probe really did succeed) — it is the servers list, not the connection status, that must
    /// carry the "could not be read" signal. This guards against "fixing" Bug 1 by making the connection
    /// badge lie in the other direction.
    /// </summary>
    [Fact]
    public void MainLayout_StillShowsConnected_WhenOnlyDiscoveryFails_NotTheProbe()
    {
        Services.AddSingleton<IDashboardDataService>(CreateConnectedButDiscoveryFailedDataService());

        var cut = Render<MainLayout>();

        cut.WaitForAssertion(() => cut.Find(".svx-connection").ClassList.Should().Contain("svx-connection-connected"));
    }

    /// <summary>
    /// The Bug 2 regression guard: <c>MainLayout</c>'s connection tooltip must reflect the actual transport
    /// probed (ssh+docker, including its specific detail text) rather than the previously hardcoded
    /// "npipe transport using the desktop-linux Docker context" claim, which is false for every non-local
    /// target.
    /// </summary>
    [Fact]
    public void MainLayout_ConnectionTooltip_ReflectsTheSshDockerTransport_NotTheHardcodedNpipeText()
    {
        Services.AddSingleton<IDashboardDataService>(CreateConnectedButDiscoveryFailedDataService());

        var cut = Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var tooltip = cut.Find(".svx-connection").GetAttribute("title");
            tooltip.Should().Contain("Docker reachable over SSH");
            tooltip.Should().NotContain("npipe");
            tooltip.Should().NotContain("desktop-linux");
        });
    }
}
