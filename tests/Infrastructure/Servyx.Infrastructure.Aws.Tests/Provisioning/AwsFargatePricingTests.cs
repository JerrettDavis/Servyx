using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The Fargate price snapshot: what it computes, and — more importantly — what it says it is not covering.
/// </summary>
/// <remarks>
/// The counterpart of <c>AwsEc2PricingTests</c> and <c>AwsLightsailPricingTests</c>, over a formula rather than a
/// lookup table. The tests that matter most here are not the arithmetic ones: they are the ones pinning that the
/// figure describes itself as compute-only, because this adapter's number sits on the same screen as Lightsail's,
/// which genuinely is all-in, and a caller comparing the two without that caveat would reach the wrong answer.
/// </remarks>
public class AwsFargatePricingTests
{
    [Fact]
    public void One_vcpu_and_two_gigabytes_is_priced_from_the_two_published_rates()
    {
        // 1 vCPU * $0.04048/hr + 2 GB * $0.004445/hr = $0.04937/hr.
        var estimate = AwsFargatePricing.For(1024, 2048);

        estimate.Hourly.Should().Be(0.0494m);
        estimate.Monthly.Should().Be(36.04m);
        estimate.Currency.Should().Be("USD");
        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
    }

    [Fact]
    public void A_quarter_vcpu_task_is_priced_proportionally_rather_than_rounded_up_to_one()
    {
        // The 256-unit row is a quarter of a vCPU; pricing it as one would overstate the smallest task by 4x.
        var estimate = AwsFargatePricing.For(256, 512);

        estimate.Hourly.Should().Be(0.0123m);
        estimate.Confidence.Should().Be(CostConfidence.ListPrice);
    }

    [Fact]
    public void A_larger_reservation_costs_proportionally_more()
    {
        var small = AwsFargatePricing.For(1024, 2048);
        var large = AwsFargatePricing.For(2048, 4096);

        // Doubling the reservation doubles the unrounded figure ($0.04937 -> $0.09874), which the published
        // four-decimal rendering then rounds to 0.0494 and 0.0987 respectively - so the displayed numbers are
        // deliberately NOT exactly double each other. Asserted as the exact expected values rather than as a
        // ratio, because the ratio is the thing rounding breaks.
        small.Hourly.Should().Be(0.0494m);
        large.Hourly.Should().Be(0.0987m);
        large.Monthly.Should().Be(small.Monthly * 2);
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(1024, 0)]
    [InlineData(-1, 2048)]
    [InlineData(1024, -1)]
    public void An_impossible_reservation_is_priced_as_unknown_rather_than_as_zero(int cpu, int memory)
    {
        // A confident "$0.00/month" on a deploy screen is the most misleading number this file could produce.
        var estimate = AwsFargatePricing.For(cpu, memory);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Hourly.Should().BeNull();
        estimate.Monthly.Should().BeNull();
    }

    [Fact]
    public void A_reservation_outside_the_size_matrix_is_still_priced_because_arithmetic_is_arithmetic()
    {
        // Refusing here would mean a task AWS runs today under a matrix row this snapshot predates would report
        // an unknown cost while visibly billing. Rejecting an impossible pair is AwsFargateSizing.Require's job.
        // 8 GB is legal for 1 vCPU but not for half of one - the 512-unit row tops out at 4096 MiB.
        AwsFargateSizing.IsValid(512, 8192).Should().BeFalse();

        AwsFargatePricing.For(512, 8192).Confidence.Should().Be(CostConfidence.ListPrice);
    }

    [Theory]
    [InlineData("1024", "2048")]
    [InlineData("256", "512")]
    public void A_task_definitions_string_fields_are_priced_the_same_as_the_numbers(string cpu, string memory)
    {
        var fromText = AwsFargatePricing.For(cpu, memory);
        var fromNumbers = AwsFargatePricing.For(int.Parse(cpu, System.Globalization.CultureInfo.InvariantCulture), int.Parse(memory, System.Globalization.CultureInfo.InvariantCulture));

        fromText.Hourly.Should().Be(fromNumbers.Hourly);
    }

    [Theory]
    [InlineData("1 vCPU", "2 GB")]
    [InlineData(null, "2048")]
    [InlineData("1024", "")]
    public void Task_definition_fields_that_are_not_plain_unit_counts_are_answered_as_unknown(string? cpu, string? memory)
    {
        // ECS accepts vCPU/GB notation for the EC2 launch type. This adapter never writes it, and guessing at it
        // would be a number nobody could check.
        var estimate = AwsFargatePricing.For(cpu, memory);

        estimate.Confidence.Should().Be(CostConfidence.Unknown);
        estimate.Source.Should().Contain("not the plain ECS unit counts");
    }

    [Fact]
    public void The_source_string_states_that_the_figure_is_not_all_in()
    {
        var source = AwsFargatePricing.For(1024, 2048).Source;

        source.Should().Contain("COMPUTE ONLY - NOT ALL-IN");
        source.Should().Contain("EFS file system");
        source.Should().Contain("CloudWatch Logs");
        source.Should().Contain("public IPv4");
        source.Should().Contain("NAT gateway");
        source.Should().Contain("load balancer");
    }

    [Fact]
    public void The_source_string_names_its_region_and_the_date_it_was_taken()
    {
        var source = AwsFargatePricing.For(1024, 2048).Source;

        source.Should().Contain(AwsFargatePricing.PricedRegion);
        source.Should().Contain(AwsFargatePricing.SnapshotDate);
        source.Should().Contain("not refreshed at runtime");
    }

    [Fact]
    public void The_source_string_says_the_meter_runs_continuously_because_a_service_keeps_a_task_alive()
    {
        AwsFargatePricing.For(1024, 2048).Source
            .Should().Contain("the compute meter runs continuously by design");
    }

    [Fact]
    public void An_unknown_estimate_still_carries_the_full_caveat_so_it_travels_with_the_absence_too()
    {
        AwsFargatePricing.For(0, 0).Source.Should().Contain("COMPUTE ONLY - NOT ALL-IN");
    }
}
