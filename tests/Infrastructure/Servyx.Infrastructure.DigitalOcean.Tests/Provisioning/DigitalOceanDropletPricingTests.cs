using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;

namespace Servyx.Infrastructure.DigitalOcean.Tests.Provisioning;

/// <summary>
/// The cost snapshot, and — more importantly — its refusal to invent a number it does not have.
/// </summary>
public class DigitalOceanDropletPricingTests
{
    [Theory]
    [InlineData("s-1vcpu-512mb-10gb", 0.00595, 4.00)]
    [InlineData("s-1vcpu-1gb", 0.00893, 6.00)]
    [InlineData("s-1vcpu-2gb", 0.01786, 12.00)]
    [InlineData("s-2vcpu-2gb", 0.02679, 18.00)]
    [InlineData("s-2vcpu-4gb", 0.03571, 24.00)]
    [InlineData("s-4vcpu-8gb", 0.07143, 48.00)]
    [InlineData("s-8vcpu-16gb", 0.14286, 96.00)]
    public void A_known_size_is_priced_at_the_published_list_price(string slug, double hourly, double monthly)
    {
        var estimate = DigitalOceanDropletPricing.For(slug);

        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
        estimate.Hourly.Should().Be((decimal)hourly);
        estimate.Monthly.Should().Be((decimal)monthly);
        estimate.Currency.Should().Be("USD");
    }

    [Theory]
    [InlineData("g-2vcpu-8gb")]
    [InlineData("c-4")]
    [InlineData("m-2vcpu-16gb")]
    [InlineData("s-2vcpu-4gb-120gb-intel")]
    [InlineData("not-a-real-slug")]
    public void An_unknown_size_yields_unknown_and_never_a_fabricated_number(string slug)
    {
        var estimate = DigitalOceanDropletPricing.For(slug);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
        estimate.Source.Should().Contain(slug);
    }

    [Fact]
    public void An_unknown_size_is_never_approximated_from_a_similar_known_one()
    {
        // CostConfidence.Estimated exists for derived figures, and this table deliberately never produces one:
        // a premium or CPU-optimised droplet priced off a similar basic slug would be a plausible number, which
        // is worse than no number because a caller cannot tell the difference.
        DigitalOceanDropletPricing.For("s-2vcpu-4gb-intel").Confidence.Should().NotBe(CostConfidence.Estimated);
        DigitalOceanDropletPricing.For("s-2vcpu-4gb-intel").Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public void A_blank_size_yields_unknown_rather_than_throwing()
    {
        DigitalOceanDropletPricing.For(null).Confidence.Should().Be(CostConfidence.Unknown);
        DigitalOceanDropletPricing.For("  ").Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public void Every_estimate_says_where_the_number_came_from_and_when_it_was_taken()
    {
        // The snapshot date is part of the answer because the table is not refreshed at runtime: a caller
        // reading a price needs to know how old it is.
        DigitalOceanDropletPricing.For("s-2vcpu-4gb").Source.Should().Contain(DigitalOceanDropletPricing.SnapshotDate);
        DigitalOceanDropletPricing.For("s-2vcpu-4gb").Source.Should().Contain("digitalocean.com/pricing/droplets");
        DigitalOceanDropletPricing.For("nope").Source.Should().Contain(DigitalOceanDropletPricing.SnapshotDate);
    }

    [Fact]
    public void The_snapshot_covers_only_the_basic_tier_it_claims_to_cover()
    {
        DigitalOceanDropletPricing.KnownSizeSlugs.Should().OnlyContain(s => s.StartsWith("s-", StringComparison.Ordinal));
        DigitalOceanDropletPricing.KnownSizeSlugs.Should().HaveCount(7);
    }
}
