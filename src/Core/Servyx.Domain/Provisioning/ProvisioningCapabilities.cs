namespace Servyx.Domain.Provisioning;

/// <summary>
/// A single capability a provisioning adapter may hold over cloud/hosting infrastructure. Each bit
/// represents one concrete thing the adapter can verifiably do to a provider's resources.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="Transport.TransportCapabilities"/>, which answers "what
/// can this pipe do" (e.g. exec a command, stream a file), and from <see cref="Control.ControlCapability"/>,
/// which answers "what may Servyx do to this server" once it is reachable (e.g. stop it, write its
/// config). <see cref="ProvisioningCapabilities"/> answers a third, earlier question: "what can this
/// adapter do to infrastructure" — i.e. before a server exists, or after it should cease to exist. None of
/// the three enums imply or subsume one another; a server can be fully controllable via
/// <see cref="Control.ControlCapability"/> while its underlying infrastructure was hand-provisioned by a
/// human and is entirely outside any <see cref="IProvisioner"/>'s reach.
/// </remarks>
[Flags]
public enum ProvisioningCapabilities
{
    /// <summary>No capabilities are held.</summary>
    None = 0,

    /// <summary>The adapter can create new provider resources (e.g. a virtual machine).</summary>
    Create = 1 << 0,

    /// <summary>
    /// The adapter can permanently destroy the <em>handle</em> to a provider resource it created — the
    /// object the provider knows about and, where applicable, bills for. It does not promise that every byte
    /// the workload wrote is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How much disappears with the handle is a property of the shape, and callers must not generalise from
    /// one to the other. Removing a container also removes its writable layer, so anything written inside
    /// the container and not onto a mount goes with it. Removing a marker file removes only Servyx's record
    /// of the install: the installed payload — binaries, configuration, saves — is still sitting on the host
    /// filesystem afterwards, and reclaiming that disk space is a separate, explicitly-confirmed operation.
    /// </para>
    /// <para>
    /// <strong>Persistent user data is never destroyed by either adapter, deliberately.</strong> The Docker
    /// adapter removes containers with <c>RemoveVolumes: false</c>; the SSH process adapter removes the
    /// marker and never touches <c>dataDir</c>. A provisioner that deleted a save directory as a side effect
    /// of destroying a workload would be the single most destructive thing in this codebase, so neither can
    /// do it — including when the caller would rather it did. Deleting user data is not a provisioning verb.
    /// </para>
    /// </remarks>
    Destroy = 1 << 1,

    /// <summary>The adapter can resize an existing resource (e.g. change a VM's size/plan).</summary>
    Resize = 1 << 2,

    /// <summary>The adapter can create and restore point-in-time snapshots of a resource.</summary>
    Snapshot = 1 << 3,

    /// <summary>The adapter can allocate and attach a static (non-ephemeral) network address.</summary>
    StaticAddress = 1 << 4,

    /// <summary>The adapter can create and manage firewall/security-group rules for a resource.</summary>
    FirewallRules = 1 << 5,

    /// <summary>The adapter can produce a <see cref="CostEstimate"/> for a planned or existing resource.</summary>
    EstimatesCost = 1 << 6,

    /// <summary>
    /// The adapter can list provider resources by tag, independent of Servyx's own records. This is the
    /// capability <see cref="IProvisioner.ReconcileAsync"/> depends on. Without it, resources that Servyx
    /// created but then lost track of (e.g. after a crash between the provider API call and the local
    /// write) cannot be found and swept as orphans — they simply keep billing forever with no way to
    /// discover them from Servyx's side. An adapter lacking this bit must never be trusted with billable
    /// resources: there would be no way to prove, after the fact, that everything it created has also been
    /// accounted for or destroyed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The bit says discovery-by-tag works; it does not say how strong the guarantee is.</strong>
    /// That varies by shape, materially, and a caller reasoning about worst cases must ask which shape it is
    /// holding rather than assume the stronger reading.
    /// </para>
    /// <para>
    /// <em>Registry-backed (container-style).</em> Identity is applied by the same atomic call that brings
    /// the resource into existence — <c>docker create</c> takes the labels — and lives inside the daemon
    /// alongside the resource itself. There is no window in which a created container exists untagged, and
    /// no way to edit its identity without going through the daemon. A sweep that finds nothing is therefore
    /// strong evidence that nothing exists.
    /// </para>
    /// <para>
    /// <em>Marker-backed (filesystem-style).</em> The marker is a separate artifact from the installed
    /// payload: a small file written next to (not inside) the thing it describes. It can be deleted by a
    /// tidy-up script, hand-edited, restored from a stale backup, or left behind after the payload it names
    /// is gone — none of which the adapter can prevent or detect, because nothing on the host enforces the
    /// relationship. The adapter buys back what it can by ordering (the marker is written <em>before</em>
    /// any install step, so a half-finished install is still discoverable), but that closes the creation
    /// window only; it cannot make the marker and the payload fail together afterwards. A sweep that finds
    /// nothing means no marker was found, which is weaker than "nothing exists" — and, symmetrically, a
    /// marker found is not proof the payload is still installed.
    /// </para>
    /// </remarks>
    TagQuery = 1 << 7,

    /// <summary>
    /// The adapter can bring an existing resource to a new desired state <em>without replacing it</em>: the
    /// resource keeps its provider identity, its <see cref="ResourceHandle.ProviderResourceId"/> stays
    /// valid, and the workload is not torn down. This is the bit that says an update is a mutation, not a
    /// replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What it guarantees is narrow, and the narrowness is the point.</strong> It does not say
    /// <em>which</em> properties are mutable in place — that varies wildly by provider and is answered by
    /// the <see cref="PlannedChange.RequiresRecreate"/> flags on an actual <see cref="UpdatePlan"/>, against
    /// a specific request, rather than by a bit. It says only that in-place mutation is a thing this adapter
    /// can do at all, so a caller holding the bit knows an <see cref="UpdateStrategy.InPlace"/> plan is
    /// reachable from it. An adapter for which every conceivable change forces a replacement must not set
    /// this bit merely because "updating" is something it can be asked to do.
    /// </para>
    /// <para>
    /// <em>Registry-backed (container-style).</em> A container's image, published ports, and labels are all
    /// fixed at create time by the Docker Engine's own model; there is no engine call that edits them. The
    /// Docker adapter therefore does <strong>not</strong> hold this bit, and could not honestly be given it
    /// without the engine growing an API it does not have.
    /// </para>
    /// <para>
    /// <em>API-backed (cloud VM-style).</em> A provider that exposes "resize this instance" or "attach this
    /// address" endpoints operates on the resource in place and would hold this bit — note that
    /// <see cref="Resize"/> is a specific such operation, and holding <see cref="Resize"/> without this bit
    /// is coherent: it says one particular mutation exists, not that update planning is implemented.
    /// </para>
    /// </remarks>
    UpdateInPlace = 1 << 8,

    /// <summary>
    /// The adapter can bring an existing resource to a new desired state by <em>replacing</em> it: stopping
    /// and removing the current resource and creating a new one in its place. The workload is interrupted
    /// and the resulting <see cref="ResourceHandle.ProviderResourceId"/> is a different one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This bit is not a weaker <see cref="UpdateInPlace"/>; it is a louder one.</strong> The two
    /// are independent and an adapter may hold either, both, or neither. Holding this one is an admission
    /// that reaching the desired state costs downtime and a new provider identity, which is information the
    /// person approving the plan needs before they approve it — not an implementation detail to be smoothed
    /// over by presenting both shapes as "update".
    /// </para>
    /// <para>
    /// <strong>It says nothing about data.</strong> Whether the replacement comes up attached to the same
    /// state is a per-plan finding, asserted as a <see cref="DataImpact"/> on each <see cref="UpdatePlan"/>
    /// after the adapter has looked at the live resource's actual mounts. A caller must never read this bit
    /// as "recreates are safe": see the same distinction drawn on <see cref="Destroy"/>, where removing a
    /// container's handle also removes its writable layer while leaving its mounts alone.
    /// </para>
    /// <para>
    /// <em>Registry-backed (container-style).</em> This is the Docker adapter's only update shape, so it
    /// holds this bit and not <see cref="UpdateInPlace"/>.
    /// </para>
    /// </remarks>
    RecreateToUpdate = 1 << 9,

    /// <summary>
    /// The adapter can compare a live resource against the <see cref="ResourceHandle"/> Servyx recorded for
    /// it and report the differences — i.e. it implements
    /// <see cref="IMaintainer.DetectDriftAsync(ResourceHandle, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>How strong a "matches" answer is depends on the shape, exactly as it does for
    /// <see cref="TagQuery"/>.</strong> A registry-backed adapter reads the resource's live properties out
    /// of the daemon, so a match means the resource as it is right now agrees with the record. A
    /// marker-backed adapter would be comparing a record against a record, which can only prove the file
    /// has not been edited — so an adapter of that shape should not hold this bit rather than hold it and
    /// mean something weaker by it.
    /// </para>
    /// <para>
    /// The bit says the comparison is implemented; it does not enumerate which properties are compared.
    /// That is per-adapter and is visible in the named <see cref="DriftDivergence.Aspect"/> values a check
    /// actually returns.
    /// </para>
    /// </remarks>
    DetectDrift = 1 << 10,
}
