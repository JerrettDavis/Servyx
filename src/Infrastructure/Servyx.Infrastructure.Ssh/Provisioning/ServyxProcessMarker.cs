using System.Text.Json;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Ssh.Provisioning;

/// <summary>
/// The universal Servyx tags every process install this project creates must carry, together with the
/// on-host JSON file that stores them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the process-shape analogue of Docker container labels.</strong> A container carries its
/// Servyx identity inside the daemon: <c>docker create</c> writes <c>servyx.managed=true</c>,
/// <c>servyx.instance-id</c>, and friends onto the container object, and
/// <c>DockerContainerProvisioner.ReconcileAsync</c> later asks the daemon "give me everything labelled
/// <c>servyx.managed=true</c>". A process install has no such registry — there is no daemon holding metadata
/// about "the Palworld install in <c>/opt/palworld</c>". So Servyx supplies the missing store itself: one
/// small JSON file per instance, under a known marker root, whose contents are <em>the same key/value
/// vocabulary the Docker labels use</em>. <c>ReconcileAsync</c> then enumerates that directory instead of
/// querying a daemon, and <c>RefreshAsync</c> reads one file instead of inspecting one container.
/// </para>
/// <para>
/// The correspondence is deliberately exact, key for key, so that the two adapters produce
/// <see cref="Domain.Provisioning.ResourceHandle.Tags"/> dictionaries that are interchangeable to everything
/// above them. That interchangeability is the whole content of the "shape H covers both containers and
/// processes" claim: a caller reconciling orphans does not need to know which shape produced a handle.
/// </para>
/// <para>
/// <strong>Where the analogy is weaker, and it matters.</strong> A container's labels are applied by the
/// same atomic create call that brings the container into existence — there is no window in which a
/// container exists unlabelled. A marker file is a separate write, so there <em>is</em> such a window unless
/// the marker is written deliberately first. <c>SshProcessProvisioner</c> therefore writes the marker
/// <em>before</em> running any install verb, mirroring the write-ahead ledger's rule that intent is durable
/// before the mutating call. An install that dies halfway is then still discoverable by a sweep; the reverse
/// ordering would leave a half-installed directory with nothing on the host tying it to Servyx.
/// </para>
/// <para>
/// <strong>The tag keys are shared with, not duplicated from, the Docker adapter.</strong> They used to be
/// duplicated — infrastructure projects reference only <c>Servyx.Domain</c> and never each other, so short
/// of promoting the vocabulary there was nowhere to put a shared constant, and the two copies were kept
/// identical purely by convention. That convention was load-bearing in the worst way: one character of
/// drift and each adapter's sweep would stop seeing the other's resources, silently, forever. The
/// vocabulary now lives in <see cref="ServyxTagKeys"/> and both adapters alias it, so the values cannot
/// diverge. The alias names below stay marker-flavoured ("tag", not "label") because that is what this
/// assembly is storing, but they no longer carry an independent definition.
/// </para>
/// <para>
/// The three keys this shape needs and the container shape does not — <see cref="ProvisionerIdTag"/>,
/// <see cref="ExecutableTag"/>, <see cref="CreatedAtTag"/> — stay local, built from
/// <see cref="ServyxTagKeys.Prefix"/> so they remain visibly part of the same namespace. They exist because
/// a marker file has to carry facts a container object already knows about itself: which provisioner owns
/// it (a marker root can hold more than one shape), what was installed, and when. They are written as
/// ordinary extras, not as identity, so they can never displace a canonical key.
/// </para>
/// </remarks>
public sealed class ServyxProcessMarker
{
    /// <summary>Marks an install as created and owned by Servyx. Always <see cref="ManagedTagValue"/>.</summary>
    public const string ManagedTag = ServyxTagKeys.Managed;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxTagKeys.ManagedValue;

    /// <summary>Identifies the Servyx server/instance the install backs.</summary>
    public const string InstanceIdTag = ServyxTagKeys.InstanceId;

    /// <summary>Identifies the provisioning job that asked for the install.</summary>
    public const string JobIdTag = ServyxTagKeys.JobId;

    /// <summary>Identifies the connector the install is reachable through, so a refresh can rebuild it.</summary>
    public const string ConnectorIdTag = ServyxTagKeys.ConnectorId;

    /// <summary>The on-host directory a <c>TargetDescriptor</c>'s paths are relative to (the profile's <c>dataDir</c>).</summary>
    public const string RootPathTag = ServyxTagKeys.RootPath;

    /// <summary>The provisioner that created the install, so a marker root shared with another shape stays unambiguous.</summary>
    public const string ProvisionerIdTag = ServyxTagKeys.Prefix + "provisioner-id";

    /// <summary>The executable the deployment profile declared, recorded so a refresh can describe the install.</summary>
    public const string ExecutableTag = ServyxTagKeys.Prefix + "executable";

    /// <summary>When the install was created, in round-trip ISO-8601.</summary>
    public const string CreatedAtTag = ServyxTagKeys.Prefix + "created-at";

    /// <summary>The filename suffix every marker file carries, used by the reconcile sweep to recognise one.</summary>
    public const string FileSuffix = ".servyx.json";

    private ServyxProcessMarker(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the install backs.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for the install.</summary>
    public string JobId { get; }

    /// <summary>The connector the install is reachable through.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The only way to obtain a <see cref="ServyxProcessMarker"/>. Every parameter is required and rejected
    /// when blank, exactly as <c>ServyxResourceTags.For</c> is on the Docker side — a defaulted value would
    /// let Servyx ship an install whose owner cannot be identified after the fact.
    /// </summary>
    /// <remarks>
    /// <paramref name="instanceId"/> is additionally constrained to a conservative filename charset, because
    /// unlike a container label it becomes part of a path on the target host. Without that constraint an
    /// instance id of <c>../../etc/cron.d/servyx</c> would place the marker file wherever the caller liked.
    /// </remarks>
    /// <exception cref="ArgumentException">Any argument is blank, or <paramref name="instanceId"/> is not a safe filename stem.</exception>
    public static ServyxProcessMarker For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        if (!IsSafeFileStem(instanceId))
        {
            throw new ArgumentException(
                $"Instance id '{instanceId}' is not usable as a marker filename. Only letters, digits, '.', '_', and '-' are permitted, " +
                "because the id becomes part of a path on the target host.",
                nameof(instanceId));
        }

        return new ServyxProcessMarker(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the tag dictionary written into the marker file. Any <paramref name="additional"/> tags are
    /// applied first and the mandatory Servyx tags last, so extras can never override them — the same
    /// ordering rule <c>ServyxResourceTags.ToLabels</c> enforces for container labels.
    /// </summary>
    /// <remarks>
    /// "The same ordering rule" is now literally the same code: both adapters delegate to
    /// <see cref="ServyxTagKeys.Build"/>. What stays different is only the store the resulting dictionary is
    /// written to — a JSON file here, container labels there.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs a marker from a parsed tag dictionary, or returns <see langword="null"/> if the file is
    /// not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    /// <remarks>
    /// Applies one check the Docker side does not, and must: the instance id recovered from a marker becomes
    /// part of a path on the target host, so a marker whose id is not a safe filename stem is rejected even
    /// though it is otherwise well-formed. A container label is never interpreted as a path, so
    /// <c>ServyxResourceTags.FromLabels</c> has no equivalent constraint. This is a genuine, deliberate
    /// asymmetry between the two shapes, not drift in the shared vocabulary.
    /// </remarks>
    public static ServyxProcessMarker? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
        && IsSafeFileStem(instanceId)
            ? new ServyxProcessMarker(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a parsed marker's tags mark the install as Servyx-managed.</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>The absolute path of the marker file for <paramref name="instanceId"/> under <paramref name="markerRoot"/>.</summary>
    public static string PathFor(string markerRoot, string instanceId) =>
        $"{markerRoot.TrimEnd('/')}/{instanceId}{FileSuffix}";

    /// <summary>Whether a directory entry name looks like a Servyx marker file.</summary>
    public static bool IsMarkerFileName(string name) =>
        name.EndsWith(FileSuffix, StringComparison.Ordinal) && name.Length > FileSuffix.Length;

    /// <summary>
    /// Serialises <paramref name="tags"/> to the marker file's on-disk form: a flat JSON object of string
    /// values, key-sorted so two identical tag sets always produce byte-identical files (which is what makes
    /// a content hash of the marker meaningful to <c>WriteFileAsync</c>'s drift check).
    /// </summary>
    public static byte[] Serialize(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var pair in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Parses a marker file's contents back into a tag dictionary. Non-string members are ignored rather
    /// than throwing, so a marker a future version has extended does not break an older sweep; malformed
    /// JSON yields <see langword="null"/>, because a sweep must never treat an unreadable file as evidence
    /// that an install is or is not Servyx-owned.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    tags[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return tags;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeFileStem(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        // Reject "." and ".." outright: both are safe by charset but are not filenames.
        return value is not ("." or "..");
    }
}
