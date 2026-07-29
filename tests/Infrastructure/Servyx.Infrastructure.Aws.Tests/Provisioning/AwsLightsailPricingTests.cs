using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The price snapshot: what it claims, and — the headline of this adapter's report — the one place it claims
/// more than the other three cloud pricing sources in this codebase do.
/// </summary>
public class AwsLightsailPricingTests
{
    [Theory]
    [InlineData("nano_3_0", 5)]
    [InlineData("medium_3_0", 24)]
    [InlineData("2xlarge_3_0", 164)]
    public void A_known_bundle_answers_with_a_list_price(string bundleId, double monthly)
    {
        var estimate = AwsLightsailPricing.For(bundleId);

        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
        estimate.Monthly.Should().Be((decimal)monthly);
        estimate.Currency.Should().Be("USD");
    }

    [Fact]
    public void The_hourly_figure_is_derived_from_the_monthly_one_the_opposite_direction_from_ec2s_table()
    {
        var estimate = AwsLightsailPricing.For("medium_3_0");

        estimate.Hourly.Should().Be(decimal.Round(24m / AwsLightsailPricing.HoursPerMonth, 4, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData("gpu_pro_3_0")]
    [InlineData("nano_1_0")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_bundle_answers_unknown_and_never_a_fabricated_number(string? bundleId)
    {
        var estimate = AwsLightsailPricing.For(bundleId);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
    }

    [Fact]
    public void An_unknown_bundle_names_itself_so_a_user_can_go_and_look_it_up() =>
        AwsLightsailPricing.For("gpu_pro_3_0").Source.Should().Contain("gpu_pro_3_0");

    [Fact]
    public void Nothing_is_ever_reported_as_a_derived_estimate_because_nothing_here_derives_anything() =>
        AwsLightsailPricing.KnownBundleIds
            .Select(AwsLightsailPricing.For)
            .Should().OnlyContain(e => e.Confidence == CostConfidence.ListPrice);

    [Fact]
    public void Nothing_is_ever_reported_as_exact_because_this_adapter_never_reads_the_accounts_bill() =>
        AwsLightsailPricing.KnownBundleIds
            .Select(AwsLightsailPricing.For)
            .Should().NotContain(e => e.Confidence == CostConfidence.Exact);

    [Fact]
    public void Every_figure_carries_its_provenance_and_its_snapshot_date()
    {
        var source = AwsLightsailPricing.For("medium_3_0").Source;

        source.Should().Contain(AwsLightsailPricing.SnapshotDate);
        source.Should().Contain("aws.amazon.com/lightsail/pricing");
        source.Should().Contain("not refreshed at runtime");
    }

    [Fact]
    public void Every_figure_says_out_loud_that_it_is_all_in_unlike_the_other_three_adapters_figures()
    {
        var source = AwsLightsailPricing.For("medium_3_0").Source;

        // The whole point the report was asked to surface: this is the one adapter in the codebase whose cost
        // figure genuinely bundles compute, storage and a data allowance, and the caveat has to say so in the
        // same string a caller might display next to the number.
        source.Should().Contain("ALL-IN");
        source.Should().Contain("boot disk");
        source.Should().Contain("public IPv4 address");
        source.Should().Contain("data-transfer allowance");
    }

    [Fact]
    public void The_ec2_source_says_compute_only_and_the_lightsail_source_says_the_opposite()
    {
        // A direct, side-by-side pin of the finding: the two AWS pricing sources in this codebase disagree
        // about what they cover, and both say so in their own words rather than leaving a caller to infer it.
        AwsEc2Pricing.Source.Should().Contain("COMPUTE ONLY");
        AwsLightsailPricing.Source.Should().Contain("ALL-IN");
        AwsLightsailPricing.Source.Should().NotContain("COMPUTE ONLY");
    }
}
