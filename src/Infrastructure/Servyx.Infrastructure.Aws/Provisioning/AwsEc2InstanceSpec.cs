using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// Everything needed to launch one EC2 instance: the provider-independent <see cref="MachineSpec"/> the domain
/// already defines for shape I, plus the handful of things <c>RunInstances</c> needs that
/// <see cref="MachineSpec"/> does not carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this wraps <see cref="MachineSpec"/> rather than replacing it.</strong> Most of
/// <see cref="MachineSpec"/> maps one-to-one onto <c>RunInstances</c>: <see cref="MachineSpec.ImageRef"/> is
/// <c>ImageId</c>, <see cref="MachineSpec.SizeRef"/> is <c>InstanceType</c>,
/// <see cref="MachineSpec.CloudInit"/> is <c>UserData</c>, and <see cref="MachineSpec.Tags"/> feeds
/// <c>TagSpecification</c>. Keeping it as a member rather than flattening it means that correspondence stays
/// visible.
/// </para>
/// <para>
/// <strong>Where it does not fit, honestly — three places.</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="MachineSpec.Region"/> is filled from the <em>provisioner's</em> region, not from a provisioning
/// parameter. EC2's region lives in the endpoint hostname (<c>ec2.us-east-1.amazonaws.com</c>) <em>and</em> in
/// the SigV4 credential scope, so a per-request region would mean re-pointing the HTTP client and re-scoping
/// every signature mid-flight. This is the one structural way the AWS adapter's request translation is not a
/// pure function of the request, and it is why <see cref="AwsEc2Provisioner.BuildSpec"/> is an instance method
/// where the DigitalOcean and Azure equivalents are static.
/// </description></item>
/// <item><description>
/// <see cref="MachineSpec.SshPublicKey"/> holds raw public key material, and <c>RunInstances</c> cannot consume
/// that: its <c>KeyName</c> parameter takes only the name of a key pair already registered in the account's EC2
/// key-pair store. So the raw key travels here unused-by-the-wire (it is still part of the plan hash, because
/// changing which key an operator intends to install must invalidate a plan) and <see cref="KeyPairName"/>
/// carries what the API actually accepts. Importing a key pair is a separate account-level mutation this
/// adapter deliberately does not perform. This is the same shape as the DigitalOcean adapter's
/// <c>SshKeyFingerprints</c>.
/// </description></item>
/// <item><description>
/// <see cref="MachineSpec.Ingress"/> cannot be honoured at all, because a security group is a resource this
/// adapter does not create. <see cref="SecurityGroupIds"/> names groups that already exist; the plan says
/// plainly that requested ingress rules were not applied. See <see cref="AwsEc2Provisioner"/>'s remarks.
/// </description></item>
/// </list>
/// <para>
/// <strong><see cref="MachineSpec.CloudInit"/> is forwarded, never authored.</strong> Nothing in this assembly
/// generates user-data. If a caller supplies none, none is sent — no bootstrap script, no package install, no
/// game payload. That is what makes "shape I contains no install logic" checkable rather than merely claimed,
/// and it is pinned by a test. EC2 requires user-data to be base64-encoded on the wire; that encoding is a
/// transport detail applied at the request boundary and changes nothing about the content.
/// </para>
/// </remarks>
public sealed record AwsEc2InstanceSpec
{
    /// <summary>Creates a spec.</summary>
    /// <param name="instanceName">The value of the instance's <c>Name</c> tag at the provider.</param>
    /// <param name="machine">The provider-independent machine shape.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException"><paramref name="instanceName"/> is blank.</exception>
    public AwsEc2InstanceSpec(string instanceName, MachineSpec machine, ServyxEc2Tags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(tags);

        InstanceName = instanceName;
        Machine = machine;
        Tags = tags;
    }

    /// <summary>The value of the instance's <c>Name</c> tag at the provider.</summary>
    public string InstanceName { get; }

    /// <summary>The provider-independent machine shape this instance realises.</summary>
    public MachineSpec Machine { get; }

    /// <summary>The mandatory Servyx identity stamped onto the instance and its volumes.</summary>
    public ServyxEc2Tags Tags { get; }

    /// <summary>
    /// The name of an EC2 key pair <em>already registered in the account</em>, which is the only form
    /// <c>RunInstances</c> accepts — see the type remarks.
    /// </summary>
    public string? KeyPairName { get; init; }

    /// <summary>
    /// Ids of security groups that already exist, attached to the instance at launch. Empty means EC2 applies
    /// the VPC's default security group.
    /// </summary>
    /// <remarks>
    /// Naming an existing group is not the same as managing one: this adapter never creates, edits or deletes a
    /// security group, which is why <see cref="ProvisioningCapabilities.FirewallRules"/> is absent and why
    /// nothing here can orphan one.
    /// </remarks>
    public IReadOnlyList<string> SecurityGroupIds { get; init; } = [];

    /// <summary>The VPC subnet to launch into, or <see langword="null"/> to let EC2 choose the default subnet.</summary>
    public string? SubnetId { get; init; }

    /// <summary>
    /// Whether to ask EC2 to assign a public IPv4 address to the instance's primary interface.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>, because shape I's whole output is an <c>ssh://</c> endpoint and a
    /// machine with no public address cannot be one. It is nonetheless a distinct, billable resource
    /// ($0.005/hour since 2024-02-01) rather than a free property of the instance, which is why it is a visible
    /// spec member and a named line in the plan rather than an implicit default.
    /// </remarks>
    public bool AssignPublicIp { get; init; } = true;

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
