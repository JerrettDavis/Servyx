namespace Servyx.Domain.Provisioning;

/// <summary>
/// Carries out an <see cref="UpdatePlan"/> that has <em>already</em> been revalidated and approved. This is
/// the only interface in the provisioning subsystem whose single member can change a resource that already
/// exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not a member on <see cref="IMaintainer"/>.</strong> <see cref="IMaintainer"/>'s
/// remarks state, as a design guarantee, that no member on it mutates anything — which is what lets a caller
/// establish "planning cannot change my machine" by looking at the type. Adding an apply verb there would
/// retract that guarantee for every implementation at once, including the ones that only ever want to
/// describe. Keeping the mutating verb on its own interface preserves the same property
/// <see cref="IProvisioningOperation"/> gives the create path: "this adapter can execute an update" is a type
/// test a caller performs, not a promise every maintainer makes.
/// </para>
/// <para>
/// <strong>An implementation is the last line of defence, not the first.</strong> The approval discipline
/// lives above this interface — a plan is recomputed from the live resource and compared against the hash
/// the user approved, and a non-preserving <see cref="DataImpact"/> additionally requires a separately-typed
/// acknowledgement — and none of it is skippable from here. <paramref name="approvedPlanHash"/> is
/// nevertheless taken by <see cref="ApplyUpdateAsync"/> and checked again by the implementation, so that an
/// adapter reached by some future caller that forgot to revalidate still refuses rather than executes.
/// </para>
/// <para>
/// <strong>Implementations may execute less than a plan describes, but never more, and never silently.</strong>
/// An adapter that implements one of the operations a plan can call for must <em>refuse</em> — with
/// <see cref="UpdateExecutionResult.Refused"/> and without issuing a provider call — any plan that describes
/// anything else. Executing the recognised part of a plan and ignoring the rest would report a partially
/// applied update as an applied one, which is precisely the misdescription <see cref="UpdatePlan"/>'s own
/// invariants exist to prevent.
/// </para>
/// <para>
/// <strong>There is no force parameter, here or anywhere below.</strong>
/// </para>
/// </remarks>
public interface IUpdateApplier
{
    /// <summary>
    /// Stable identifier of the provisioner whose resources this applier can change, matching
    /// <see cref="IProvisioner.ProvisionerId"/>.
    /// </summary>
    string ProvisionerId { get; }

    /// <summary>
    /// Executes <paramref name="revalidatedPlan"/> against the resource behind <paramref name="handle"/>.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws for every outcome the provider can produce, so a caller renders a refusal,
    /// a failure and a still-running operation as the different things they are. Only a defect — a null
    /// argument, or a provider response the adapter cannot parse at all — throws.
    /// </remarks>
    /// <param name="handle">The already-provisioned resource to change.</param>
    /// <param name="revalidatedPlan">
    /// The plan to execute, as recomputed from the live resource immediately beforehand — not the plan
    /// object the user was originally shown.
    /// </param>
    /// <param name="approvedPlanHash">
    /// The plan hash the caller approved. Compared against <see cref="UpdatePlan.PlanHash"/> again here; a
    /// mismatch is <see cref="UpdateExecutionResult.Refused"/> and issues no provider call.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdateExecutionResult> ApplyUpdateAsync(
        ResourceHandle handle,
        UpdatePlan revalidatedPlan,
        string approvedPlanHash,
        CancellationToken ct = default);
}
