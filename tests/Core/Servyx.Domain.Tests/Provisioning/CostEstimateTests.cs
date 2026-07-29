using Servyx.Domain.Provisioning;

namespace Servyx.Domain.Tests.Provisioning;

public class CostEstimateTests
{
    [Fact]
    public void Unknown_HasNullAmountsAndUnknownConfidence()
    {
        var estimate = CostEstimate.Unknown("no pricing API available");

        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Source.Should().Be("no pricing API available");
    }

    [Fact]
    public void Unknown_IsDistinctFromZeroCost()
    {
        var unknown = CostEstimate.Unknown("source");
        var zero = new CostEstimate(0m, 0m, "USD", CostConfidence.Exact, "source");

        unknown.Should().NotBe(zero);
    }
}
