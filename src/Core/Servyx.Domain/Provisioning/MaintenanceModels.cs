using System.Globalization;

namespace Servyx.Domain.Provisioning;

/// <summary>
/// What an <see cref="UpdatePlan"/> would do to the resource's <em>persistent</em> data — the mounts,
/// volumes, or on-disk payload a workload's state actually lives in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is deliberately no zero member.</strong> <c>default(DataImpact)</c> is not a valid value,
/// and <see cref="UpdatePlan"/> rejects it. That is the whole reason this enum starts at one: a data-impact
/// answer must be something an adapter looked at the live resource and asserted, never something a
/// forgotten constructor argument or a zero-initialised field produced. The failure mode being designed out
/// is a plan that says <see cref="Preserved"/> because nobody set it — which is indistinguishable, to every
/// caller and every operator reading a preview, from a plan that says <see cref="Preserved"/> because the
/// adapter enumerated the live mounts and confirmed each one is carried over.
/// </para>
/// <para>
/// <strong>This describes persistent data only.</strong> It says nothing about availability: every value
/// here is compatible with the workload being stopped for the duration of the update. Read the plan's
/// <see cref="UpdatePlan.Stages"/> for that.
/// </para>
/// </remarks>
public enum DataImpact
{
    /// <summary>
    /// Every store the resource's persistent data lives in survives the update, and the resource that
    /// exists afterwards is attached to the same stores it was attached to before.
    /// </summary>
    /// <remarks>
    /// An adapter may only assert this after enumerating the live resource's data stores and confirming
    /// each one is carried across. "The adapter does not delete data" is not sufficient evidence: a
    /// replacement resource that silently comes up attached to nothing has preserved the bytes and lost the
    /// state, which is the same outcome to whoever was playing on the server.
    /// </remarks>
    Preserved = 1,

    /// <summary>
    /// The update may separate the workload from some of its state, without the adapter deleting anything.
    /// </summary>
    /// <remarks>
    /// This is the honest answer whenever the adapter cannot demonstrate <see cref="Preserved"/> — for
    /// example a container whose state is in its writable layer rather than on a mount (the layer goes when
    /// the container is replaced), or a mount present on the live resource that the desired state does not
    /// carry over (the bytes remain on the host, but nothing references them and the workload will come up
    /// on fresh state). It is not a hedge: it is the difference between "the data survives" and "the data
    /// survives and is still attached", and an operator approving a recreate needs to be told which one
    /// they are getting.
    /// </remarks>
    AtRisk = 2,

    /// <summary>
    /// The update deletes a store the resource's persistent data lives in. Approving such a plan is
    /// approving data loss.
    /// </summary>
    /// <remarks>
    /// No adapter in Servyx asserts this today, and that is a property of the adapters rather than an
    /// oversight — see the remarks on <see cref="ProvisioningCapabilities.Destroy"/>, where "deleting user
    /// data is not a provisioning verb" is stated as a project-wide rule. The value exists so that an
    /// adapter which one day genuinely does delete a volume has a way to say so, rather than being forced
    /// to describe the deletion as <see cref="AtRisk"/> and understate it.
    /// </remarks>
    Destroyed = 3,
}

/// <summary>
/// How an <see cref="UpdatePlan"/> would reach the desired state.
/// </summary>
/// <remarks>
/// As with <see cref="DataImpact"/> there is no zero member, so a strategy is always something the adapter
/// decided rather than something a default produced.
/// </remarks>
public enum UpdateStrategy
{
    /// <summary>
    /// The live resource already matches the desired state. A plan with this strategy carries no changes
    /// and no stages: it would do nothing at all.
    /// </summary>
    NoChangeRequired = 1,

    /// <summary>
    /// The live resource can be mutated into the desired state without being replaced, keeping its
    /// provider identity. Requires <see cref="ProvisioningCapabilities.UpdateInPlace"/>.
    /// </summary>
    InPlace = 2,

    /// <summary>
    /// The live resource must be replaced: the existing one is stopped and removed and a new one is created
    /// in its place, which means a new provider identity and an interruption. Requires
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/>.
    /// </summary>
    Recreate = 3,
}

/// <summary>
/// One difference between the live resource and the desired state, as an <see cref="UpdatePlan"/> found it.
/// </summary>
/// <param name="Aspect">
/// What differs, named in the adapter's own vocabulary — e.g. <c>"image"</c>, <c>"ports"</c>, or
/// <c>"label servyx.job-id"</c>. Never blank.
/// </param>
/// <param name="Current">The value observed on the live resource, or <see langword="null"/> if it has none.</param>
/// <param name="Desired">The value the request asks for, or <see langword="null"/> if the request drops it.</param>
/// <param name="RequiresRecreate">
/// Whether this single change is enough on its own to force <see cref="UpdateStrategy.Recreate"/>. An
/// <see cref="UpdatePlan"/> carrying any change with this set may not claim
/// <see cref="UpdateStrategy.InPlace"/>.
/// </param>
public sealed record PlannedChange(string Aspect, string? Current, string? Desired, bool RequiresRecreate)
{
    /// <summary>What differs. Never blank.</summary>
    public string Aspect { get; } = ValidateAspect(Aspect);

    /// <summary>A one-line rendering of the change, suitable for a preview.</summary>
    public string Description => string.Create(
        CultureInfo.InvariantCulture,
        $"{Aspect}: {Render(Current)} -> {Render(Desired)}");

    private static string Render(string? value) => value is null ? "(none)" : $"'{value}'";

    private static string ValidateAspect(string aspect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspect);
        return aspect;
    }
}

/// <summary>
/// A plan describing what would change to bring an existing resource to a desired state. Like a
/// <see cref="ProvisioningPlan"/>, it is inert: producing one mutates nothing, and this codebase has no
/// executor for it — see the remarks on <see cref="IMaintainer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The invariants are enforced here rather than trusted per adapter.</strong> A plan cannot claim
/// <see cref="UpdateStrategy.NoChangeRequired"/> while carrying changes, cannot claim
/// <see cref="UpdateStrategy.InPlace"/> while carrying a change that
/// <see cref="PlannedChange.RequiresRecreate"/>, and cannot be constructed with an unset
/// <see cref="DataImpact"/>. Those are exactly the three ways a plan could mislead the person approving it,
/// so none of them is expressible.
/// </para>
/// </remarks>
public sealed record UpdatePlan
{
    /// <summary>Creates an update plan, rejecting any combination that would misdescribe itself.</summary>
    /// <param name="planId">Stable identifier for this plan.</param>
    /// <param name="planHash">
    /// A content hash over both the observed live state and the desired state, so a caller can tell whether
    /// the plan it showed a user is still the plan those inputs produce.
    /// </param>
    /// <param name="provisionerId">The provisioner whose resource this plan describes.</param>
    /// <param name="strategy">How the plan would reach the desired state.</param>
    /// <param name="dataImpact">
    /// What the plan would do to the resource's persistent data. Must be a defined value; the zero value is
    /// rejected precisely so it cannot arrive by omission.
    /// </param>
    /// <param name="changes">Every difference the plan found. Empty exactly when nothing needs to change.</param>
    /// <param name="stages">The ordered steps that would run. Empty for <see cref="UpdateStrategy.NoChangeRequired"/>.</param>
    /// <param name="expiresAt">When the observed live state this plan was computed against should no longer be trusted.</param>
    /// <exception cref="ArgumentException">An identifier is blank, or the plan would misdescribe itself.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> or <paramref name="dataImpact"/> is not a defined value.</exception>
    public UpdatePlan(
        string planId,
        string planHash,
        string provisionerId,
        UpdateStrategy strategy,
        DataImpact dataImpact,
        IReadOnlyList<PlannedChange> changes,
        IReadOnlyList<ProvisioningStage> stages,
        DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(provisionerId);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(stages);

        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(strategy),
                strategy,
                "An update plan's strategy must be a value the adapter chose. The zero value is not one of them.");
        }

        if (!Enum.IsDefined(dataImpact))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dataImpact),
                dataImpact,
                "An update plan must state its data impact explicitly. The zero value is rejected so 'Preserved' can never arrive by omission.");
        }

        if (strategy == UpdateStrategy.NoChangeRequired && changes.Count > 0)
        {
            throw new ArgumentException(
                "A plan that reports NoChangeRequired cannot also carry changes.",
                nameof(strategy));
        }

        if (strategy != UpdateStrategy.NoChangeRequired && changes.Count == 0)
        {
            throw new ArgumentException(
                "A plan that would do something must say what differs; report NoChangeRequired instead.",
                nameof(changes));
        }

        if (strategy == UpdateStrategy.InPlace && changes.Any(c => c.RequiresRecreate))
        {
            throw new ArgumentException(
                "A plan carrying a change that requires a recreate cannot describe itself as an in-place update.",
                nameof(strategy));
        }

        if (strategy == UpdateStrategy.NoChangeRequired && stages.Count > 0)
        {
            throw new ArgumentException(
                "A plan that would do nothing cannot carry stages that would run.",
                nameof(stages));
        }

        PlanId = planId;
        PlanHash = planHash;
        ProvisionerId = provisionerId;
        Strategy = strategy;
        DataImpact = dataImpact;
        Changes = changes;
        Stages = stages;
        ExpiresAt = expiresAt;
    }

    /// <summary>Stable identifier for this plan.</summary>
    public string PlanId { get; }

    /// <summary>A content hash over the observed live state and the desired state.</summary>
    public string PlanHash { get; }

    /// <summary>The provisioner whose resource this plan describes.</summary>
    public string ProvisionerId { get; }

    /// <summary>How the plan would reach the desired state.</summary>
    public UpdateStrategy Strategy { get; }

    /// <summary>What the plan would do to the resource's persistent data. Always deliberately asserted.</summary>
    public DataImpact DataImpact { get; }

    /// <summary>Every difference the plan found between the live resource and the desired state.</summary>
    public IReadOnlyList<PlannedChange> Changes { get; }

    /// <summary>The ordered steps that would run. Nothing here has been applied.</summary>
    public IReadOnlyList<ProvisioningStage> Stages { get; }

    /// <summary>When the observed live state this plan was computed against should no longer be trusted.</summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>
/// One way a live resource fails to match what Servyx recorded when it provisioned it.
/// </summary>
/// <param name="Aspect">What diverged, e.g. <c>"image"</c> or <c>"label servyx.job-id"</c>. Never blank.</param>
/// <param name="Expected">
/// What Servyx's record says the value should be, or <see langword="null"/> when the record holds no
/// expectation for this aspect at all. A null expectation is still reported as a divergence: a check that
/// cannot prove a match must not claim one.
/// </param>
/// <param name="Found">The value observed on the live resource, or <see langword="null"/> if it has none.</param>
public sealed record DriftDivergence(string Aspect, string? Expected, string? Found)
{
    /// <summary>What diverged. Never blank.</summary>
    public string Aspect { get; } = ValidateAspect(Aspect);

    /// <summary>
    /// A one-line rendering, e.g. <c>"image: expected nginx:1.25, found nginx:1.27"</c>.
    /// </summary>
    public string Description => Expected is null
        ? string.Create(CultureInfo.InvariantCulture, $"{Aspect}: Servyx recorded no expected value, found {Render(Found)}")
        : string.Create(CultureInfo.InvariantCulture, $"{Aspect}: expected {Expected}, found {Render(Found)}");

    private static string Render(string? value) => value ?? "nothing";

    private static string ValidateAspect(string aspect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspect);
        return aspect;
    }
}

/// <summary>
/// The answer to "does this resource still match what Servyx provisioned?".
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Matches"/> is computed, never asserted.</strong> It is defined as "no divergences
/// were found", so there is no way for an adapter to report a match while also reporting differences, and
/// no way for it to report a match it did not do the work to establish — a check that could not read the
/// live resource reports that as a divergence (see <see cref="DriftDivergence.Expected"/>) rather than
/// falling through to <see langword="true"/>.
/// </para>
/// <para>
/// A drift check is a read. Nothing here is a decision about what to do next: an operator seeing drift may
/// legitimately want to adopt it rather than undo it, and this type deliberately does not prejudge that.
/// </para>
/// </remarks>
public sealed record DriftResult
{
    /// <summary>Creates a drift result for <paramref name="handle"/>.</summary>
    /// <param name="handle">The recorded handle the live resource was compared against.</param>
    /// <param name="divergences">Every difference found. Empty means, and is the only thing that means, a match.</param>
    public DriftResult(ResourceHandle handle, IReadOnlyList<DriftDivergence> divergences)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(divergences);

        Handle = handle;
        Divergences = divergences;
    }

    /// <summary>The recorded handle the live resource was compared against.</summary>
    public ResourceHandle Handle { get; }

    /// <summary>Every difference found between the live resource and the recorded handle.</summary>
    public IReadOnlyList<DriftDivergence> Divergences { get; }

    /// <summary>Whether the live resource still matches the record. True exactly when nothing diverged.</summary>
    public bool Matches => Divergences.Count == 0;

    /// <summary>A one-line summary, listing each divergence by name.</summary>
    public string Summary => Matches
        ? string.Create(CultureInfo.InvariantCulture, $"{Handle.ProviderResourceId} matches the resource Servyx provisioned.")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Handle.ProviderResourceId} has drifted: {string.Join("; ", Divergences.Select(d => d.Description))}");
}
