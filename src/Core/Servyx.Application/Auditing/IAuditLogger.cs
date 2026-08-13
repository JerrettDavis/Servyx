using Servyx.Domain.Entities;

namespace Servyx.Application.Auditing;

/// <summary>
/// Records an entry in Servyx's cross-cutting accountability trail — who did what, to what, and when — for
/// the app's key write actions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Wired into, at minimum:</strong> the mutating members of <c>IUserService</c> (create, role change,
/// activate/deactivate — see <see cref="AuditActions.UserCreated"/> and its siblings),
/// <c>IHostRegistrationService.RegisterAsync</c>/<c>DeregisterAsync</c>, <c>IServerAdoptionService.AdoptAsync</c>/
/// <c>ForgetAsync</c>, and <c>PlanExecutor.ApplyAsync</c>/<c>RevertAsync</c>.
/// </para>
/// <para>
/// <strong>Pluggable by design: a new event type is a new <see cref="AuditEntry.Action"/> string, never an
/// interface change.</strong> This is why the surface is a single <see cref="RecordAsync(AuditEntry, CancellationToken)"/>
/// plus one convenience overload, rather than one method per audited event (a
/// <c>RecordUserCreatedAsync</c>/<c>RecordHostRegisteredAsync</c>/... shape). Matches the same "one shape,
/// section-keyed content" discipline <c>ISettingsDataService</c> uses for settings sections (see commit
/// 24f6a79): a caller that wants to audit a brand-new kind of event defines one more
/// <see cref="AuditActions"/> constant and calls the existing method — it never touches this interface or its
/// implementation.
/// </para>
/// <para>
/// <strong>Never throws for an ordinary write failure.</strong> Recording an audit entry is a side effect of
/// the action it describes, not a precondition for it — a database hiccup while writing an audit row must
/// never be the reason a role change or a host registration itself fails. See
/// <c>AuditLogger</c>'s own remarks for how that is enforced.
/// </para>
/// </remarks>
public interface IAuditLogger
{
    /// <summary>Records <paramref name="entry"/> verbatim.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Convenience overload: builds an <see cref="AuditEntry"/> from its parts (minting <see cref="AuditEntry.Id"/>
    /// and stamping <see cref="AuditEntry.TimestampUtc"/> from this logger's own clock) and records it. The
    /// call-site-friendly counterpart to <see cref="RecordAsync(AuditEntry, CancellationToken)"/> — most
    /// callers have an actor, an action, and maybe a target and a detail string on hand, not a fully-formed
    /// entity.
    /// </summary>
    /// <param name="actor">Who did this — a username, or <see cref="AuditActors.System"/> for a non-interactive event.</param>
    /// <param name="action">What happened — see <see cref="AuditActions"/> for the well-known values.</param>
    /// <param name="targetType">What kind of thing <paramref name="action"/> was done to, or <see langword="null"/>.</param>
    /// <param name="targetId">The identifier of the thing <paramref name="action"/> was done to, or <see langword="null"/>.</param>
    /// <param name="details">Short, human-readable context, or <see langword="null"/>. Never a full payload dump — see <see cref="AuditEntry"/>'s own remarks.</param>
    /// <param name="ct">Cancels the recording.</param>
    Task RecordAsync(
        string actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        string? details = null,
        CancellationToken ct = default);
}
