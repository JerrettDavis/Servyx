using Servyx.Infrastructure.Docker;

namespace Servyx.Infrastructure.Docker.Tests;

public class DockerCpuPercentCalculatorTests
{
    [Fact]
    public void Compute_returns_null_when_previous_reading_is_all_zero_first_sample()
    {
        var previous = new CpuUsageSnapshot(0, 0, 4);
        var current = new CpuUsageSnapshot(2_000_000_000, 10_000_000_000, 4);

        var result = DockerCpuPercentCalculator.Compute(current, previous);

        result.Should().BeNull();
    }

    [Fact]
    public void Compute_returns_zero_when_container_used_no_cpu_between_readings()
    {
        var previous = new CpuUsageSnapshot(1_000_000_000, 50_000_000_000, 4);
        var current = new CpuUsageSnapshot(1_000_000_000, 60_000_000_000, 4);

        var result = DockerCpuPercentCalculator.Compute(current, previous);

        result.Should().Be(0.0);
    }

    [Fact]
    public void Compute_returns_zero_when_system_delta_is_zero_or_negative()
    {
        var previous = new CpuUsageSnapshot(1_000_000_000, 50_000_000_000, 4);
        var current = new CpuUsageSnapshot(1_500_000_000, 50_000_000_000, 4);

        var result = DockerCpuPercentCalculator.Compute(current, previous);

        result.Should().Be(0.0);
    }

    [Fact]
    public void Compute_computes_known_percentage_for_a_single_core_container()
    {
        // Container used 2s of CPU time while 10s of wall-clock/system time elapsed on a single online CPU: 20%.
        var seeded = new CpuUsageSnapshot(TotalUsageNanoseconds: 5_000_000_000, SystemUsageNanoseconds: 100_000_000_000, OnlineCpuCount: 1);
        var next = new CpuUsageSnapshot(TotalUsageNanoseconds: 7_000_000_000, SystemUsageNanoseconds: 110_000_000_000, OnlineCpuCount: 1);

        var result = DockerCpuPercentCalculator.Compute(next, seeded);

        result.Should().Be(20.0);
    }

    [Fact]
    public void Compute_scales_by_online_cpu_count()
    {
        // Container used 2s of CPU time across 4 online CPUs while 10s of system time elapsed: (2/10) * 4 * 100 = 80%.
        var previous = new CpuUsageSnapshot(5_000_000_000, 100_000_000_000, 4);
        var current = new CpuUsageSnapshot(7_000_000_000, 110_000_000_000, 4);

        var result = DockerCpuPercentCalculator.Compute(current, previous);

        result.Should().Be(80.0);
    }

    [Fact]
    public void Compute_treats_online_cpu_count_of_zero_as_one()
    {
        var previous = new CpuUsageSnapshot(5_000_000_000, 100_000_000_000, 0);
        var current = new CpuUsageSnapshot(7_000_000_000, 110_000_000_000, 0);

        var result = DockerCpuPercentCalculator.Compute(current, previous);

        result.Should().Be(20.0);
    }
}
