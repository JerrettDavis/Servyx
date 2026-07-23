namespace Servyx.Domain.Observability;

/// <summary>A single point-in-time resource usage sample.</summary>
/// <param name="Timestamp">When the sample was taken.</param>
/// <param name="CpuPercent">CPU utilization, as a percentage.</param>
/// <param name="MemoryBytes">Memory used, in bytes.</param>
/// <param name="NetworkRxBytes">Cumulative bytes received.</param>
/// <param name="NetworkTxBytes">Cumulative bytes transmitted.</param>
public sealed record ResourceSample(DateTimeOffset Timestamp, double CpuPercent, long MemoryBytes, long NetworkRxBytes, long NetworkTxBytes);

/// <summary>
/// Supplies resource metrics. Backed by an in-memory ring buffer and exported via OpenTelemetry — metrics
/// are deliberately not persisted to the relational store.
/// </summary>
public interface IMetricsSource
{
    /// <summary>Streams resource samples for the given server as they are collected.</summary>
    IAsyncEnumerable<ResourceSample> StreamAsync(string serverId, CancellationToken ct = default);
}
