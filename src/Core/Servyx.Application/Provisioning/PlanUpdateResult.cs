using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>
/// The outcome of <see cref="IProvisioningDashboard.PlanUpdateAsync"/>: exactly one of "here is the plan",
/// "this provisioner cannot answer maintenance questions at all", or "the provider no longer knows about
/// that resource".
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a closed hierarchy rather than <c>UpdatePlan?</c>.</strong> A nullable plan would collapse two
/// answers that a caller must not confuse. "This provisioner does not implement <see cref="IMaintainer"/>"
/// is a statement about the adapter — the same call will never succeed, and a UI should not offer the
/// control. "The provider no longer knows about this resource" is a statement about the resource, and the
/// remedy is a reconcile, not a retry. <see cref="IMaintainer.PlanUpdateAsync"/> already refuses to blur the
/// second one into "nothing needs to change"; this type refuses to blur it into the first.
/// </para>
/// <para>
/// Nothing here mutates. Producing any of these cases involves reads only —
/// <see cref="IMaintainer.PlanUpdateAsync"/> inspects the live resource and changes nothing — so a caller
/// may compute one freely and show it before anyone has approved anything.
/// </para>
/// </remarks>
public abstract record PlanUpdateResult
{
    // Private so the case set is closed to this file, matching ProvisioningApplyResult.
    private PlanUpdateResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The maintainer inspected the live resource and produced a plan. Nothing was changed.</summary>
    public sealed record Planned : PlanUpdateResult
    {
        /// <summary>Creates a planned result.</summary>
        /// <param name="plan">The plan the maintainer computed. Its <see cref="UpdatePlan.PlanHash"/> is what a later apply must quote back.</param>
        /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
        public Planned(UpdatePlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);

            Plan = plan;
        }

        /// <summary>The computed plan, including the <see cref="Domain.Provisioning.DataImpact"/> it states.</summary>
        public UpdatePlan Plan { get; }

        /// <inheritdoc />
        public override string Message =>
            $"'{Plan.ProvisionerId}' would reach the desired state by {Plan.Strategy} "
            + $"({Plan.Changes.Count} change(s)); persistent data: {Plan.DataImpact}.";
    }

    /// <summary>
    /// The provisioner is registered, but does not implement <see cref="IMaintainer"/>, so it cannot say
    /// what an update would do. Nothing was read and nothing was changed.
    /// </summary>
    /// <remarks>
    /// This is discovered by a type test, which is the whole reason <see cref="IMaintainer"/> is a separate
    /// interface from <see cref="IProvisioner"/> — see the remarks on <see cref="IMaintainer"/>. It is
    /// reported rather than thrown because "can this provider do maintenance?" is a capability question a UI
    /// legitimately asks about every provisioner it lists, not an exceptional condition.
    /// </remarks>
    public sealed record Unsupported : PlanUpdateResult
    {
        /// <summary>Creates an unsupported result.</summary>
        /// <param name="provisionerId">The provisioner that does not implement <see cref="IMaintainer"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="provisionerId"/> is null, empty, or whitespace.</exception>
        public Unsupported(string provisionerId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

            ProvisionerId = provisionerId;
        }

        /// <summary>The provisioner that cannot answer maintenance questions.</summary>
        public string ProvisionerId { get; }

        /// <inheritdoc />
        public override string Message =>
            $"Provisioner '{ProvisionerId}' does not support maintenance: it implements no update planning or "
            + "drift detection, so there is nothing to preview and no update to apply.";
    }

    /// <summary>
    /// The maintainer looked and the provider no longer knows about the resource, so there is nothing to
    /// update. Distinct from "nothing needs to change".
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="IMaintainer.PlanUpdateAsync"/> returning <see langword="null"/>. Inventing a plan
    /// that creates the resource from scratch would quietly convert an update preview into a provisioning
    /// one, so this case exists to stop that at the type level.
    /// </remarks>
    public sealed record ResourceGone : PlanUpdateResult
    {
        /// <summary>Creates a resource-gone result.</summary>
        /// <param name="provisionerId">The provisioner that was asked.</param>
        /// <param name="handle">The handle the provider no longer recognises.</param>
        /// <exception cref="ArgumentException"><paramref name="provisionerId"/> is null, empty, or whitespace.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="handle"/> is null.</exception>
        public ResourceGone(string provisionerId, ResourceHandle handle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
            ArgumentNullException.ThrowIfNull(handle);

            ProvisionerId = provisionerId;
            Handle = handle;
        }

        /// <summary>The provisioner that was asked.</summary>
        public string ProvisionerId { get; }

        /// <summary>The handle the provider no longer recognises.</summary>
        public ResourceHandle Handle { get; }

        /// <inheritdoc />
        public override string Message =>
            $"'{ProvisionerId}' no longer knows about resource '{Handle.ProviderResourceId}', so there is nothing "
            + "to update. This is not the same as 'nothing needs to change' — reconcile before acting on it.";
    }
}
