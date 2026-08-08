using Servyx.Domain.Lifecycle;
using Servyx.Web.Models;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Services;

/// <summary>
/// <see cref="MockDashboardDataService"/> backs every user-guide screenshot, so its aggregate
/// <c>DashboardSummary</c> must be internally consistent with the server list <see cref="MockDashboardDataService.GetServersAsync"/>
/// actually returns — a screenshot that contradicts another screenshot in the same guide undermines both.
/// These tests pin the aggregation to the seeded two-server list (both <c>Running</c>, both <c>Unhealthy</c>)
/// so a future edit to the seed data cannot silently drift the summary tiles out of sync again, the same
/// class of bug this file was added to close (see <c>ServersOnline</c>/<c>ServersTotal</c>, <c>AlertsCount</c>
/// and <c>TotalPlayers</c>/<c>TotalPlayerCapacity</c> below).
/// </summary>
public class MockDashboardDataServiceTests
{
    private readonly MockDashboardDataService _sut = new();

    [Fact]
    public async Task Dashboard_summary_reports_both_seeded_servers_as_online()
    {
        var servers = await _sut.GetServersAsync();
        var summary = await _sut.GetDashboardSummaryAsync();

        summary.ServersTotal.Should().Be(servers.Count);
        summary.ServersOnline.Should().Be(servers.Count(s => s.State == ServerState.Running));
    }

    [Fact]
    public async Task Dashboard_summary_derives_alerts_count_from_the_unhealthy_server_count()
    {
        // Mirrors LiveDashboardDataService.GetDashboardSummaryAsync: servers.Count(s => s.Health == ContainerHealth.Unhealthy).
        var servers = await _sut.GetServersAsync();
        var summary = await _sut.GetDashboardSummaryAsync();

        var expectedUnhealthy = servers.Count(s => s.Health == ContainerHealth.Unhealthy);
        expectedUnhealthy.Should().Be(2, "both seeded demo servers are deliberately Unhealthy");
        summary.AlertsCount.Should().Be(expectedUnhealthy);
    }

    [Fact]
    public async Task Dashboard_summary_sums_players_across_every_seeded_server_not_just_the_first()
    {
        var servers = await _sut.GetServersAsync();
        var summary = await _sut.GetDashboardSummaryAsync();

        var expectedPlayers = servers.Sum(s => s.PlayersOnline ?? 0);
        var expectedCapacity = servers.Sum(s => s.PlayersMax ?? 0);

        summary.TotalPlayers.Should().Be(expectedPlayers);
        summary.TotalPlayerCapacity.Should().Be(expectedCapacity);

        // Locks in the aggregate, not just "matches whatever the sum is": a regression that reverts to
        // reading only the first server's PlayersOnline/PlayersMax would still sum correctly against a
        // one-server list, so the test must also know the two-server total is not just the first server's.
        summary.TotalPlayers.Should().NotBe(servers[0].PlayersOnline);
        summary.TotalPlayerCapacity.Should().NotBe(servers[0].PlayersMax);
    }

    [Fact]
    public void SumIfAnyKnown_reports_null_when_every_value_is_unsampled()
    {
        // Every server unsampled must aggregate to "unknown", never a fabricated 0 — the same convention
        // LiveDashboardDataService.GetDashboardSummaryAsync pins for its own all-null TotalPlayers/
        // TotalPlayerCapacity.
        int?[] values = [null, null, null];

        MockDashboardDataService.SumIfAnyKnown(values).Should().BeNull();
    }

    [Fact]
    public void SumIfAnyKnown_sums_only_the_known_values_when_some_servers_are_unsampled()
    {
        // A mix of sampled and unsampled servers must sum the known ones rather than either conflating the
        // unsampled server's "unknown" into a fabricated 0 (old `?? 0` behaviour) or losing the aggregate
        // entirely just because one server hasn't been sampled.
        int?[] values = [3, null, 7];

        MockDashboardDataService.SumIfAnyKnown(values).Should().Be(10);
    }

    [Fact]
    public void SumIfAnyKnown_sums_normally_when_every_value_is_known()
    {
        int?[] values = [3, 7];

        MockDashboardDataService.SumIfAnyKnown(values).Should().Be(10);
    }
}
