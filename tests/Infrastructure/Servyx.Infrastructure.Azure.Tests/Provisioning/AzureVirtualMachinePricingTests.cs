using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;

namespace Servyx.Infrastructure.Azure.Tests.Provisioning;

/// <summary>
/// The price snapshot: what it knows, what it refuses to guess, and what it says out loud about what it does
/// not cover.
/// </summary>
public class AzureVirtualMachinePricingTests
{
    [Theory]
    [InlineData("Standard_B1s", 0.0104)]
    [InlineData("Standard_B2s", 0.0416)]
    [InlineData("Standard_D2s_v5", 0.0960)]
    [InlineData("Standard_F2s_v2", 0.0846)]
    public void A_known_size_is_priced_at_list_price(string size, double hourly)
    {
        var estimate = AzureVirtualMachinePricing.For(size);

        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
        estimate.Hourly.Should().Be((decimal)hourly);
        estimate.Currency.Should().Be("USD");
    }

    [Fact]
    public void A_monthly_figure_is_the_hourly_rate_times_the_hours_azure_bills_in_a_month()
    {
        var estimate = AzureVirtualMachinePricing.For("Standard_B2s");

        // Azure publishes an hourly rate and no monthly cap, so the monthly figure here is derived rather than
        // transcribed - the opposite of DigitalOcean, which publishes a monthly cap and derives the hourly
        // rate. Pinned so a future edit cannot quietly change the convention.
        estimate.Monthly.Should().Be(decimal.Round(0.0416m * 730m, 2, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData("Standard_NC24ads_A100_v4")]
    [InlineData("Standard_M128ms")]
    [InlineData("s-2vcpu-4gb")]
    [InlineData("not-a-size")]
    public void An_unknown_size_answers_unknown_rather_than_approximating(string size)
    {
        var estimate = AzureVirtualMachinePricing.For(size);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();

        // The unknown answer names the size it could not price, which is what a user needs in order to go and
        // look the number up. Note the third case above: a DigitalOcean slug is simply an unknown Azure size,
        // which is the correct answer rather than an error.
        estimate.Source.Should().Contain(size);
    }

    [Fact]
    public void A_blank_size_answers_unknown()
    {
        AzureVirtualMachinePricing.For(null).Confidence.Should().Be(CostConfidence.Unknown);
        AzureVirtualMachinePricing.For(string.Empty).Confidence.Should().Be(CostConfidence.Unknown);
        AzureVirtualMachinePricing.For("   ").Confidence.Should().Be(CostConfidence.Unknown);
    }

    [Fact]
    public void No_price_is_ever_reported_as_exact_or_as_estimated()
    {
        // ListPrice and nothing stronger: this adapter does not read the subscription's billing API, so it
        // cannot know what the account is actually charged after an enterprise agreement, a reservation or a
        // savings plan. And nothing weaker: CostConfidence.Estimated exists for derived figures, and nothing in
        // the snapshot derives a price for a size it does not carry.
        foreach (var size in AzureVirtualMachinePricing.KnownSizes)
        {
            AzureVirtualMachinePricing.For(size).Confidence.Should().Be(CostConfidence.ListPrice);
        }

        AzureVirtualMachinePricing.For("Standard_NC24ads_A100_v4").Confidence.Should().NotBe(CostConfidence.Estimated);
    }

    [Fact]
    public void Every_figure_carries_the_snapshot_date_and_the_compute_only_caveat()
    {
        var known = AzureVirtualMachinePricing.For("Standard_B2s");
        var unknown = AzureVirtualMachinePricing.For("Standard_M128ms");

        foreach (var estimate in new[] { known, unknown })
        {
            estimate.Source.Should().Contain(AzureVirtualMachinePricing.SnapshotDate);
            estimate.Source.Should().Contain(AzureVirtualMachinePricing.PricedRegion);
            estimate.Source.Should().Contain("not refreshed at runtime");

            // The caveat that distinguishes this table from the DigitalOcean one, pinned so it cannot be
            // dropped by a future edit: a droplet price is the whole machine, this one is the compute meter
            // only, and this adapter creates a separately-billed managed disk and static address on every host.
            estimate.Source.Should().Contain("COMPUTE ONLY");
        }
    }

    [Fact]
    public void The_snapshot_covers_the_tiers_a_game_server_is_normally_sized_from()
    {
        AzureVirtualMachinePricing.KnownSizes.Should().Contain(["Standard_B1s", "Standard_B2s", "Standard_D2s_v5"]);
        AzureVirtualMachinePricing.KnownSizes.Should().OnlyContain(s => s.StartsWith("Standard_", StringComparison.Ordinal));
    }
}
