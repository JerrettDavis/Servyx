namespace Servyx.Domain.Entities;

/// <summary>
/// One row of Servyx's cross-cutting accountability trail: who did what, to what, and when — for the app's
/// key write actions (account management, host registration/adoption, configuration change-plan apply/revert,
/// and any future write action that needs the same "who did this" answer).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately NOT the same thing as <c>ChangePlanRecord</c>/<c>ChangePlanActionRecord</c>.</strong>
/// Those two exist to make a configuration change exactly revertible: they carry full pre- and post-image
/// file content per action, because an exact revert needs the literal bytes, not a summary. An
/// <see cref="AuditEntry"/> carries none of that — <see cref="Details"/> is a short, human-readable note, never
/// a payload dump — because its job is a security/accountability trail an operator can scan, not a
/// recoverability mechanism. The two are complementary, not redundant: <c>changeplan.applied</c>/
/// <c>changeplan.reverted</c> entries recorded here are lightweight breadcrumbs pointing at a plan, not a copy
/// of what the plan's own ledger already records in full. See the remarks on <c>PlanExecutor.ApplyAsync</c>/
/// <c>RevertAsync</c> for exactly what gets recorded and why nothing here duplicates <c>ChangePlanRecord</c>'s
/// own columns.
/// </para>
/// <para>
/// <strong>Append-only.</strong> Nothing in this codebase updates or deletes a row after it is written — an
/// accountability trail that could be edited after the fact would not be one. There is deliberately no
/// <c>UpdateAsync</c> on <c>IAuditEntryRepository</c>, only <c>AddAsync</c> and read paths.
/// </para>
/// <para>
/// <strong>Persistence, plus a pluggable writer, only — this phase.</strong> This increment adds the entity,
/// its durable store, and <c>IAuditLogger</c>, and wires that logger into the write actions listed on
/// <c>IAuditLogger</c>'s own remarks. The reader UI (the <c>/audit</c> page) is a separate, later increment —
/// nothing here renders a single row.
/// </para>
/// </remarks>
public sealed class AuditEntry
{
    /// <summary>This entry's own identifier. A plain <see cref="Guid"/>, not a strongly-typed id — matching
    /// <c>ChangePlanActionRecord.Id</c>'s own choice for the same reason: this is a high-volume, append-only
    /// event row, not an aggregate root anything else references by a typed key.</summary>
    public required Guid Id { get; set; }

    /// <summary>When this action happened, in UTC. Indexed — see <c>AuditEntryConfiguration</c> — because a
    /// chronological read (most recent first) is this table's primary access pattern.</summary>
    public required DateTimeOffset TimestampUtc { get; set; }

    /// <summary>
    /// Who did this — a username, or a system marker (see <see cref="AuditActors.System"/>) for an action
    /// with no human behind it. Never blank: every write action this trail covers has an attributable actor,
    /// even if that actor is "the system itself".
    /// </summary>
    public required string Actor { get; set; }

    /// <summary>
    /// What happened, as a short, stable, dotted string — e.g. <c>"user.created"</c>, <c>"host.registered"</c>.
    /// See <see cref="AuditActions"/> for the well-known values this codebase currently records. Deliberately a
    /// plain string, not an enum: adding a new audited event is meant to be "define one more constant", never
    /// an <c>IAuditLogger</c> interface change — see that interface's own remarks.
    /// </summary>
    public required string Action { get; set; }

    /// <summary>
    /// What kind of thing <see cref="Action"/> was done to — e.g. <c>"user"</c>, <c>"host"</c>,
    /// <c>"changeplan"</c> — or <see langword="null"/> when an action has no single target (rare; most actions
    /// this trail records have one). Paired with <see cref="TargetId"/>.
    /// </summary>
    public string? TargetType { get; set; }

    /// <summary>
    /// The identifier of the thing <see cref="Action"/> was done to — e.g. a username, a host name, a
    /// <c>ChangePlanId</c>'s string form — or <see langword="null"/> to match <see cref="TargetType"/>.
    /// Deliberately a plain string rather than a strongly-typed id: this table's targets span every entity
    /// type in the system, and a single free-text column is what lets that stay true without this entity
    /// referencing every one of their id types.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// Short, human-readable context for this entry — e.g. "role changed to Admin" — or
    /// <see langword="null"/> when <see cref="Action"/> and <see cref="TargetId"/> already say everything worth
    /// recording. Never a full payload dump: see this type's own remarks on why that discipline is what keeps
    /// this table apart from <c>ChangePlanActionRecord</c>.
    /// </summary>
    public string? Details { get; set; }
}

/// <summary>
/// Well-known <see cref="AuditEntry.Actor"/> values that do not name a human.
/// </summary>
public static class AuditActors
{
    /// <summary>The actor recorded for an audited action with no interactive caller behind it.</summary>
    public const string System = "system";
}

/// <summary>
/// Well-known <see cref="AuditEntry.Action"/> values currently recorded by this codebase.
/// </summary>
/// <remarks>
/// A catalog for convenience and typo-safety at call sites — not an exhaustive enum. <c>IAuditLogger</c> (see
/// its own remarks) accepts any string; adding a new audited event never requires touching this list, only
/// benefits from it.
/// </remarks>
public static class AuditActions
{
    /// <summary>A new <c>User</c> account was created.</summary>
    public const string UserCreated = "user.created";

    /// <summary>A <c>User</c>'s <c>UserRole</c> was changed.</summary>
    public const string UserRoleChanged = "user.role_changed";

    /// <summary>A <c>User</c> account was activated (re-enabled after deactivation).</summary>
    public const string UserActivated = "user.activated";

    /// <summary>A <c>User</c> account was deactivated.</summary>
    public const string UserDeactivated = "user.deactivated";

    /// <summary>A remote SSH <c>Host</c> was registered.</summary>
    public const string HostRegistered = "host.registered";

    /// <summary>A registered <c>Host</c> was deregistered.</summary>
    public const string HostDeregistered = "host.deregistered";

    /// <summary>An already-running container was adopted as a <c>Server</c>.</summary>
    public const string ServerAdopted = "server.adopted";

    /// <summary>Servyx stopped tracking an adopted <c>Server</c>.</summary>
    public const string ServerForgotten = "server.forgotten";

    /// <summary>A <c>ChangePlanRecord</c> was applied.</summary>
    public const string ChangePlanApplied = "changeplan.applied";

    /// <summary>A <c>ChangePlanRecord</c> was reverted.</summary>
    public const string ChangePlanReverted = "changeplan.reverted";
}
