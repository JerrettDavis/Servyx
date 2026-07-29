using System.Diagnostics.CodeAnalysis;

namespace Servyx.Domain.Provisioning;

/// <summary>
/// The one vocabulary of tag/label keys every Servyx provisioning adapter stamps onto the resources it
/// creates, plus the pure helpers that build and read a canonical tag set from it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is in <c>Servyx.Domain</c> and not in each adapter.</strong> Every infrastructure
/// project references <c>Servyx.Domain</c> and never another infrastructure project, so two adapters that
/// each spelled out <c>"servyx.managed"</c> for themselves had no place to share the spelling. That is a
/// silent-failure shape, not a stylistic one: <see cref="IProvisioner.ReconcileAsync"/> finds orphans
/// <em>by tag</em>, so one adapter drifting by a single character stops the other's sweep from ever seeing
/// its resources — with no error raised anywhere, and a provider bill that keeps running. Promoting the
/// keys here makes the two adapters share one definition, so drift becomes a compile-time impossibility
/// rather than a code-review responsibility.
/// </para>
/// <para>
/// <strong>Only the vocabulary is shared, not the storage.</strong> Docker keeps these keys as container
/// labels inside the daemon; the SSH process adapter keeps them as JSON members in a marker file on the
/// host. Those are legitimately different stores with different failure modes (see the remarks on
/// <see cref="ProvisioningCapabilities.TagQuery"/>), and nothing here tries to unify them. What is unified
/// is that a <see cref="ResourceHandle.Tags"/> dictionary means the same thing whichever adapter produced
/// it.
/// </para>
/// <para>
/// <strong>Pure by construction.</strong> <c>Servyx.Domain</c> performs no I/O, and neither does anything
/// here: these are constants and total functions over dictionaries.
/// </para>
/// </remarks>
public static class ServyxTagKeys
{
    /// <summary>
    /// The key namespace Servyx owns. Every key in this class begins with it, and an adapter that needs a
    /// key of its own (e.g. the SSH adapter's <c>servyx.executable</c>) builds it from this prefix so the
    /// namespace stays visibly one namespace.
    /// </summary>
    public const string Prefix = "servyx.";

    /// <summary>Marks a resource as created and owned by Servyx. Always <see cref="ManagedValue"/>.</summary>
    /// <remarks>
    /// This is the key the orphan sweep selects on, and therefore the single most load-bearing string in
    /// provisioning: a resource that does not carry it is, as far as any sweep can tell, someone else's.
    /// </remarks>
    public const string Managed = Prefix + "managed";

    /// <summary>The only value <see cref="Managed"/> is ever set to.</summary>
    public const string ManagedValue = "true";

    /// <summary>Identifies the Servyx server/instance the resource backs.</summary>
    public const string InstanceId = Prefix + "instance-id";

    /// <summary>Identifies the provisioning job that asked for the resource.</summary>
    public const string JobId = Prefix + "job-id";

    /// <summary>Identifies the connector the resource is reachable through, so a refresh can rebuild it.</summary>
    public const string ConnectorId = Prefix + "connector-id";

    /// <summary>
    /// The path a <see cref="Transport.TargetDescriptor"/>'s paths are relative to, recorded on the resource
    /// so a refresh can rebuild an identical descriptor from the resource alone.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>not</em> one of the <see cref="Canonical"/> keys. It is descriptive rather than
    /// identifying: a resource missing it is still unambiguously Servyx-owned, so both adapters pass it
    /// through as an ordinary extra rather than as part of the identity written last.
    /// </remarks>
    public const string RootPath = Prefix + "root-path";

    /// <summary>
    /// The image/artefact reference the resource was created from, recorded on the resource so a later
    /// drift check has something to compare the live value against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, drift detection cannot check the one property that matters most.</strong> A
    /// <see cref="ResourceHandle"/> carries no image field, so the only place an expectation can live is in
    /// its <see cref="ResourceHandle.Tags"/>. A check with no recorded expectation is not free to assume a
    /// match — see <see cref="DriftDivergence.Expected"/> — so a resource created before this key existed
    /// reports its image as unverifiable rather than as unchanged.
    /// </para>
    /// <para>
    /// Descriptive rather than identifying, so — like <see cref="RootPath"/> — it is deliberately not one of
    /// the <see cref="Canonical"/> keys and travels as an ordinary extra. A resource missing it is still
    /// unambiguously Servyx-owned and still fully sweepable; only its image expectation is unknown. Adapters
    /// whose resources have no image concept simply never write it.
    /// </para>
    /// </remarks>
    public const string Image = Prefix + "image";

    /// <summary>
    /// The machine size/shape reference the resource was created at — a DigitalOcean size slug, an Azure VM
    /// size — recorded on the resource so a later drift check has something to compare the live value
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists for exactly the reason <see cref="Image"/> does, and reads the same way: a
    /// <see cref="ResourceHandle"/> carries no size field, so its <see cref="ResourceHandle.Tags"/> is the
    /// only place an expectation can live, and a check with no recorded expectation reports the size as
    /// unverifiable rather than as unchanged. On a cloud VM the two together are what a drift check is
    /// mostly <em>for</em>: an out-of-band resize is the cheapest way for a machine to stop matching what
    /// Servyx recorded while still looking healthy.
    /// </para>
    /// <para>
    /// Descriptive rather than identifying, so — like <see cref="RootPath"/> and <see cref="Image"/> — it is
    /// deliberately not one of the <see cref="Canonical"/> keys and travels as an ordinary extra. Adapters
    /// whose resources have no size concept (a container, a process) simply never write it.
    /// </para>
    /// </remarks>
    public const string Size = Prefix + "size";

    /// <summary>
    /// The identity keys every Servyx-managed resource carries, in the order <see cref="Build"/> writes
    /// them. A resource missing any of these cannot be attributed after the fact, which is why
    /// <see cref="TryReadIdentity"/> refuses to reconstruct an identity from a partial set rather than
    /// defaulting the gaps.
    /// </summary>
    public static IReadOnlyList<string> Canonical { get; } = [Managed, InstanceId, JobId, ConnectorId];

    /// <summary>
    /// Builds the canonical tag dictionary for a resource: <paramref name="additional"/> first, then the
    /// <see cref="Canonical"/> keys.
    /// </summary>
    /// <remarks>
    /// The ordering is the guarantee, not an implementation detail. Caller-supplied extras are written
    /// first precisely so a caller cannot pass <c>servyx.managed=false</c> (or someone else's instance id)
    /// and thereby hide a resource it owns from an orphan sweep. Both adapters route every tag set they
    /// produce through this method, so the rule is enforced in one place instead of being restated per
    /// adapter.
    /// </remarks>
    /// <param name="instanceId">The Servyx server/instance the resource backs. Required, non-blank.</param>
    /// <param name="jobId">The provisioning job that asked for the resource. Required, non-blank.</param>
    /// <param name="connectorId">The connector the resource is reachable through. Required, non-blank.</param>
    /// <param name="additional">Adapter- or caller-supplied extras. Never able to shadow a canonical key.</param>
    /// <exception cref="ArgumentException">Any identity argument is null, empty, or whitespace.</exception>
    public static IReadOnlyDictionary<string, string> Build(
        string instanceId,
        string jobId,
        string connectorId,
        IReadOnlyDictionary<string, string>? additional = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (additional is not null)
        {
            foreach (var pair in additional)
            {
                tags[pair.Key] = pair.Value;
            }
        }

        tags[Managed] = ManagedValue;
        tags[InstanceId] = instanceId;
        tags[JobId] = jobId;
        tags[ConnectorId] = connectorId;

        return tags;
    }

    /// <summary>Whether <paramref name="tags"/> mark the resource as Servyx-managed.</summary>
    /// <remarks>
    /// Deliberately an exact <see cref="StringComparison.Ordinal"/> match against
    /// <see cref="ManagedValue"/>, not a truthiness test: a sweep that treats <c>"TRUE"</c>, <c>"1"</c>, or
    /// anything else as ownership can destroy a resource Servyx never created.
    /// </remarks>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) =>
        tags is not null
        && tags.TryGetValue(Managed, out var managed)
        && string.Equals(managed, ManagedValue, StringComparison.Ordinal);

    /// <summary>
    /// Reads the Servyx identity out of a resource's tags, or reports <see langword="false"/> if the
    /// resource is not Servyx-managed or is missing any identity key.
    /// </summary>
    /// <remarks>
    /// Never invents a value for a missing key. A partially-tagged resource is reported as unidentifiable
    /// rather than as belonging to whatever the gaps would default to — attributing a resource to the wrong
    /// instance is strictly worse than failing to attribute it at all.
    /// </remarks>
    public static bool TryReadIdentity(
        IReadOnlyDictionary<string, string>? tags,
        [NotNullWhen(true)] out string? instanceId,
        [NotNullWhen(true)] out string? jobId,
        [NotNullWhen(true)] out string? connectorId)
    {
        instanceId = null;
        jobId = null;
        connectorId = null;

        if (!IsManaged(tags))
        {
            return false;
        }

        if (!tags!.TryGetValue(InstanceId, out var readInstanceId) || string.IsNullOrWhiteSpace(readInstanceId)
            || !tags.TryGetValue(JobId, out var readJobId) || string.IsNullOrWhiteSpace(readJobId)
            || !tags.TryGetValue(ConnectorId, out var readConnectorId) || string.IsNullOrWhiteSpace(readConnectorId))
        {
            return false;
        }

        instanceId = readInstanceId;
        jobId = readJobId;
        connectorId = readConnectorId;
        return true;
    }
}
