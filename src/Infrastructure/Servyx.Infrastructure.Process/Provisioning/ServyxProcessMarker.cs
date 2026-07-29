using System.Text.Json;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// The universal Servyx tags every local process install this project creates must carry, together with the
/// on-disk JSON file that stores them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Same vocabulary, same storage shape, different assembly.</strong> A process install has no daemon
/// or provider API holding metadata about it, so — exactly as <c>Servyx.Infrastructure.Ssh</c>'s marker does —
/// Servyx supplies the missing registry itself: one small JSON file per instance under a known marker root,
/// whose members are the shared <see cref="ServyxTagKeys"/> vocabulary.
/// <see cref="LocalProcessProvisioner.ReconcileAsync"/> enumerates that directory instead of querying a
/// daemon, and <see cref="LocalProcessProvisioner.RefreshAsync"/> reads one file instead of inspecting one
/// container.
/// </para>
/// <para>
/// <strong>Why this type exists at all, given the SSH project already has one.</strong> Infrastructure
/// projects reference <c>Servyx.Domain</c> and never each other, so there is nowhere to put a shared marker
/// type short of promoting it to the domain — and the domain performs no I/O, while a marker is inherently a
/// file format. What must not diverge is the <em>vocabulary</em>, and that is exactly what
/// <see cref="ServyxTagKeys"/> already owns: every key below aliases it, and the identity tag set is built by
/// <see cref="ServyxTagKeys.Build"/> rather than assembled here, so the two adapters' sweeps cannot go blind
/// to each other's resources through a one-character drift.
/// </para>
/// <para>
/// <strong>One deliberate difference from the SSH marker: path composition.</strong> The SSH marker joins
/// paths with <c>/</c> because the host it writes to is POSIX by definition. A local marker lands on whatever
/// machine Servyx is running on, so <see cref="PathFor"/> composes with <see cref="Path.Combine(string, string)"/>
/// and the resulting separator is the local one. The instance-id charset restriction is unchanged and is
/// load-bearing for the same reason: the id becomes part of a filename.
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

    /// <summary>The directory a <c>TargetDescriptor</c>'s paths are relative to (the profile's <c>dataDir</c>).</summary>
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
    /// when blank — a defaulted value would let Servyx ship an install whose owner cannot be identified after
    /// the fact.
    /// </summary>
    /// <remarks>
    /// <paramref name="instanceId"/> is additionally constrained to a conservative filename charset, because
    /// it becomes part of a path on the machine. Without that constraint an instance id of
    /// <c>../../etc/cron.d/servyx</c> would place the marker file wherever the caller liked.
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
                "because the id becomes part of a path on the machine.",
                nameof(instanceId));
        }

        return new ServyxProcessMarker(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the tag dictionary written into the marker file. Any <paramref name="additional"/> tags are
    /// applied first and the mandatory Servyx tags last, so extras can never override one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs a marker from a parsed tag dictionary, or returns <see langword="null"/> if the file is
    /// not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxProcessMarker? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
        && IsSafeFileStem(instanceId)
            ? new ServyxProcessMarker(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a parsed marker's tags mark the install as Servyx-managed.</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>The absolute path of the marker file for <paramref name="instanceId"/> under <paramref name="markerRoot"/>.</summary>
    public static string PathFor(string markerRoot, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        return Path.Combine(Path.TrimEndingDirectorySeparator(markerRoot), instanceId + FileSuffix);
    }

    /// <summary>Whether a directory entry name looks like a Servyx marker file.</summary>
    public static bool IsMarkerFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(FileSuffix, StringComparison.Ordinal) && name.Length > FileSuffix.Length;
    }

    /// <summary>
    /// Serialises <paramref name="tags"/> to the marker file's on-disk form: a flat JSON object of string
    /// values, key-sorted so two identical tag sets always produce byte-identical files (which is what makes a
    /// content hash of the marker meaningful to <see cref="Domain.Transport.IExecutionTarget.WriteFileAsync"/>'s
    /// drift check).
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
    /// Parses a marker file's contents back into a tag dictionary. Non-string members are ignored rather than
    /// throwing, so a marker a future version has extended does not break an older sweep; malformed JSON
    /// yields <see langword="null"/>, because a sweep must never treat an unreadable file as evidence that an
    /// install is or is not Servyx-owned.
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
