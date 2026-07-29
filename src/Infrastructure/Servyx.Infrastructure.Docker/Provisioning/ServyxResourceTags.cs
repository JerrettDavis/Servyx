using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// The universal Servyx labels every container this project creates must carry, in a form that cannot be
/// constructed without all of them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Structural, not conventional.</strong> The constructor is private and the only way to obtain an
/// instance is <see cref="For"/>, whose parameters are all required and all validated non-blank. Because
/// <see cref="DockerContainerSpec"/> takes a non-nullable <see cref="ServyxResourceTags"/> as a positional
/// constructor argument, and because the only code path in this assembly that builds Docker
/// <c>CreateContainerParameters</c> derives its label dictionary from <see cref="ToLabels"/>, there is no
/// way to create a Servyx-managed container without <see cref="ManagedLabel"/>,
/// <see cref="InstanceIdLabel"/>, and <see cref="JobIdLabel"/> on it. Forgetting them is a compile error,
/// not a code-review miss.
/// </para>
/// <para>
/// <see cref="ToLabels"/> writes the canonical entries <em>after</em> any caller-supplied extras, so an
/// extra label can never shadow or blank out one of the mandatory ones.
/// </para>
/// <para>
/// <see cref="ManagedLabel"/> is what makes the orphan sweep possible at all: it is the tag
/// <c>DockerContainerProvisioner.ReconcileAsync</c> filters on, independent of any Servyx-local record.
/// </para>
/// <para>
/// <strong>The key vocabulary is not defined here.</strong> Every key below is an alias for the
/// corresponding <see cref="ServyxTagKeys"/> constant in <c>Servyx.Domain</c>, which the SSH process
/// adapter's <c>ServyxProcessMarker</c> aliases too. The keys used to be spelled out independently in each
/// adapter and kept identical by convention, because infrastructure projects reference only
/// <c>Servyx.Domain</c> and never each other; a single character of drift would have made one adapter's
/// orphan sweep blind to the other's resources with nothing raised anywhere. The aliases remain because
/// they are the Docker-flavoured names ("label", not "tag") this assembly reads naturally, but they no
/// longer carry an independent definition that could drift.
/// </para>
/// </remarks>
public sealed class ServyxResourceTags
{
    /// <summary>Marks a container as created and owned by Servyx. Always <see cref="ManagedLabelValue"/>.</summary>
    public const string ManagedLabel = ServyxTagKeys.Managed;

    /// <summary>The only value <see cref="ManagedLabel"/> is ever set to.</summary>
    public const string ManagedLabelValue = ServyxTagKeys.ManagedValue;

    /// <summary>Identifies the Servyx server/instance the container backs.</summary>
    public const string InstanceIdLabel = ServyxTagKeys.InstanceId;

    /// <summary>Identifies the provisioning job that asked for the container.</summary>
    public const string JobIdLabel = ServyxTagKeys.JobId;

    /// <summary>Identifies the connector the container is reachable through, so a refresh can rebuild it.</summary>
    public const string ConnectorIdLabel = ServyxTagKeys.ConnectorId;

    /// <summary>
    /// The in-container path a <c>TargetDescriptor</c>'s paths are relative to, recorded on the container so
    /// <c>RefreshAsync</c> can rebuild an identical descriptor from the live container alone.
    /// </summary>
    public const string RootPathLabel = ServyxTagKeys.RootPath;

    /// <summary>
    /// The image reference the container was created from, recorded on the container so
    /// <c>DockerContainerProvisioner.DetectDriftAsync</c> has a recorded expectation to compare the live
    /// <c>Config.Image</c> against. Descriptive, not identifying — see <see cref="ServyxTagKeys.Image"/>.
    /// </summary>
    public const string ImageLabel = ServyxTagKeys.Image;

    /// <summary>
    /// The complete specification of the container this one <em>replaced</em>, recorded on the replacement at
    /// the moment an update recreates it. This is the only durable record of a container's pre-update state,
    /// and therefore the only thing a rollback can restore from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing else records it.</strong> A <see cref="ResourceHandle"/> carries a provisioner id, a
    /// provider-assigned id, a region and the container's labels — so the ledger knows the image
    /// (<see cref="ImageLabel"/>) and the root path, and nothing about ports, environment or mounts. The
    /// container itself holds all of those, but a recreate removes it, taking the only copy with it. Without
    /// this label a rollback could restore nothing it did not invent, which is why it exists.
    /// </para>
    /// <para>
    /// <strong>Docker-local on purpose.</strong> The payload is an encoded
    /// <see cref="DockerContainerSpec"/> — ports, binds, env — which no other adapter has an analogue for, so
    /// unlike <see cref="ImageLabel"/> and <see cref="RootPathLabel"/> this key is not promoted to
    /// <see cref="ServyxTagKeys"/>. It is still built from <see cref="ServyxTagKeys.Prefix"/> so the Servyx
    /// namespace stays one namespace, exactly as the SSH adapter's own keys are.
    /// </para>
    /// <para>
    /// <strong>A caller can never supply one.</strong> <c>DockerContainerProvisioner.LabelsFor</c> strips this
    /// key (and the two below) out of caller-supplied extras, so a <c>label:servyx.previous-spec</c>
    /// provisioning parameter cannot plant a prior state that Servyx never observed. It is written in exactly
    /// one place: the recreate operation, from a spec read off the live container it is about to replace.
    /// </para>
    /// </remarks>
    public const string PreviousSpecLabel = ServyxTagKeys.Prefix + "previous-spec";

    /// <summary>
    /// When this container was created by a rollback, as a round-trip UTC timestamp. Present only on a
    /// container a rollback produced.
    /// </summary>
    public const string RolledBackAtLabel = ServyxTagKeys.Prefix + "rolled-back-at";

    /// <summary>
    /// The encoded spec of the container a rollback <em>undid</em> — i.e. the updated container that the
    /// restored one replaced.
    /// </summary>
    /// <remarks>
    /// Deliberately a different key from <see cref="PreviousSpecLabel"/>, and deliberately never read as a
    /// prior state. A rollback writes this instead of <see cref="PreviousSpecLabel"/> precisely so a second
    /// consecutive rollback finds no recorded prior state and refuses, rather than treating the update it just
    /// undid as a state to restore and silently re-applying it.
    /// </remarks>
    public const string RolledBackFromLabel = ServyxTagKeys.Prefix + "rolled-back-from";

    /// <summary>
    /// The keys this adapter writes for its own bookkeeping rather than to describe the workload. They are
    /// never part of a <see cref="DockerContainerSpec"/>, never diffed by update planning, and stripped out of
    /// any caller-supplied extras.
    /// </summary>
    public static IReadOnlyList<string> Bookkeeping { get; } = [PreviousSpecLabel, RolledBackAtLabel, RolledBackFromLabel];

    /// <summary>The Docker Engine label filter expression that selects every Servyx-managed container.</summary>
    /// <remarks>
    /// Stays in this assembly rather than moving to <see cref="ServyxTagKeys"/> alongside the keys it is
    /// built from: <c>key=value</c> is the Docker Engine's filter wire syntax, not part of the shared
    /// vocabulary. A marker-file adapter has no filter expression at all — it lists a directory — so
    /// promoting this would put one storage mechanism's query language into the domain.
    /// </remarks>
    public const string ManagedFilter = ManagedLabel + "=" + ManagedLabelValue;

    private ServyxResourceTags(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the container backs.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for the container.</summary>
    public string JobId { get; }

    /// <summary>The connector the container is reachable through.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The only way to obtain a <see cref="ServyxResourceTags"/>. Every parameter is required and rejected
    /// when blank — there is deliberately no defaulting overload, because a default would let a caller ship
    /// a container whose owner cannot be identified after the fact.
    /// </summary>
    public static ServyxResourceTags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        return new ServyxResourceTags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the label dictionary to send to the Docker Engine. Any <paramref name="additional"/> labels
    /// are applied first and the mandatory Servyx labels last, so extras can never override them.
    /// </summary>
    /// <remarks>
    /// The ordering rule is <see cref="ServyxTagKeys.Build"/>'s, applied identically by both adapters
    /// because both call it — rather than each restating it and being trusted to keep restating it the same
    /// way. What differs between the adapters is only where the resulting dictionary is stored: here it goes
    /// to the Docker Engine as container labels.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToLabels(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs tags from a live container's labels, or returns <see langword="null"/> if the container
    /// is not Servyx-managed or is missing any mandatory label. Never invents a value for a missing label.
    /// </summary>
    public static ServyxResourceTags? FromLabels(IReadOnlyDictionary<string, string>? labels) =>
        ServyxTagKeys.TryReadIdentity(labels, out var instanceId, out var jobId, out var connectorId)
            ? new ServyxResourceTags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a container's labels mark it as Servyx-managed.</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? labels) => ServyxTagKeys.IsManaged(labels);
}
