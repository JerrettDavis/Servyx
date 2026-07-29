namespace Servyx.Domain.Provisioning;

/// <summary>
/// Reads an <em>already-provisioned</em> resource and answers two questions about it without touching it:
/// "what would it take to bring this to the desired state?" (<see cref="PlanUpdateAsync"/>) and "does this
/// still match what Servyx provisioned?" (<see cref="DetectDriftAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is its own interface rather than new members on <see cref="IProvisioner"/>.</strong>
/// <see cref="IProvisioner"/> has four implementations today (the Docker container adapter, the SSH process
/// adapter, the DigitalOcean droplet adapter, and a test double in the web test project) and exactly one of
/// them can answer these questions. Putting the members on <see cref="IProvisioner"/> would force the other
/// three to ship an implementation they cannot honour, and every available way of writing that
/// implementation is worse than not having it: throwing turns a capability question into an exception at
/// call time — which the remarks on <see cref="IProvisioner"/> forbid on that interface specifically —
/// while returning an empty plan is the <see cref="DataImpact"/> failure this feature exists to design out,
/// a resource-safety answer produced by a stub rather than by analysis. A separate interface makes "this
/// provider supports maintenance" a fact a caller establishes by a type test, which is checkable, instead of
/// a promise every provider makes and one keeps.
/// </para>
/// <para>
/// <strong>Propose only; nothing here executes.</strong> There is no <c>ApplyAsync</c>, deliberately and for
/// the same reason <see cref="IProvisioner"/> has none. An <see cref="UpdatePlan"/> for a container recreate
/// is a stop, a remove, and a create — destructive, interrupting, and worth someone looking at first. This
/// codebase already models that discipline in
/// <see cref="Lifecycle.IServerLifecycle.RecreateAsync(string, CancellationToken)"/>, which takes an
/// <em>already-approved</em> change-plan id precisely so a recreate is never callable ad hoc. This interface
/// builds the proposing half of that discipline and stops there: no member on it mutates anything, so there
/// is no execution path to gate, review, or get wrong.
/// </para>
/// <para>
/// <strong>Reading live state is still reading.</strong> Unlike <see cref="IProvisioner.PlanAsync"/> — which
/// is pure computation over a request and issues no provider call at all — the members here must inspect the
/// live resource, because a plan that did not look at what is actually there could not honestly state a
/// <see cref="DataImpact"/>. Implementations must confine themselves to read calls; the Docker adapter's
/// test suite asserts that no mutating engine call is issued during planning.
/// </para>
/// </remarks>
public interface IMaintainer
{
    /// <summary>
    /// Stable identifier of the provisioner whose resources this maintainer understands, matching
    /// <see cref="IProvisioner.ProvisionerId"/>. Declared with the same shape so an adapter implementing
    /// both interfaces satisfies both with one property.
    /// </summary>
    string ProvisionerId { get; }

    /// <summary>
    /// The capabilities this adapter actually implements, including the maintenance bits
    /// (<see cref="ProvisioningCapabilities.UpdateInPlace"/>,
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/>,
    /// <see cref="ProvisioningCapabilities.DetectDrift"/>).
    /// </summary>
    ProvisioningCapabilities Capabilities { get; }

    /// <summary>
    /// Inspects the live resource behind <paramref name="handle"/> and returns a plan describing what would
    /// change to bring it to the state <paramref name="desired"/> asks for. Changes nothing.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> when the provider no longer knows about the resource, mirroring
    /// <see cref="IProvisioner.RefreshAsync"/>. That is not the same answer as "nothing needs to change":
    /// there is nothing to update, and inventing a plan to create the resource from scratch would quietly
    /// convert an update preview into a provisioning one.
    /// </remarks>
    /// <param name="handle">The resource to plan an update for.</param>
    /// <param name="desired">The state the resource should end up in.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<UpdatePlan?> PlanUpdateAsync(ResourceHandle handle, ProvisioningRequest desired, CancellationToken ct = default);

    /// <summary>
    /// Compares the live resource against what <paramref name="handle"/> records Servyx provisioned, and
    /// reports every divergence by name. Changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The comparison is only as good as the handle: pass the handle the ledger recorded at creation time,
    /// not one just rebuilt from the live resource by <see cref="IProvisioner.RefreshAsync"/> — the latter
    /// is derived from the very state being checked, so it can only ever report a match.
    /// </para>
    /// <para>
    /// Always returns a result. A resource the provider no longer knows about is reported as a divergence,
    /// not as an absent answer, because its disappearance is exactly the kind of drift a caller is asking
    /// about.
    /// </para>
    /// </remarks>
    /// <param name="handle">The recorded handle to compare the live resource against.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default);
}
