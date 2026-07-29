namespace Servyx.Domain.Provisioning;

/// <summary>
/// A single, already-decided provider mutation that a plan-execution layer may carry out: create the
/// resource, or undo a partial creation. It is the narrow seam between <c>Servyx.Application</c>'s
/// executor and whichever infrastructure project actually talks to a provider.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this lives in <c>Servyx.Domain</c> and not <c>Servyx.Application</c>.</strong> Every
/// infrastructure project in this solution (<c>Servyx.Infrastructure.Docker</c>,
/// <c>.Ssh</c>, <c>.Persistence</c>) references <c>Servyx.Domain</c> only — never
/// <c>Servyx.Application</c> — precisely so <c>Servyx.Application</c> can consume them through DI
/// without a reference cycle. An abstraction that infrastructure must <em>implement</em> therefore
/// cannot be owned by <c>Servyx.Application</c>; it has to sit in <c>Servyx.Domain</c> alongside
/// <see cref="IProvisioner"/> and <c>ITransport</c>, which are shaped the same way for the same reason.
/// </para>
/// <para>
/// <strong>Why this is not <c>ApplyAsync</c> on <see cref="IProvisioner"/>.</strong> See the remarks on
/// <see cref="IProvisioner"/>: a provisioner describes and reads, it never applies. Application belongs
/// to a plan executor, so the mutating verb lives on its own interface that the executor drives. Keeping
/// them apart is what makes "a provisioner cannot mutate anything" checkable by looking at the type
/// rather than by trusting a convention.
/// </para>
/// </remarks>
public interface IProvisioningOperation
{
    /// <summary>The <see cref="IProvisioner.ProvisionerId"/> whose resources this operation acts on.</summary>
    string ProvisionerId { get; }

    /// <summary>The provider region/location the resource will live in, or <see langword="null"/> if the provider is not region-scoped.</summary>
    string? Region { get; }

    /// <summary>
    /// The tags/labels this operation will attach to the resource it creates. Read by the executor
    /// <em>before</em> <see cref="CreateAsync"/> is called so they can be committed to the write-ahead
    /// ledger: a resource that is created but never acknowledged can then still be found by tag.
    /// </summary>
    IReadOnlyDictionary<string, string> Tags { get; }

    /// <summary>
    /// Creates the resource at the provider and returns it, including the
    /// <see cref="Transport.TargetDescriptor"/> the rest of Servyx uses to reach it. This is the single
    /// billable/mutating call in the flow; the executor guarantees a durable intent record exists before
    /// it is invoked.
    /// </summary>
    Task<ProvisionedResource> CreateAsync(CancellationToken ct = default);

    /// <summary>
    /// Attempts to undo a failed <see cref="CreateAsync"/> by removing whatever partial resource it left
    /// behind. Must be safe to call when nothing was created. Implementations must not swallow provider
    /// errors — the executor decides how a failed compensation is reported, and the ledger row is
    /// deliberately left in <see cref="ResourceLifecycleState.Intended"/> so a later
    /// <see cref="IProvisioner.ReconcileAsync"/> sweep can still find the resource.
    /// </summary>
    Task CompensateAsync(CancellationToken ct = default);
}
