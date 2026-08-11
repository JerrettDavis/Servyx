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

/// <summary>
/// One desired value that <see cref="IPlanExecutor.PreviewAsync"/> could <em>not</em> turn into a
/// <see cref="PlannedAction"/>, and why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The counterpart of <see cref="SurfaceResolutionFailure"/>, one level up.</strong> That type
/// answers "this surface could not be reached"; this one answers "this setting could not be written", which
/// is the question an operator staring at a settings form is actually asking. The two are related but not
/// interchangeable: one unreachable surface can block several settings, and a perfectly reachable surface
/// can still block one setting whose pointer names a value the format cannot address.
/// </para>
/// <para>
/// <strong>Blocking is a first-class result, never an exception and never a silent omission.</strong> A
/// preview that quietly dropped an unwritable key would show an operator a plan that does less than they
/// asked for, with nothing in the UI saying so — and they would find out only when the setting failed to take
/// effect on a running server. Every desired key that cannot be fully written appears here instead, which is
/// what makes <see cref="ConfigChangePlan.Feasibility"/> answerable at all.
/// </para>
/// </remarks>
/// <param name="SettingKey">The catalogue key of the setting that could not be written.</param>
/// <param name="SurfaceId">The surface the blocked write binding targeted.</param>
/// <param name="Reason">What specifically was wrong, phrased for an operator rather than a stack trace.</param>
/// <param name="RemediationHint">
/// The concrete next action that would unblock this change. Never empty, for the same reason
/// <see cref="SurfaceResolutionFailure.RemediationHint"/> never is: a refusal an operator cannot act on is a
/// dead end.
/// </param>
public sealed record BlockedChange(string SettingKey, string SurfaceId, string Reason, string RemediationHint);

/// <summary>How much of what an operator asked for a <see cref="ConfigChangePlan"/> can actually deliver.</summary>
public enum PlanFeasibility
{
    /// <summary>Every requested change became a <see cref="PlannedAction"/>; nothing was blocked.</summary>
    FullyAchievable,

    /// <summary>Some changes became actions and at least one was blocked — applying this plan does part of what was asked.</summary>
    PartiallyAchievable,

    /// <summary>Nothing could be planned: every requested change was blocked. Applying this plan would do nothing at all.</summary>
    Blocked,
}

/// <summary>Kind of advisory note attached to a <see cref="ConfigChangePlan"/>.</summary>
/// <remarks>
/// Distinct from both <see cref="Consequence"/> and <see cref="BlockedChange"/>, and deliberately so. A
/// consequence is a truthful statement about what applying the plan will do to the workload; a blocked change
/// is a write that will not happen. A diagnostic is neither — it is something an operator needs to know that
/// would otherwise be inferred wrongly from the plan's silence.
/// </remarks>
public enum PlanDiagnosticKind
{
    /// <summary>
    /// The governing definition is internally malformed in a way the previewer worked around rather than
    /// failed on — today, a cycle in the <c>derivedFrom</c> graph. The plan is still valid; the definition
    /// needs fixing.
    /// </summary>
    DefinitionDefect,

    /// <summary>
    /// A surface downstream of one being written regenerates only on a <see cref="RegenerationKind.Manual"/>
    /// trigger, so the change will not reach the running workload until an operator regenerates it by hand.
    /// Deliberately not a <see cref="ConsequenceKind.RestartRequired"/> consequence (no restart would help)
    /// and deliberately not silence (silence reads as "this takes effect immediately").
    /// </summary>
    ManualRegenerationRequired,
}

/// <summary>An advisory note attached to a plan: something true and load-bearing that is neither a consequence nor a blocked change.</summary>
/// <param name="Kind">What kind of note this is.</param>
/// <param name="SurfaceId">The surface the note is about.</param>
/// <param name="Message">Human-readable explanation shown in the UI, phrased for an operator.</param>
public sealed record PlanDiagnostic(PlanDiagnosticKind Kind, string SurfaceId, string Message);

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
    /// <summary>
    /// Every requested change that could not be turned into a <see cref="PlannedAction"/>. Empty when the
    /// plan does everything that was asked of it.
    /// </summary>
    /// <remarks>
    /// An init-only property with a default rather than a positional parameter, so the four-parameter
    /// construction every existing caller and test uses keeps compiling and keeps meaning exactly what it
    /// meant before: a plan with nothing blocked.
    /// </remarks>
    public IReadOnlyList<BlockedChange> Blocked { get; init; } = [];

    /// <summary>
    /// Advisory notes about this plan — a malformed definition worked around, or a downstream surface that
    /// only regenerates by hand. Empty in the ordinary case. Init-only with a default for the same
    /// compatibility reason as <see cref="Blocked"/>.
    /// </summary>
    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How much of what the operator asked for this plan can actually deliver.</summary>
    /// <remarks>
    /// Derived rather than stored, so it cannot disagree with <see cref="Actions"/> and <see cref="Blocked"/>.
    /// Note the ordering: "nothing blocked" wins outright, so a plan with no actions and no blocked changes —
    /// an operator who asked for nothing, or asked only for values already in place — is
    /// <see cref="PlanFeasibility.FullyAchievable"/> rather than <see cref="PlanFeasibility.Blocked"/>. There
    /// is nothing obstructing an empty plan.
    /// </remarks>
    public PlanFeasibility Feasibility => Blocked.Count == 0
        ? PlanFeasibility.FullyAchievable
        : Actions.Count == 0
            ? PlanFeasibility.Blocked
            : PlanFeasibility.PartiallyAchievable;

    /// <summary>True when the plan has at least one action and every action is individually reversible.</summary>
    public bool IsFullyReversible => Actions.Count > 0 && Actions.All(a => a.Reversible);

    /// <summary>True when applying this plan requires a workload restart.</summary>
    public bool RequiresRestart => Consequences.Any(c => c.Kind == ConsequenceKind.RestartRequired);

    /// <summary>True when applying this plan requires the workload's container to be recreated.</summary>
    public bool RequiresRecreate => Consequences.Any(c => c.Kind == ConsequenceKind.RecreateRequired);
}
