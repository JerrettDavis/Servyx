using System.Globalization;
using System.Text;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// Translates an <see cref="AwsEc2InstanceSpec"/> into the flat, indexed parameter list the EC2 Query API's
/// <c>RunInstances</c> action expects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a separate file.</strong> The EC2 Query protocol has no request objects — a nested
/// structure is expressed by flattening it into <c>Name.1.Sub.2.Field</c> keys, one-based, in an order the
/// service reassembles. That is verbose enough to bury the actual decisions if it lived inside the provisioner,
/// and it is the exact place a silent mistake (a zero-based index, a missing <c>ResourceType</c>) turns into a
/// resource that exists but is untagged. Keeping it here makes it reviewable on its own.
/// </para>
/// <para>
/// <strong>Two <c>TagSpecification</c> entries, not one, and that is the orphan story's foundation.</strong>
/// <c>TagSpecification.1</c> tags the instance; <c>TagSpecification.2</c> tags every EBS volume the launch
/// creates. Both are applied by the same call that creates the resources, so there is no window in which either
/// exists untagged. This is the capability Azure's managed OS disk simply does not have — that disk is
/// materialised implicitly by the VM write with no request in which Servyx could tag it — and it is why an
/// orphaned EC2 root volume is findable where an orphaned Azure OS disk is not.
/// </para>
/// <para>
/// <strong>The <c>NetworkInterface</c> wart, named rather than hidden.</strong> EC2 refuses a request that
/// carries both a top-level <c>SubnetId</c>/<c>SecurityGroupId</c> and a <c>NetworkInterface.1</c> block, and a
/// public IPv4 address can only be requested through the latter. So the shape of the request changes depending
/// on whether a public address was asked for, and the same subnet id lands under a different parameter name in
/// each case. There is no way to write this as one uniform mapping; pretending otherwise would produce a
/// request EC2 rejects.
/// </para>
/// </remarks>
internal static class AwsEc2Requests
{
    /// <summary>The <c>ResourceType</c> value that tags the instance itself.</summary>
    internal const string InstanceResourceType = "instance";

    /// <summary>The <c>ResourceType</c> value that tags every volume the launch creates.</summary>
    internal const string VolumeResourceType = "volume";

    /// <summary>Builds the full <c>RunInstances</c> parameter list for <paramref name="spec"/>.</summary>
    /// <param name="spec">The instance to launch.</param>
    /// <param name="instanceTags">The tag set to stamp on the instance.</param>
    /// <param name="volumeTags">The tag set to stamp on the volumes the launch creates.</param>
    internal static List<KeyValuePair<string, string>> RunInstances(
        AwsEc2InstanceSpec spec,
        IReadOnlyDictionary<string, string> instanceTags,
        IReadOnlyDictionary<string, string> volumeTags)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(instanceTags);
        ArgumentNullException.ThrowIfNull(volumeTags);

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("ImageId", spec.Machine.ImageRef),
            new("InstanceType", spec.Machine.SizeRef),
            new("MinCount", "1"),
            new("MaxCount", "1"),
        };

        if (!string.IsNullOrWhiteSpace(spec.KeyPairName))
        {
            parameters.Add(new KeyValuePair<string, string>("KeyName", spec.KeyPairName));
        }

        if (!string.IsNullOrEmpty(spec.Machine.CloudInit))
        {
            // EC2 requires user-data base64-encoded on the wire. Nothing is authored here; the caller's bytes
            // are re-encoded and forwarded unchanged.
            parameters.Add(new KeyValuePair<string, string>(
                "UserData",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(spec.Machine.CloudInit))));
        }

        AppendPlacement(parameters, spec);
        AppendTagSpecification(parameters, index: 1, InstanceResourceType, instanceTags);
        AppendTagSpecification(parameters, index: 2, VolumeResourceType, volumeTags);

        return parameters;
    }

    /// <summary>Emits either the network-interface block or the top-level subnet/group parameters — never both.</summary>
    private static void AppendPlacement(List<KeyValuePair<string, string>> parameters, AwsEc2InstanceSpec spec)
    {
        if (spec.AssignPublicIp)
        {
            parameters.Add(new KeyValuePair<string, string>("NetworkInterface.1.DeviceIndex", "0"));
            parameters.Add(new KeyValuePair<string, string>("NetworkInterface.1.AssociatePublicIpAddress", "true"));

            if (!string.IsNullOrWhiteSpace(spec.SubnetId))
            {
                parameters.Add(new KeyValuePair<string, string>("NetworkInterface.1.SubnetId", spec.SubnetId));
            }

            for (var i = 0; i < spec.SecurityGroupIds.Count; i++)
            {
                parameters.Add(new KeyValuePair<string, string>(
                    string.Create(CultureInfo.InvariantCulture, $"NetworkInterface.1.SecurityGroupId.{i + 1}"),
                    spec.SecurityGroupIds[i]));
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(spec.SubnetId))
        {
            parameters.Add(new KeyValuePair<string, string>("SubnetId", spec.SubnetId));
        }

        for (var i = 0; i < spec.SecurityGroupIds.Count; i++)
        {
            parameters.Add(new KeyValuePair<string, string>(
                string.Create(CultureInfo.InvariantCulture, $"SecurityGroupId.{i + 1}"),
                spec.SecurityGroupIds[i]));
        }
    }

    /// <summary>
    /// Emits one <c>TagSpecification.&lt;index&gt;</c> block, key-sorted so the request is deterministic.
    /// </summary>
    /// <remarks>
    /// Ordering is not cosmetic: the plan hash is computed over the same tag set, and a request whose parameter
    /// order varied run to run would make two identical launches produce two different signed payloads, which
    /// makes a recorded request impossible to compare against a later one.
    /// </remarks>
    private static void AppendTagSpecification(
        List<KeyValuePair<string, string>> parameters,
        int index,
        string resourceType,
        IReadOnlyDictionary<string, string> tags)
    {
        parameters.Add(new KeyValuePair<string, string>(
            string.Create(CultureInfo.InvariantCulture, $"TagSpecification.{index}.ResourceType"),
            resourceType));

        var tagIndex = 1;
        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            parameters.Add(new KeyValuePair<string, string>(
                string.Create(CultureInfo.InvariantCulture, $"TagSpecification.{index}.Tag.{tagIndex}.Key"),
                tag.Key));
            parameters.Add(new KeyValuePair<string, string>(
                string.Create(CultureInfo.InvariantCulture, $"TagSpecification.{index}.Tag.{tagIndex}.Value"),
                tag.Value));
            tagIndex++;
        }
    }
}
