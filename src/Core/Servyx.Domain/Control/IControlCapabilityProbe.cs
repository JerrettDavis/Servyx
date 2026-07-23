using Servyx.Domain.Transport;

namespace Servyx.Domain.Control;

/// <summary>
/// How invasive a capability probe is allowed to be.
/// </summary>
public enum ProbeDepth
{
    /// <summary>The probe only reads metadata (e.g. file permissions, ownership, socket presence) and never mutates the target.</summary>
    Passive,

    /// <summary>
    /// The probe may create and immediately delete a zero-byte probe artifact (e.g. a temp file) to
    /// verify write access. Requires explicit user consent before it may run.
    /// </summary>
    Active,
}

/// <summary>The identity Servyx is operating as on the target, used by probes that check filesystem/process permissions.</summary>
/// <param name="UserName">The operating-system user name, if known.</param>
/// <param name="Uid">The numeric user id, if known (POSIX targets).</param>
/// <param name="Gid">The numeric primary group id, if known (POSIX targets).</param>
/// <param name="SupplementaryGids">Additional group ids the identity belongs to.</param>
public sealed record TargetIdentity(string? UserName, int? Uid, int? Gid, IReadOnlyList<int> SupplementaryGids)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var name = UserName ?? "(unknown user)";
        var uid = Uid?.ToString() ?? "?";
        var gid = Gid?.ToString() ?? "?";
        var supplementary = SupplementaryGids.Count > 0 ? $", groups=[{string.Join(',', SupplementaryGids)}]" : string.Empty;
        return $"{name} (uid={uid}, gid={gid}{supplementary})";
    }
}

/// <summary>
/// The minimal context a <see cref="IControlCapabilityProbe"/> needs to run. Richer deployment context
/// (host details, transport handles, etc.) arrives in a later milestone.
/// </summary>
/// <param name="ServerId">Identifier of the server being probed.</param>
/// <param name="Depth">How invasive probing is permitted to be for this evaluation.</param>
/// <param name="Identity">The identity Servyx is operating as on the target.</param>
/// <param name="WriteMode">The server's current write posture.</param>
public sealed record CapabilityProbeContext(string ServerId, ProbeDepth Depth, TargetIdentity Identity, WriteMode WriteMode);

/// <summary>
/// Investigates whether one or more <see cref="ControlCapability"/> bits are held for a server.
/// Implementations live outside <c>Servyx.Domain</c> (this project has zero I/O); this interface is the
/// domain-side contract they fulfill.
/// </summary>
public interface IControlCapabilityProbe
{
    /// <summary>A stable identifier for this probe, referenced by the evidence and fingerprints it produces.</summary>
    string ProbeId { get; }

    /// <summary>The capability (or capabilities) this probe investigates.</summary>
    ControlCapability Investigates { get; }

    /// <summary>The minimum <see cref="ProbeDepth"/> required before this probe is allowed to run.</summary>
    ProbeDepth MinimumDepth { get; }

    /// <summary>The transport capabilities this probe needs in order to run.</summary>
    TransportCapabilities RequiresTransport { get; }

    /// <summary>
    /// Runs the probe and returns one or more grants covering <see cref="Investigates"/>. Implementations
    /// should not throw for ordinary "capability not held" outcomes — return a
    /// <see cref="CapabilityGrant.Denied"/> or <see cref="CapabilityGrant.Unknown"/> grant instead. Only
    /// unexpected failures should propagate as exceptions.
    /// </summary>
    /// <param name="ctx">The probe context.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<CapabilityGrant>> ProbeAsync(CapabilityProbeContext ctx, CancellationToken ct = default);
}

/// <summary>
/// Runs the registered <see cref="IControlCapabilityProbe"/>s for a server and merges their results into
/// a single <see cref="ControlCapabilitySet"/>.
/// </summary>
public interface IControlCapabilityEvaluator
{
    /// <summary>Evaluates every applicable probe and returns the merged <see cref="ControlCapabilitySet"/>.</summary>
    /// <param name="ctx">The probe context.</param>
    /// <param name="available">The transport capabilities actually available for this server.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<ControlCapabilitySet> EvaluateAsync(CapabilityProbeContext ctx, TransportCapabilities available, CancellationToken ct = default);
}
