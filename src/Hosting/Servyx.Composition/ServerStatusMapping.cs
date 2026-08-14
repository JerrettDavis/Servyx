using Servyx.Application.Servers;
using Servyx.Domain.Observability;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Composition;

/// <summary>
/// A single server's cached status plus its most recent resource sample — the shape
/// <see cref="ServerStatusCache"/> holds in memory. <see cref="UpdatedAt"/> is what
/// <c>LiveDashboardDataService</c> uses to decide <c>IsStale</c>: it is stamped once per background refresh
/// tick, not per read, so every entry produced by the same tick shares one timestamp.
/// </summary>
/// <param name="Summary">The server's status, as of <paramref name="UpdatedAt"/>.</param>
/// <param name="Metrics">The most recent resource sample taken for this server, or <see langword="null"/> if none has ever succeeded.</param>
/// <param name="UpdatedAt">When this entry was last refreshed.</param>
public sealed record ServerStatusEntry(ServerSummary Summary, ResourceSample? Metrics, DateTimeOffset UpdatedAt);

/// <summary>
/// Bridges <see cref="ServerStatusSnapshot"/> (the durable, <c>Servyx.Domain</c>-only row —
/// <c>Servyx.Infrastructure.Persistence</c> deliberately does not reference <c>Servyx.Application</c>, see
/// that entity's own remarks) and <see cref="ServerStatusEntry"/> (the in-memory, Application-layer shape
/// both <see cref="ServerStatusCache"/> and <see cref="ServerStatusRefreshService"/> work with). This is the
/// one place that translates between them, so the two never drift apart independently.
/// </summary>
internal static class ServerStatusMapping
{
    /// <summary>
    /// Game-neutral fallback shown whenever a server's health is Unhealthy but this background path (which,
    /// unlike <c>ServerQueryService</c>, resolves no per-definition <c>HealthSignalDefinition</c>) has no
    /// richer explanation to offer. Mirrors <c>ServerQueryService.GenericUnhealthyExplanation</c> verbatim.
    /// </summary>
    public const string GenericUnhealthyExplanation =
        "The container's own health check is reporting unhealthy. This definition has not documented " +
        "whether that signal can be trusted, so Servyx is showing it as-is.";

    /// <summary>Builds a brand-new durable row from a freshly-refreshed entry.</summary>
    public static ServerStatusSnapshot ToNewRecord(string containerId, ServerStatusEntry entry) => new()
    {
        ContainerId = containerId,
        Name = entry.Summary.Name,
        Game = entry.Summary.Game,
        State = entry.Summary.State,
        Health = entry.Summary.Health.ToString(),
        HealthDetail = entry.Summary.HealthDetail,
        StartedAt = entry.Summary.StartedAt,
        Host = entry.Summary.Host,
        HostKey = entry.Summary.HostKey,
        BindingStatus = entry.Summary.BindingStatus.ToString(),
        AmbiguousCandidateGameIds = entry.Summary.AmbiguousCandidateGameIds ?? [],
        Ports = entry.Summary.Ports.Select(p => new ServerPortSnapshot(p.HostPort, p.ContainerPort, p.Protocol)).ToList(),
        CpuPercent = entry.Metrics?.CpuPercent,
        MemoryBytes = entry.Metrics?.MemoryBytes,
        PlayersOnline = entry.Summary.PlayersOnline,
        PlayersMax = entry.Summary.PlayersMax,
        UpdatedAt = entry.UpdatedAt,
    };

    /// <summary>Overwrites every mutable column of an already-tracked row with a freshly-refreshed entry, in place.</summary>
    public static void ApplyTo(ServerStatusSnapshot record, ServerStatusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entry);

        record.Name = entry.Summary.Name;
        record.Game = entry.Summary.Game;
        record.State = entry.Summary.State;
        record.Health = entry.Summary.Health.ToString();
        record.HealthDetail = entry.Summary.HealthDetail;
        record.StartedAt = entry.Summary.StartedAt;
        record.Host = entry.Summary.Host;
        record.HostKey = entry.Summary.HostKey;
        record.BindingStatus = entry.Summary.BindingStatus.ToString();
        record.AmbiguousCandidateGameIds = entry.Summary.AmbiguousCandidateGameIds ?? [];
        record.Ports = entry.Summary.Ports.Select(p => new ServerPortSnapshot(p.HostPort, p.ContainerPort, p.Protocol)).ToList();
        record.CpuPercent = entry.Metrics?.CpuPercent;
        record.MemoryBytes = entry.Metrics?.MemoryBytes;
        record.PlayersOnline = entry.Summary.PlayersOnline;
        record.PlayersMax = entry.Summary.PlayersMax;
        record.UpdatedAt = entry.UpdatedAt;
    }

    /// <summary>Reconstructs an in-memory <see cref="ServerStatusEntry"/> from a durable row, e.g. while priming the cache at startup.</summary>
    public static ServerStatusEntry ToEntry(ServerStatusSnapshot row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var summary = new ServerSummary(
            Id: row.ContainerId,
            Name: row.Name,
            Game: row.Game,
            State: row.State,
            Health: Enum.TryParse<ServerHealthStatus>(row.Health, out var health) ? health : ServerHealthStatus.Unknown,
            HealthDetail: row.HealthDetail,
            StartedAt: row.StartedAt,
            Host: row.Host,
            Ports: row.Ports.Select(p => new ServerPort(p.HostPort, p.ContainerPort, p.Protocol)).ToList(),
            BindingStatus: Enum.TryParse<ServerBindingStatus>(row.BindingStatus, out var binding) ? binding : ServerBindingStatus.Bound,
            AmbiguousCandidateGameIds: row.AmbiguousCandidateGameIds.Count == 0 ? null : row.AmbiguousCandidateGameIds,
            HostKey: row.HostKey,
            PlayersOnline: row.PlayersOnline,
            PlayersMax: row.PlayersMax);

        // Only Cpu/Memory were ever persisted (see ServerStatusSnapshot's remarks) — network counters are
        // not columns on that row, so a reconstructed sample always reports zero for them. Nothing reads
        // those two fields off a cache-sourced sample today (LiveDashboardDataService only plots CPU/memory).
        ResourceSample? metrics = row.CpuPercent is null && row.MemoryBytes is null
            ? null
            : new ResourceSample(row.UpdatedAt, row.CpuPercent ?? 0, row.MemoryBytes ?? 0, NetworkRxBytes: 0, NetworkTxBytes: 0);

        return new ServerStatusEntry(summary, metrics, row.UpdatedAt);
    }
}
