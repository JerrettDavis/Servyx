namespace Servyx.Domain.Provisioning;

/// <summary>
/// Creates, inspects, and reconciles infrastructure resources for a specific hosting provider (e.g. a
/// cloud VM provider). Declared in <c>Servyx.Domain</c> so <c>Servyx.Application</c> can plan and reconcile
/// provisioning without referencing any specific provider's infrastructure project.
/// </summary>
/// <remarks>
/// There is deliberately no <c>ApplyAsync</c> on this interface. A <see cref="ProvisioningPlan"/> produced
/// by <see cref="PlanAsync"/> is applied by <c>IPlanExecutor</c>, not by the provisioner itself. This keeps
/// two project-wide invariants true by construction rather than by convention: nothing beneath
/// <c>IPlanExecutor</c> ever throws for a capability reason, and there is no force flag anywhere in the
/// system. A provisioner that cannot perform some part of a request does not except — it returns a plan
/// whose relevant stage is marked blocked, leaving <c>IPlanExecutor</c> and the caller to decide what to do
/// about it.
/// </remarks>
public interface IProvisioner
{
    /// <summary>Stable identifier for this provisioner, e.g. <c>"hetzner"</c> or <c>"digitalocean"</c>.</summary>
    string ProvisionerId { get; }

    /// <summary>The capabilities this provisioner actually implements.</summary>
    ProvisioningCapabilities Capabilities { get; }

    /// <summary>
    /// Computes a plan for satisfying <paramref name="request"/>, without creating or changing anything at
    /// the provider. The plan is later applied by <c>IPlanExecutor</c>, never by this method.
    /// </summary>
    Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default);

    /// <summary>
    /// Re-reads a previously created resource from wherever this provisioner recorded it, and returns the
    /// <see cref="ProvisionedResource"/> as that record currently describes it. Returns
    /// <see langword="null"/> if the record is gone or no longer identifies a Servyx-managed resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>"Refreshed" means re-read, not observed.</strong> How current the answer is depends entirely
    /// on what the provisioner is re-reading. A registry-backed adapter inspects the daemon or provider API,
    /// which does maintain live state, so its answer reflects the resource as it is now. A marker-backed
    /// adapter re-reads a file that was written once at creation and is never updated afterwards, so its
    /// answer replays creation-time facts: it will happily describe an install whose process died months
    /// ago, and its <see cref="ResourceFacts.CreatedAt"/> is the only timestamp in it that was ever true.
    /// Callers must not read a non-null result as "this workload is up".
    /// </para>
    /// <para>
    /// <strong>Liveness is not provisioning's job.</strong> Whether a server is actually running, reachable,
    /// or healthy is answered by the control plane over an <c>ITransport</c> — the machinery that exists to
    /// observe live workloads — not by re-reading a provisioner's record of having created one. Provisioning
    /// deliberately stops at the point it can hand back a <see cref="Transport.TargetDescriptor"/>; asking
    /// it for liveness would be asking it to duplicate, less well, a job something else already does.
    /// </para>
    /// </remarks>
    Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default);

    /// <summary>
    /// Finds provider resources within <paramref name="scope"/> that were created by this provisioner (per
    /// its tagging convention) so they can be reconciled against Servyx's local records — surfacing
    /// resources Servyx has lost track of (see <see cref="ResourceLifecycleState.Intended"/>) so they are
    /// not left billing indefinitely. Requires <see cref="ProvisioningCapabilities.TagQuery"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> states the search space the sweep will cover, so a caller can see that
    /// before running it (see the remarks on <see cref="OrphanScope"/>). A provisioner handed a scope naming
    /// another provisioner — or a search-space shape it does not implement — reports no handles rather than
    /// substituting a scope of its own choosing. Note that how strong a negative result is depends on the
    /// adapter's shape: see the remarks on <see cref="ProvisioningCapabilities.TagQuery"/>.
    /// </remarks>
    Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default);

    /// <summary>
    /// Builds the mutating operation that would satisfy <paramref name="request"/>, without creating or
    /// changing anything at the provider. The returned <see cref="IProvisioningOperation"/> is later driven
    /// by <c>Servyx.Application</c>'s plan executor (see the remarks on this interface), never by this
    /// method — calling it commits nothing on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Takes the request, not the <see cref="ProvisioningPlan"/> <see cref="PlanAsync"/> computed
    /// from it.</strong> A plan is a description for display — stages, a cost estimate, an expiry — not
    /// enough state to rebuild the provider-specific mutation each concrete provisioner already knows how to
    /// build from a request (e.g. a Docker container spec, an SSH install spec, a droplet spec). Requiring
    /// the plan back would mean smuggling that provider-specific state into a project-wide domain type,
    /// which is exactly the coupling this interface exists to avoid. A caller that must be sure it is still
    /// acting on the plan it showed a user compares <see cref="ProvisioningPlan.PlanHash"/> itself before
    /// calling this method; this interface makes no staleness promise on its own.
    /// </para>
    /// <para>
    /// This is the seam <c>Servyx.Application</c> uses to reach a provider-specific
    /// <see cref="IProvisioningOperation"/> through <see cref="IProvisioner"/> alone, without a reference to
    /// whichever infrastructure project defines that provider's spec type. Every implementation is expected
    /// to be a thin translation from <paramref name="request"/> into that provider-specific spec, handed to
    /// the same operation constructor the provisioner's own typed overload uses.
    /// </para>
    /// </remarks>
    /// <param name="request">The request to build the operation for.</param>
    IProvisioningOperation CreateOperation(ProvisioningRequest request);
}
