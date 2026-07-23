using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Web.Components.Pages;
using Servyx.Web.Services;

namespace Servyx.Web.Tests.Pages;

public class DashboardTests : BunitContext
{
    public DashboardTests()
    {
        Services.AddSingleton<IDashboardDataService, MockDashboardDataService>();
    }

    [Fact]
    public void ShowsHealthAsABadge_DistinctFromTheStateBadge()
    {
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".state-badge").Should().NotBeEmpty());

        var stateBadge = cut.Find(".state-badge");
        var healthBadge = cut.Find(".health-badge");

        // Two separate elements, not one badge doing double duty.
        stateBadge.Should().NotBeSameAs(healthBadge);

        stateBadge.TextContent.Trim().Should().Be("Running");
        healthBadge.TextContent.Trim().Should().Be("Unhealthy");
        healthBadge.ClassList.Should().Contain("health-unhealthy");
        healthBadge.GetAttribute("title").Should().Contain("401 Unauthorized");
    }

    [Fact]
    public void ShowsSummaryStatCards()
    {
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".stat-card").Should().HaveCount(4));
    }
}
