using System.Text.RegularExpressions;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// Everything needed to create one Lightsail instance: the provider-independent <see cref="MachineSpec"/> the
/// domain already defines for shape I, plus the handful of things <c>CreateInstances</c> needs that
/// <see cref="MachineSpec"/> does not carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The mapping onto <see cref="MachineSpec"/>, and how much of it is a straight rename.</strong>
/// <see cref="MachineSpec.ImageRef"/> is <c>blueprintId</c>, <see cref="MachineSpec.SizeRef"/> is
/// <c>bundleId</c>, <see cref="MachineSpec.CloudInit"/> is <c>userData</c>, and
/// <see cref="MachineSpec.Tags"/> feeds the request's <c>tags</c> array. That is a materially larger fraction of
/// <see cref="MachineSpec"/> mapped without a wart than <c>AwsEc2InstanceSpec</c> manages: there is no
/// <c>NetworkInterface</c>-vs-top-level split (Lightsail has no VPC/subnet/security-group concept to place an
/// instance into at all), and <see cref="MachineSpec.CloudInit"/> needs no base64 transcoding at the wire
/// boundary the way EC2's <c>UserData</c> does — Lightsail's <c>userData</c> is plain text on the wire.
/// </para>
/// <para>
/// <strong>Where it does not fit — two places, both genuinely smaller than EC2's three.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="AvailabilityZone"/> has no <see cref="MachineSpec"/> counterpart at all: <c>CreateInstances</c>
/// requires a specific zone (e.g. <c>us-east-1a</c>), one level more specific than <see cref="MachineSpec.Region"/>.
/// <c>AwsLightsailProvisioner.BuildSpec</c> defaults it to the adapter's region with an <c>"a"</c> suffix when a
/// caller does not name one explicitly.
/// </description></item>
/// <item><description>
/// <see cref="MachineSpec.Ingress"/> cannot be honoured, for the same structural reason as EC2's: applying it
/// would mean calling <c>PutInstancePublicPorts</c>, a mutation this adapter does not make. See
/// <see cref="AwsLightsailProvisioner"/>'s remarks for the one nuance that differs from EC2 here — Lightsail's
/// default instance firewall is not deny-all.
/// </description></item>
/// </list>
/// <para>
/// There is deliberately no <c>SecurityGroupIds</c>/<c>SubnetId</c> equivalent at all, unlike
/// <c>AwsEc2InstanceSpec</c>: Lightsail instances are placed on a flat, fully-managed network with no VPC
/// concept for a caller to name a member of.
/// </para>
/// </remarks>
public sealed record AwsLightsailInstanceSpec
{
    /// <summary>
    /// Lightsail's own pattern for a legal instance/resource name, reproduced from its published parameter
    /// documentation: at least two characters, starting and ending with a word character, with only word
    /// characters and hyphens in between.
    /// </summary>
    private static readonly Regex NamePattern = new(
        @"^\w[\w\-]*\w$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates a spec.</summary>
    /// <param name="instanceName">
    /// The name to create the instance under. Unlike an EC2 instance id, this <em>is</em> the instance's
    /// identity at the provider — every other Lightsail action names the instance by it, not by a
    /// provider-generated id. Validated against <see cref="NamePattern"/> at construction, so an illegal name is
    /// caught before <see cref="AwsLightsailProvisioner.PlanAsync"/> ever runs rather than as a 400 from
    /// <c>CreateInstances</c>.
    /// </param>
    /// <param name="machine">The provider-independent machine shape.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException"><paramref name="instanceName"/> is blank or not a legal Lightsail resource name.</exception>
    public AwsLightsailInstanceSpec(string instanceName, MachineSpec machine, ServyxLightsailTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tags);

        if (!NamePattern.IsMatch(instanceName))
        {
            throw new ArgumentException(
                $"'{instanceName}' is not a legal Lightsail instance name. Lightsail requires at least two "
                + "characters, starting and ending with a letter, digit or underscore, with only letters, "
                + "digits, underscores and hyphens in between.",
                nameof(instanceName));
        }

        InstanceName = instanceName;
        Machine = machine;
        Tags = tags;
    }

    /// <summary>The instance's name at the provider - its identity, not merely a label. See the constructor remarks.</summary>
    public string InstanceName { get; }

    /// <summary>The provider-independent machine shape this instance realises.</summary>
    public MachineSpec Machine { get; }

    /// <summary>The mandatory Servyx identity stamped onto the instance.</summary>
    public ServyxLightsailTags Tags { get; }

    /// <summary>
    /// The availability zone to create the instance in, e.g. <c>us-east-1a</c>. Required by <c>CreateInstances</c>;
    /// see the type remarks for how <c>BuildSpec</c> defaults it when a caller does not supply one.
    /// </summary>
    public string AvailabilityZone { get; init; } = string.Empty;

    /// <summary>
    /// The name of a Lightsail key pair <em>already registered in the account</em>, or <see langword="null"/> to
    /// let Lightsail use the account's default key pair.
    /// </summary>
    public string? KeyPairName { get; init; }

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
