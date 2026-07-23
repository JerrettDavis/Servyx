using Bunit;
using FluentAssertions;
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

namespace Servyx.Web.Tests.Services;

/// <summary>
/// Docker being unreachable is a normal condition for a self-hosted panel, not a bug. These tests drive
/// the real <see cref="ServerQueryService"/> and <see cref="LiveDashboardDataService"/> with a
/// substituted <see cref="ITransport"/>/<see cref="IServerDiscovery"/> that fail exactly as a downed
/// daemon would, and assert the dashboard degrades to an honest state instead of throwing.
/// </summary>
public class LiveDashboardDataServiceDegradedPathTests : BunitContext
{
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
            AdoptionCriteria.PalworldDefault);

        return new LiveDashboardDataService(queryService, NullLogger<LiveDashboardDataService>.Instance);
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
}
