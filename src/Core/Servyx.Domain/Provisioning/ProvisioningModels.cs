namespace Servyx.Domain.Provisioning;

/// <summary>
/// A request to plan provisioning for a game deployment, before any provider resource is created.
/// </summary>
/// <param name="GameDefinitionId">The game definition the resulting server will run.</param>
/// <param name="DeploymentProfileId">The deployment profile (e.g. Docker Compose profile) to provision for.</param>
/// <param name="ConnectorId">
/// The connector to attach the provisioned resource to once created, or <see langword="null"/> if a
/// connector should be created as part of the plan.
/// </param>
/// <param name="Parameters">Free-form provisioning parameters (e.g. machine size choice), keyed by name.</param>
public sealed record ProvisioningRequest(
    string GameDefinitionId,
    string DeploymentProfileId,
    string? ConnectorId,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// The search space an <see cref="IProvisioner.ReconcileAsync"/> sweep will cover: always one provisioner,
/// and beyond that whichever shape of search space that provisioner's resources actually live in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a closed hierarchy rather than a flat record.</strong> The original shape was
/// <c>OrphanScope(string ProvisionerId, string? Region)</c>, which could express a cloud provider's search
/// space and nothing else. The SSH process adapter's search space is a directory of marker files, so it had
/// no way to say what it would sweep and had to hoist its marker root into constructor state instead — with
/// the result that a caller holding an <see cref="IProvisioner"/> and an <c>OrphanScope</c> could not tell
/// what a sweep was about to cover. That is the defect this type exists to fix, and it is a legibility
/// defect, so the fix has to be legible: <c>new OrphanScope.MarkerDirectory(id, "/var/lib/servyx/instances")</c>
/// states the covered space in the type, where a reviewer and the compiler can both see it. An open
/// <c>Parameters</c> bag would have carried the same value while leaving the caller to guess which magic
/// key an adapter honours, which is the original defect in a weaker form.
/// </para>
/// <para>
/// <strong>Why a closed set is acceptable here.</strong> Provisioning shapes are a stable, low-cardinality
/// taxonomy — a resource is created inside a daemon/API that can be queried, or it is recorded as files in
/// a directory that can be listed — and this codebase already models exactly this kind of taxonomy as a
/// closed hierarchy in <c>Servyx.Domain</c> (see <c>SurfaceLocator</c>, <c>StopStage</c>, and the
/// <c>DetectSpec</c> design). Adding a genuinely new shape is a deliberate act that should be reviewed
/// against the taxonomy, not something an adapter slips in behind a string key.
/// </para>
/// <para>
/// <strong>An adapter may refuse a shape it cannot serve.</strong> Because the shape is visible in the
/// type, an adapter handed a search space it does not implement can say so by returning no handles, exactly
/// as it does for a scope naming a different provisioner. Before this change such a request was
/// inexpressible, so it could only be silently reinterpreted as "sweep everything".
/// </para>
/// </remarks>
public abstract record OrphanScope
{
    private OrphanScope(string provisionerId, string? region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);

        ProvisionerId = provisionerId;
        Region = region;
    }

    /// <summary>The provisioner to sweep. A provisioner handed another provisioner's scope reports nothing.</summary>
    public string ProvisionerId { get; }

    /// <summary>
    /// The provider region/location to restrict the sweep to, or <see langword="null"/> to sweep every
    /// region. Meaningful only for region-scoped providers; the Docker and SSH adapters both act on a single
    /// unregioned host and neither reads it.
    /// </summary>
    public string? Region { get; }

    /// <summary>
    /// Sweep everything the provider itself reports for this provisioner — the shape for an adapter whose
    /// resources are registered with a daemon or API that can be queried by tag (a Docker Engine, a cloud
    /// provider's instance API).
    /// </summary>
    /// <remarks>
    /// The search space is the provider's own inventory, so there is nothing to describe beyond which
    /// provisioner is asking and, for a region-scoped provider, which region.
    /// </remarks>
    public sealed record ProviderWide : OrphanScope
    {
        /// <summary>Creates a provider-wide scope.</summary>
        /// <param name="provisionerId">The provisioner to sweep.</param>
        /// <param name="region">The region to restrict the sweep to, or <see langword="null"/> for all regions.</param>
        public ProviderWide(string provisionerId, string? region = null)
            : base(provisionerId, region)
        {
        }
    }

    /// <summary>
    /// Sweep a directory of marker files — the shape for an adapter whose resources have no registry, so
    /// Servyx supplies the registry itself as files on the target host.
    /// </summary>
    /// <remarks>
    /// The marker root is part of the scope rather than adapter-internal state precisely so a caller can see
    /// which directory a sweep will cover before running it, and so two sweeps of the same host can cover
    /// different roots without needing two differently-constructed provisioners. The path's syntactic rules
    /// belong to the adapter, not here: <c>Servyx.Domain</c> knows nothing about POSIX paths, so this type
    /// only guarantees the value is non-blank and leaves the adapter to reject a root it cannot use.
    /// </remarks>
    public sealed record MarkerDirectory : OrphanScope
    {
        /// <summary>Creates a marker-directory scope.</summary>
        /// <param name="provisionerId">The provisioner to sweep.</param>
        /// <param name="markerRoot">The directory holding the marker files to enumerate.</param>
        /// <param name="region">The region to restrict the sweep to, or <see langword="null"/> for all regions.</param>
        /// <exception cref="ArgumentException"><paramref name="markerRoot"/> is null, empty, or whitespace.</exception>
        public MarkerDirectory(string provisionerId, string markerRoot, string? region = null)
            : base(provisionerId, region)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(markerRoot);

            MarkerRoot = markerRoot;
        }

        /// <summary>The directory whose marker files the sweep will enumerate.</summary>
        public string MarkerRoot { get; }
    }
}

/// <summary>
/// A single step of a <see cref="ProvisioningPlan"/>.
/// </summary>
/// <param name="StageId">Stable identifier for this stage within its plan.</param>
/// <param name="ProvisionerId">The provisioner responsible for executing this stage.</param>
/// <param name="Description">Human-readable description of what this stage does, shown to the user before approval.</param>
public sealed record ProvisioningStage(string StageId, string ProvisionerId, string Description);

/// <summary>
/// A plan describing the stages required to satisfy a <see cref="ProvisioningRequest"/>, along with its
/// estimated cost. A plan is inert — nothing in it has been applied yet; application flows through
/// <c>IPlanExecutor</c> (see the remarks on <see cref="IProvisioner"/>).
/// </summary>
/// <param name="PlanId">Stable identifier for this plan.</param>
/// <param name="PlanHash">
/// A content hash of the plan's stages and parameters, used to detect whether the plan is still valid for
/// the inputs it was computed from before it is executed.
/// </param>
/// <param name="Stages">The ordered stages that make up this plan.</param>
/// <param name="EstimatedCost">The best available cost estimate for the plan as a whole.</param>
/// <param name="ExpiresAt">When this plan's pricing/availability figures should no longer be trusted.</param>
public sealed record ProvisioningPlan(
    string PlanId,
    string PlanHash,
    IReadOnlyList<ProvisioningStage> Stages,
    CostEstimate EstimatedCost,
    DateTimeOffset ExpiresAt);

/// <summary>
/// A resource that has been created by a provisioner and handed off to the rest of Servyx.
/// </summary>
/// <remarks>
/// This type marks the architectural boundary of provisioning: a provisioner's job is finished the moment
/// it can hand back a <see cref="Transport.TargetDescriptor"/> — from that point on, the existing transport
/// machinery takes over completely unchanged, exactly as if the target had been hand-configured. Note that
/// provider-specific state lives on <see cref="Handle"/> rather than being folded into
/// <see cref="Connectors.ConnectorDescriptor"/>. <see cref="Connectors.ConnectorDescriptor"/> is
/// deliberately closed, and its fields feed <c>ConnectorKey</c> pooling identity; adding provider metadata
/// (region, tags, etc.) to it would change pool identity whenever that metadata changed, even though such
/// edits are unrelated to the connection itself.
/// <para>
/// <strong>The hand-off is a question, not a value.</strong> <see cref="Reachability"/> is a closed
/// hierarchy rather than a <see cref="Transport.TargetDescriptor"/>, because not every shape of resource
/// terminates in something a transport can address — a managed container service exposes no daemon, no
/// sshd, and is not the Servyx host. The full argument, including why this is not a nullable
/// <c>TargetDescriptor?</c>, is on <see cref="ResourceReachability"/>. Every adapter that <em>does</em>
/// have a target still passes one: the <see cref="Transport.TargetDescriptor"/> overload of the
/// constructor is the unchanged path, and it wraps rather than validates.
/// </para>
/// </remarks>
/// <param name="Handle">The provider-specific reference to the created resource.</param>
/// <param name="ConnectorId">The connector through which the resource is now reachable.</param>
/// <param name="Reachability">
/// Whether a transport can address the resource, and which one — see <see cref="ResourceReachability"/>.
/// </param>
/// <param name="Facts">Facts observed about the resource at creation time.</param>
public sealed record ProvisionedResource(
    ResourceHandle Handle,
    string ConnectorId,
    ResourceReachability Reachability,
    ResourceFacts Facts)
{
    /// <summary>
    /// Creates a resource that <em>is</em> reachable, through <paramref name="Target"/>.
    /// </summary>
    /// <remarks>
    /// The convenience shape for the six adapters whose provider genuinely terminates in an addressable
    /// thing. It is not a way to avoid the question: it answers it, with
    /// <see cref="ResourceReachability.ViaTransport"/>. There is deliberately no counterpart overload
    /// taking a bare reason string — declaring a resource unreachable should read as the deliberate act it
    /// is, spelled <c>new ResourceReachability.NoTransport("…")</c> at the call site.
    /// </remarks>
    /// <param name="Handle">The provider-specific reference to the created resource.</param>
    /// <param name="ConnectorId">The connector through which the resource is now reachable.</param>
    /// <param name="Target">The transport target the rest of Servyx should use to reach the resource.</param>
    /// <param name="Facts">Facts observed about the resource at creation time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="Target"/> is null.</exception>
    public ProvisionedResource(
        ResourceHandle Handle,
        string ConnectorId,
        Transport.TargetDescriptor Target,
        ResourceFacts Facts)
        : this(Handle, ConnectorId, new ResourceReachability.ViaTransport(Target), Facts)
    {
    }

    /// <summary>
    /// The transport target for a resource the caller has already established is reachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accessor for code whose surrounding context guarantees a target exists — a test asserting an
    /// adapter's hand-off, or a branch that has already matched
    /// <see cref="ResourceReachability.ViaTransport"/>. It <em>throws</em> rather than returning null
    /// precisely so it cannot become the quiet way to reintroduce the bug this shape removes: there is no
    /// value it could return for an unreachable resource that would not be a fabrication.
    /// </para>
    /// <para>
    /// Code that does not already know the answer must not call this. It should match on
    /// <see cref="Reachability"/> and handle both cases, which is the whole point of the type.
    /// </para>
    /// </remarks>
    /// <returns>The transport target.</returns>
    /// <exception cref="InvalidOperationException">
    /// The resource is not reachable by any transport. The message carries the adapter's own reason.
    /// </exception>
    public Transport.TargetDescriptor RequireTarget() => Reachability switch
    {
        ResourceReachability.ViaTransport reachable => reachable.Target,
        ResourceReachability.NoTransport unreachable => throw new InvalidOperationException(
            $"Resource '{Handle.ProviderResourceId}' (provisioner '{Handle.ProvisionerId}') is not reachable "
            + $"by any transport, so it has no target descriptor: {unreachable.Reason}"),
        _ => throw new InvalidOperationException(
            $"Unhandled {nameof(ResourceReachability)} shape '{Reachability.GetType().Name}'."),
    };

    /// <summary>
    /// The transport target if the resource is reachable, or <see langword="null"/> if it is not.
    /// </summary>
    /// <remarks>
    /// For rendering and logging, where "unreachable" is a value to display rather than a branch to take.
    /// Deliberately a method rather than a property: a property named <c>Target</c> is exactly the shape
    /// this type replaced, and a caller reaching for one should feel the difference.
    /// </remarks>
    /// <returns>The transport target, or <see langword="null"/> when no transport can address the resource.</returns>
    public Transport.TargetDescriptor? TargetOrNull() =>
        Reachability is ResourceReachability.ViaTransport reachable ? reachable.Target : null;
}
