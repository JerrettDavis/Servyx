namespace Servyx.Domain.Provisioning;

/// <summary>
/// Carries out an <see cref="UpdatePlan"/> that has already been revalidated, already been approved, and
/// whose <see cref="UpdatePlan.DataImpact"/> is <em>not</em> <see cref="DataImpact.Preserved"/> — the only
/// interface in this codebase whose single member is allowed to destroy a customer's data.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not another overload on <see cref="IUpdateApplier"/>.</strong>
/// <see cref="IUpdateApplier"/>'s implementations exist to carry out updates that leave the resource's data
/// where it is, and every caller written against it was written on that understanding. Adding a
/// destroy-capable overload there would mean an adapter that grew a destructive path could start destroying
/// data through a call site that had never been changed and never been reviewed. Keeping the destructive verb
/// on its own interface preserves the property the rest of this subsystem is built on: a capability is a type
/// test a caller performs deliberately, not a promise every implementation silently acquires. An adapter may
/// implement both; the two members remain separate entry points and neither can reach the other's operation.
/// </para>
/// <para>
/// <strong><paramref name="acknowledgedDataImpact"/> is not a force flag.</strong> It is the impact a human
/// separately accepted, and it must name <em>exactly</em> the impact the revalidated plan states. It cannot
/// make a stale plan run — the plan-hash check is checked again below it and there is no argument that skips
/// it — it cannot widen what an adapter will execute, and there is no value of it meaning "whatever the plan
/// turns out to be". The <see cref="DataImpact"/> here is deliberately a value the caller writes out rather
/// than one derived from the plan: <c>ApplyDestructiveUpdateAsync(…, plan.DataImpact, …)</c> acknowledges
/// whatever the plan happens to say and therefore acknowledges nothing, which is why the Application layer
/// mints it only from a separately-typed token and never from the plan.
/// </para>
/// <para>
/// <strong>The mirror of Servyx.Application's <c>DataImpactAcknowledgement</c>, restated as a
/// <see cref="DataImpact"/>.</strong> That token type lives in the Application layer and infrastructure
/// assemblies reference only <c>Servyx.Domain</c>, so the token itself cannot cross this boundary — the same
/// constraint the Docker adapter's recreate path already resolves the same way. What crosses is the impact
/// the token named, and the Application layer refuses to produce it at all unless a matching token was
/// supplied. Implementations re-check it regardless, because an adapter is the last line of defence and not
/// the first.
/// </para>
/// <para>
/// <strong>There is no force parameter, here or anywhere below.</strong>
/// </para>
/// </remarks>
public interface IDestructiveUpdateApplier
{
    /// <summary>
    /// Stable identifier of the provisioner whose resources this applier can change, matching
    /// <see cref="IProvisioner.ProvisionerId"/>.
    /// </summary>
    string ProvisionerId { get; }

    /// <summary>
    /// Executes <paramref name="revalidatedPlan"/> — a plan that destroys or endangers persistent data —
    /// against the resource behind <paramref name="handle"/>.
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
    /// <param name="acknowledgedDataImpact">
    /// The impact a human separately accepted, or <see langword="null"/> if none was accepted. Anything other
    /// than an exact match for <see cref="UpdatePlan.DataImpact"/> is
    /// <see cref="UpdateExecutionResult.Refused"/> and issues no provider call.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdateExecutionResult> ApplyDestructiveUpdateAsync(
        ResourceHandle handle,
        UpdatePlan revalidatedPlan,
        string approvedPlanHash,
        DataImpact? acknowledgedDataImpact,
        CancellationToken ct = default);
}
