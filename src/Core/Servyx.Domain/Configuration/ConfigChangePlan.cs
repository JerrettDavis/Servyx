using Servyx.Domain.Transport;

namespace Servyx.Domain.Configuration;

/// <summary>Kind of a single planned action within a <see cref="ConfigChangePlan"/>.</summary>
public enum PlannedActionKind
{
    /// <summary>Writes a value into a host-file-backed surface.</summary>
    WriteSurface,

    /// <summary>Writes a value via a control-channel-backed surface.</summary>
    WriteControlChannel,
}

/// <summary>A single action within a <see cref="ConfigChangePlan"/>, including its unified diff.</summary>
/// <param name="Kind">What kind of action this is.</param>
/// <param name="SurfaceId">The surface this action targets.</param>
/// <param name="UnifiedDiff">A unified diff of the change, with secret values masked.</param>
/// <param name="Reversible">Whether this action can be reverted from its recorded pre-image.</param>
/// <param name="RequiredCapabilities">The transport capabilities required to apply this action.</param>
public sealed record PlannedAction(PlannedActionKind Kind, string SurfaceId, string UnifiedDiff, bool Reversible, TransportCapabilities RequiredCapabilities);

/// <summary>Kind of downstream effect an applied plan may have.</summary>
public enum ConsequenceKind
{
    /// <summary>The workload must be restarted for the change to take effect.</summary>
    RestartRequired,

    /// <summary>The workload's container must be recreated for the change to take effect.</summary>
    RecreateRequired,

    /// <summary>Applying the plan will interrupt service, even if no restart/recreate is strictly required.</summary>
    ServiceInterruption,
}

/// <summary>A downstream effect of applying a plan, surfaced to the operator before approval.</summary>
/// <param name="Kind">The kind of consequence.</param>
/// <param name="Description">Human-readable explanation shown in the UI.</param>
public sealed record Consequence(ConsequenceKind Kind, string Description);

/// <summary>A previewed, not-yet-applied set of configuration changes.</summary>
/// <param name="Id">Identifier for this plan, used by <see cref="IPlanExecutor.ApplyAsync"/> and <see cref="IPlanExecutor.RevertAsync"/>.</param>
/// <param name="Actions">The individual actions that make up this plan.</param>
/// <param name="Consequences">Downstream effects of applying this plan.</param>
/// <param name="SurfaceHashes">Content hash of each bound surface at preview time, used to detect drift before apply.</param>
public sealed record ConfigChangePlan(
    string Id,
    IReadOnlyList<PlannedAction> Actions,
    IReadOnlyList<Consequence> Consequences,
    IReadOnlyDictionary<string, string> SurfaceHashes)
{
    /// <summary>True when the plan has at least one action and every action is individually reversible.</summary>
    public bool IsFullyReversible => Actions.Count > 0 && Actions.All(a => a.Reversible);

    /// <summary>True when applying this plan requires a workload restart.</summary>
    public bool RequiresRestart => Consequences.Any(c => c.Kind == ConsequenceKind.RestartRequired);

    /// <summary>True when applying this plan requires the workload's container to be recreated.</summary>
    public bool RequiresRecreate => Consequences.Any(c => c.Kind == ConsequenceKind.RecreateRequired);
}
