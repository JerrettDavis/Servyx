using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The EC2 objects this adapter reads, projected out of the Query API's XML into ordinary records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these are hand-projected rather than deserialised.</strong> The EC2 Query API answers in XML,
/// not JSON, so there is no <c>System.Text.Json</c> equivalent of the DigitalOcean and Azure adapters'
/// <c>ReadFromJsonAsync</c>. <see cref="XDocument"/> is in the shared framework and covers it, but the schema
/// is verbose and versioned (<c>instancesSet/item</c>, <c>tagSet/item</c>, an <c>xmlns</c> that carries the API
/// version), so everything here matches on <em>local</em> element names. That makes the projection survive an
/// api-version bump that changes only the namespace, which is the change most likely to happen and the one
/// least worth a code edit.
/// </para>
/// <para>
/// <strong>The one field name worth calling out.</strong> An instance's public address is
/// <c>&lt;ipAddress&gt;</c> in this API and its private one is <c>&lt;privateIpAddress&gt;</c> — the asymmetry
/// is real and is a classic source of a null address. <see cref="Ec2Instance.PublicIpAddress"/> reads
/// <c>ipAddress</c> and falls back to <c>publicIpAddress</c> so a future schema that regularises the name still
/// works.
/// </para>
/// </remarks>
internal static class Ec2Xml
{
    /// <summary>The first child of <paramref name="parent"/> with the given local name, ignoring namespaces.</summary>
    internal static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    /// <summary>Every descendant of <paramref name="parent"/> with the given local name, ignoring namespaces.</summary>
    internal static IEnumerable<XElement> Children(XElement? parent, string localName) =>
        parent?.Elements().Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal)) ?? [];

    /// <summary>The text of a named child, or <see langword="null"/> when absent or blank.</summary>
    internal static string? Text(XElement? parent, string localName)
    {
        var value = Child(parent, localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The <c>item</c> elements of a named set element (EC2's list shape).</summary>
    internal static IEnumerable<XElement> Items(XElement? parent, string setName) =>
        Children(Child(parent, setName), "item");

    /// <summary>Reads an EC2 <c>tagSet</c> into an ordinal-comparer dictionary.</summary>
    internal static IReadOnlyDictionary<string, string> Tags(XElement? parent)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in Items(parent, "tagSet"))
        {
            var key = Text(item, "key");
            if (key is not null)
            {
                tags[key] = Child(item, "value")?.Value ?? string.Empty;
            }
        }

        return tags;
    }

    /// <summary>Parses an EC2 timestamp, or <see langword="null"/> when absent or unparseable.</summary>
    internal static DateTimeOffset? Timestamp(XElement? parent, string localName) =>
        DateTimeOffset.TryParse(
            Text(parent, localName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}

/// <summary>An EC2 instance, as the Query API describes it.</summary>
/// <param name="InstanceId">The instance's provider id, e.g. <c>i-0123456789abcdef0</c>.</param>
/// <param name="InstanceType">The instance type, e.g. <c>t3.medium</c>. This is the priced dimension.</param>
/// <param name="ImageId">The AMI the instance was launched from.</param>
/// <param name="State">The lifecycle state name: <c>pending</c>, <c>running</c>, <c>shutting-down</c>, <c>terminated</c>, <c>stopping</c>, <c>stopped</c>.</param>
/// <param name="PublicIpAddress">The public IPv4 address, if the instance has one yet.</param>
/// <param name="PrivateIpAddress">The private IPv4 address, if the instance has one yet.</param>
/// <param name="AvailabilityZone">The availability zone, e.g. <c>us-east-1a</c>.</param>
/// <param name="LaunchTime">When EC2 reports the instance was launched.</param>
/// <param name="Tags">The instance's tags, decoded from its <c>tagSet</c>.</param>
/// <param name="BlockDevices">The EBS devices the instance's block device mapping attaches.</param>
internal sealed record Ec2Instance(
    string InstanceId,
    string? InstanceType,
    string? ImageId,
    string? State,
    string? PublicIpAddress,
    string? PrivateIpAddress,
    string? AvailabilityZone,
    DateTimeOffset? LaunchTime,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<Ec2BlockDevice> BlockDevices)
{
    /// <summary>The states in which an instance no longer exists as far as Servyx is concerned.</summary>
    /// <remarks>
    /// EC2 keeps a terminated instance visible to <c>DescribeInstances</c> for up to about an hour after it is
    /// gone, so "does not exist" is a <em>state</em> here and not a 404. Every place this adapter would
    /// otherwise treat a missing resource as absent has to consult this set instead — see
    /// <c>AwsEc2Provisioner.RefreshAsync</c>, which returns <see langword="null"/> for a terminated instance
    /// even though EC2 answered with a complete instance object.
    /// </remarks>
    internal static IReadOnlySet<string> GoneStates { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "terminated", "shutting-down" };

    /// <summary>Whether EC2 still reports this instance as an existing, potentially-billing machine.</summary>
    internal bool IsGone => State is not null && GoneStates.Contains(State);

    /// <summary>Projects one <c>instancesSet/item</c> element.</summary>
    internal static Ec2Instance? From(XElement item)
    {
        var instanceId = Ec2Xml.Text(item, "instanceId");
        if (instanceId is null)
        {
            return null;
        }

        var blockDevices = Ec2Xml.Items(item, "blockDeviceMapping")
            .Select(Ec2BlockDevice.From)
            .Where(device => device is not null)
            .Select(device => device!)
            .ToList();

        return new Ec2Instance(
            instanceId,
            Ec2Xml.Text(item, "instanceType"),
            Ec2Xml.Text(item, "imageId"),
            Ec2Xml.Text(Ec2Xml.Child(item, "instanceState"), "name"),
            Ec2Xml.Text(item, "ipAddress") ?? Ec2Xml.Text(item, "publicIpAddress"),
            Ec2Xml.Text(item, "privateIpAddress"),
            Ec2Xml.Text(Ec2Xml.Child(item, "placement"), "availabilityZone"),
            Ec2Xml.Timestamp(item, "launchTime"),
            Ec2Xml.Tags(item),
            blockDevices);
    }
}

/// <summary>One EBS device an instance's <c>blockDeviceMapping</c> attaches.</summary>
/// <param name="DeviceName">The guest device name, e.g. <c>/dev/xvda</c>.</param>
/// <param name="VolumeId">The EBS volume attached at that device.</param>
/// <param name="DeleteOnTermination">
/// Whether EC2 deletes the volume when the instance is terminated, or <see langword="null"/> when the API
/// reported no value for it.
/// </param>
/// <remarks>
/// <para>
/// <strong><see cref="DeleteOnTermination"/> is the whole reason this record exists</strong>, and it is the one
/// field that decides what an update plan may claim about a caller's data. This adapter sends no
/// <c>BlockDeviceMapping</c> on <c>RunInstances</c> (see <c>AwsEc2Provisioner</c>'s type remarks), so the flag
/// is whatever the AMI's own default is — which means it can only ever be <em>read back</em> off a live
/// instance, never assumed from anything this codebase did.
/// </para>
/// <para>
/// <see langword="null"/> is deliberately distinguished from <see langword="false"/>. "EC2 reported nothing" is
/// not evidence that the volume survives, and a planner that collapsed the two would state a data impact it
/// has no grounds for — see <c>AwsEc2Provisioner.AssertDataImpact</c>, which answers the unknown case with
/// <c>DataImpact.AtRisk</c> rather than with the reassuring value.
/// </para>
/// </remarks>
internal sealed record Ec2BlockDevice(string? DeviceName, string VolumeId, bool? DeleteOnTermination)
{
    /// <summary>Projects one <c>blockDeviceMapping/item</c> element, or <see langword="null"/> if it names no EBS volume.</summary>
    internal static Ec2BlockDevice? From(XElement item)
    {
        var ebs = Ec2Xml.Child(item, "ebs");
        var volumeId = Ec2Xml.Text(ebs, "volumeId");

        if (volumeId is null)
        {
            return null;
        }

        var flag = Ec2Xml.Text(ebs, "deleteOnTermination")?.Trim();
        var deleteOnTermination =
            string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase) ? false
            : (bool?)null;

        return new Ec2BlockDevice(Ec2Xml.Text(item, "deviceName"), volumeId, deleteOnTermination);
    }
}

/// <summary>An EBS volume, as the Query API describes it.</summary>
/// <param name="VolumeId">The volume's provider id, e.g. <c>vol-0123456789abcdef0</c>.</param>
/// <param name="State">The volume state: <c>creating</c>, <c>available</c>, <c>in-use</c>, <c>deleting</c>, <c>deleted</c>, <c>error</c>.</param>
/// <param name="AvailabilityZone">The availability zone the volume lives in.</param>
/// <param name="SizeGib">The volume's size in GiB — the billed dimension.</param>
/// <param name="CreateTime">When EC2 reports the volume was created.</param>
/// <param name="Tags">The volume's tags, decoded from its <c>tagSet</c>.</param>
/// <param name="AttachedInstanceIds">Instances the volume is currently attached to.</param>
internal sealed record Ec2Volume(
    string VolumeId,
    string? State,
    string? AvailabilityZone,
    int? SizeGib,
    DateTimeOffset? CreateTime,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyList<string> AttachedInstanceIds)
{
    /// <summary>Whether the volume is attached to something and therefore cannot be deleted on its own.</summary>
    internal bool IsAttached => AttachedInstanceIds.Count > 0;

    /// <summary>Projects one <c>volumeSet/item</c> element.</summary>
    internal static Ec2Volume? From(XElement item)
    {
        var volumeId = Ec2Xml.Text(item, "volumeId");
        if (volumeId is null)
        {
            return null;
        }

        var attachments = Ec2Xml.Items(item, "attachmentSet")
            .Select(a => Ec2Xml.Text(a, "instanceId"))
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();

        return new Ec2Volume(
            volumeId,
            Ec2Xml.Text(item, "status"),
            Ec2Xml.Text(item, "availabilityZone"),
            int.TryParse(Ec2Xml.Text(item, "size"), CultureInfo.InvariantCulture, out var size) ? size : null,
            Ec2Xml.Timestamp(item, "createTime"),
            Ec2Xml.Tags(item),
            attachments);
    }
}

/// <summary>
/// An AWS API call that did not succeed.
/// </summary>
/// <remarks>
/// Carries the HTTP status and EC2's own error code so a caller can distinguish a throttle
/// (<c>RequestLimitExceeded</c>), an authentication failure (<c>AuthFailure</c>,
/// <c>SignatureDoesNotMatch</c>) and a missing resource (<c>InvalidInstanceID.NotFound</c>) from a genuine
/// service error — and never carries any part of the request, so neither the secret access key (which never
/// travels anyway) nor the signature that stood in for it can reach a message.
/// </remarks>
public sealed class AwsApiException : Exception
{
    /// <summary>Creates an exception for a failed AWS API call.</summary>
    /// <param name="statusCode">The HTTP status AWS returned.</param>
    /// <param name="errorCode">EC2's own error code, if the response carried one.</param>
    /// <param name="message">A message built from the status and the service's error body only.</param>
    public AwsApiException(HttpStatusCode statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>Creates an exception for a failed AWS API call.</summary>
    /// <param name="statusCode">The HTTP status AWS returned.</param>
    /// <param name="errorCode">EC2's own error code, if the response carried one.</param>
    /// <param name="message">A message built from the status and the service's error body only.</param>
    /// <param name="innerException">The underlying failure.</param>
    public AwsApiException(HttpStatusCode statusCode, string? errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    /// <summary>Creates an exception with no status context.</summary>
    public AwsApiException()
        : this(HttpStatusCode.InternalServerError, null, "An AWS API call failed.")
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    /// <param name="message">The failure description.</param>
    public AwsApiException(string message)
        : this(HttpStatusCode.InternalServerError, null, message)
    {
    }

    /// <summary>Creates an exception with no status context.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public AwsApiException(string message, Exception innerException)
        : this(HttpStatusCode.InternalServerError, null, message, innerException)
    {
    }

    /// <summary>The HTTP status AWS returned.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>EC2's own error code, e.g. <c>InvalidInstanceID.NotFound</c>, when the response carried one.</summary>
    public string? ErrorCode { get; }
}
