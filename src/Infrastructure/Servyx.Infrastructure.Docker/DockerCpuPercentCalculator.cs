namespace Servyx.Infrastructure.Docker;

/// <summary>
/// The CPU-accounting fields from one Docker container stats reading (either <c>cpu_stats</c> or
/// <c>precpu_stats</c> in the raw JSON), decoupled from the Docker.DotNet response shape so the
/// calculation below can be unit tested with plain values.
/// </summary>
/// <param name="TotalUsageNanoseconds">Cumulative container CPU usage in nanoseconds since boot.</param>
/// <param name="SystemUsageNanoseconds">Cumulative host-wide CPU usage in nanoseconds since boot.</param>
/// <param name="OnlineCpuCount">Number of CPUs visible to the container at the time of this reading.</param>
public readonly record struct CpuUsageSnapshot(ulong TotalUsageNanoseconds, ulong SystemUsageNanoseconds, uint OnlineCpuCount);

/// <summary>
/// Computes container CPU utilization percentage from two consecutive stats readings, following the
/// same formula <c>docker stats</c> itself uses: the ratio of the container's CPU-time delta to the
/// host's CPU-time delta, scaled by the number of online CPUs.
/// </summary>
public static class DockerCpuPercentCalculator
{
    /// <summary>
    /// Computes the CPU percentage between <paramref name="previous"/> and <paramref name="current"/>.
    /// </summary>
    /// <returns>
    /// The CPU utilization percentage (which may legitimately be <c>0</c> when the container is idle),
    /// or <see langword="null"/> when <paramref name="previous"/> carries no genuine prior reading (all
    /// zero, e.g. the very first sample taken for a container) — a single sample cannot produce a
    /// meaningful percentage, and this method refuses to fabricate one.
    /// </returns>
    public static double? Compute(CpuUsageSnapshot current, CpuUsageSnapshot previous)
    {
        if (previous.TotalUsageNanoseconds == 0 && previous.SystemUsageNanoseconds == 0)
        {
            return null;
        }

        var cpuDelta = (double)current.TotalUsageNanoseconds - previous.TotalUsageNanoseconds;
        var systemDelta = (double)current.SystemUsageNanoseconds - previous.SystemUsageNanoseconds;

        if (systemDelta <= 0 || cpuDelta <= 0)
        {
            // No measurable change in either the container's or the host's CPU time between the two
            // readings: a legitimate "0% busy" result, not a division-by-zero to guard against silently.
            return 0.0;
        }

        var onlineCpus = current.OnlineCpuCount > 0 ? current.OnlineCpuCount : 1;
        return cpuDelta / systemDelta * onlineCpus * 100.0;
    }
}
