using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// A caller's explicit, separately-constructed statement that it understands an update plan will not leave
/// the resource's persistent data <see cref="DataImpact.Preserved"/>, and which of the non-preserving
/// impacts it is accepting.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type exists so that data loss cannot be approved by the same argument that approves a safe
/// update.</strong> The argument that says "yes, apply the plan I was shown" on
/// <see cref="IProvisioningDashboard.ApplyUpdateAsync"/> is the approved plan hash — a string every update
/// carries, preserving or not. If that string were also sufficient to authorise a
/// <see cref="DataImpact.Destroyed"/> plan, then a caller that had only ever been written and tested against
/// preserving updates would destroy data the first time an adapter's analysis came back differently, without
/// a single line of that caller changing. Making the acknowledgement its own parameter of its own type means
/// the compiler, not a code review, is what stops that: there is no value of <c>string</c> that inhabits this
/// type, and no way to reach a non-preserving execution without naming one of the factories below.
/// </para>
/// <para>
/// <strong>There is deliberately no factory for <see cref="DataImpact.Preserved"/>.</strong> A preserving
/// plan needs no acknowledgement, so a token for one is not a thing that can exist — which is what makes
/// "the acknowledgement is not the ordinary approval" true by construction rather than by convention. It
/// also means a caller cannot mint a token for the cheap case and later find that same token accepted for
/// the expensive one.
/// </para>
/// <para>
/// <strong>There is deliberately no <c>For(DataImpact)</c> factory either.</strong> Such a factory would
/// invite <c>DataImpactAcknowledgement.For(plan.DataImpact)</c>, which acknowledges whatever the plan happens
/// to say and therefore acknowledges nothing at all — the human-in-the-loop step laundered into a data flow.
/// The two factories are named after the specific impact they accept so that accepting one is visible in the
/// caller's source.
/// </para>
/// <para>
/// <strong>An acknowledgement is impact-specific, not a general override.</strong>
/// <see cref="Covers(DataImpact)"/> is exact equality, so a token for <see cref="DataImpact.AtRisk"/> does
/// not authorise a <see cref="DataImpact.Destroyed"/> plan. This is not a force flag: it cannot make a stale
/// plan run, it cannot skip the plan-hash revalidation that precedes it, and there is no value of it that
/// means "whatever the plan turns out to be".
/// </para>
/// </remarks>
public sealed class DataImpactAcknowledgement
{
    private DataImpactAcknowledgement(DataImpact acknowledged) => Acknowledged = acknowledged;

    /// <summary>
    /// The single <see cref="DataImpact"/> this token accepts. Never <see cref="DataImpact.Preserved"/>,
    /// because no factory produces one.
    /// </summary>
    public DataImpact Acknowledged { get; }

    /// <summary>
    /// Acknowledges that the update may separate the workload from some of its state — see
    /// <see cref="DataImpact.AtRisk"/>. Does not acknowledge <see cref="DataImpact.Destroyed"/>.
    /// </summary>
    public static DataImpactAcknowledgement AtRisk() => new(DataImpact.AtRisk);

    /// <summary>
    /// Acknowledges that the update deletes a store the resource's persistent data lives in — see
    /// <see cref="DataImpact.Destroyed"/>. Approving this is approving data loss.
    /// </summary>
    public static DataImpactAcknowledgement Destroyed() => new(DataImpact.Destroyed);

    /// <summary>
    /// Whether this token acknowledges exactly <paramref name="planImpact"/>. Exact equality: acknowledging
    /// one impact never covers another, and nothing covers <see cref="DataImpact.Preserved"/> (a preserving
    /// plan is applied by supplying no token at all).
    /// </summary>
    /// <param name="planImpact">The impact the plan being applied actually states.</param>
    public bool Covers(DataImpact planImpact) => planImpact == Acknowledged;

    /// <summary>
    /// Whether <paramref name="acknowledgement"/> — which may be <see langword="null"/>, meaning the caller
    /// approved only an ordinary preserving update — is the correct approval for a plan stating
    /// <paramref name="planImpact"/>.
    /// </summary>
    /// <remarks>
    /// Both directions are checked. A non-preserving plan with no token (or the wrong token) is refused
    /// because the caller has not accepted what would happen; a <see cref="DataImpact.Preserved"/> plan with
    /// a token is also refused, because the caller is approving something other than the plan that would
    /// run, and a mismatch between what was acknowledged and what was planned is exactly the state this
    /// parameter exists to detect.
    /// </remarks>
    /// <param name="acknowledgement">The token the caller supplied, or <see langword="null"/> if none.</param>
    /// <param name="planImpact">The impact stated by the plan that was just revalidated.</param>
    internal static bool Satisfies(DataImpactAcknowledgement? acknowledgement, DataImpact planImpact) =>
        planImpact == DataImpact.Preserved
            ? acknowledgement is null
            : acknowledgement is not null && acknowledgement.Covers(planImpact);
}
