using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The price snapshot: what it claims, and — more importantly — what it refuses to claim.
/// </summary>
public class AwsEc2PricingTests
{
    [Theory]
    [InlineData("t3.micro", 0.0104)]
    [InlineData("t3.medium", 0.0416)]
    [InlineData("m5.large", 0.0960)]
    [InlineData("c5.large", 0.0850)]
    public void A_known_instance_type_answers_with_a_list_price(string instanceType, double hourly)
    {
        var estimate = AwsEc2Pricing.For(instanceType);

        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
        estimate.Hourly.Should().Be((decimal)hourly);
        estimate.Currency.Should().Be("USD");
    }

    [Fact]
    public void The_monthly_figure_is_the_hourly_one_over_the_730_hour_month_aws_publishes_against()
    {
        var estimate = AwsEc2Pricing.For("t3.medium");

        estimate.Monthly.Should().Be(decimal.Round(0.0416m * AwsEc2Pricing.HoursPerMonth, 2, MidpointRounding.AwayFromZero));
        estimate.Monthly.Should().Be(30.37m);
    }

    [Theory]
    [InlineData("p5.48xlarge")]
    [InlineData("mac2.metal")]
    [InlineData("t3.gigantic")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_instance_type_answers_unknown_and_never_a_fabricated_number(string? instanceType)
    {
        var estimate = AwsEc2Pricing.For(instanceType);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
    }

    [Fact]
    public void An_unknown_instance_type_names_itself_so_a_user_can_go_and_look_it_up() =>
        AwsEc2Pricing.For("p5.48xlarge").Source.Should().Contain("p5.48xlarge");

    [Fact]
    public void Nothing_is_ever_reported_as_a_derived_estimate_because_nothing_here_derives_anything() =>
        AwsEc2Pricing.KnownInstanceTypes
            .Select(AwsEc2Pricing.For)
            .Should().OnlyContain(e => e.Confidence == CostConfidence.ListPrice);

    [Fact]
    public void Nothing_is_ever_reported_as_exact_because_this_adapter_never_reads_the_accounts_bill() =>
        AwsEc2Pricing.KnownInstanceTypes
            .Select(AwsEc2Pricing.For)
            .Should().NotContain(e => e.Confidence == CostConfidence.Exact);

    [Fact]
    public void Every_figure_carries_its_provenance_its_snapshot_date_and_its_region()
    {
        var source = AwsEc2Pricing.For("t3.medium").Source;

        source.Should().Contain(AwsEc2Pricing.SnapshotDate);
        source.Should().Contain(AwsEc2Pricing.PricedRegion);
        source.Should().Contain("aws.amazon.com/ec2/pricing/on-demand");
        source.Should().Contain("not refreshed at runtime");
    }

    [Fact]
    public void Every_figure_says_out_loud_that_it_is_compute_only()
    {
        var source = AwsEc2Pricing.For("t3.medium").Source;

        // The caveat has to travel with the number onto whatever screen displays it, not live only in a doc
        // comment: a caller comparing this against an all-in DigitalOcean droplet price is comparing a partial
        // figure with a complete one, and the two most commonly missed lines (the EBS root volume and the
        // public IPv4 charge) are both excluded.
        source.Should().Contain("COMPUTE ONLY");
        source.Should().Contain("EBS root volume");
        source.Should().Contain("public IPv4 address");
        source.Should().Contain("not directly comparable");
    }
}
