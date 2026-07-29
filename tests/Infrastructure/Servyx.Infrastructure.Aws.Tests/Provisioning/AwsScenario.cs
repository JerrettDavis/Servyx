using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The shared setup every provisioning test in this assembly builds on: one substituted EC2 endpoint, one
/// in-memory secret store holding an AWS key pair, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// Kept as a scenario object rather than a base class so a test can reach the API double and the secret store
/// directly and assert on what the adapter actually did — which requests it made, which secrets it resolved,
/// what it signed — rather than only on what it returned.
/// </remarks>
internal sealed class AwsScenario
{
    /// <summary>The AWS region every test acts in.</summary>
    internal const string Region = "us-east-1";

    /// <summary>The access key id stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string AccessKeyId = "AKIA_SERVYX_TESTKEYID_must_never_appear_outside_a_credential_field";

    /// <summary>The secret access key stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string SecretAccessKey = "servyxTESTSECRETKEY_must_never_appear_anywhere_at_all_not_even_on_the_wire";

    /// <summary>The Servyx identity every instance in these tests carries.</summary>
    internal const string InstanceId = "srv-0001";

    /// <summary>The provisioning job every instance in these tests carries.</summary>
    internal const string JobId = "job-42";

    /// <summary>The connector every instance in these tests carries.</summary>
    internal const string ConnectorId = "conn-1";

    /// <summary>The EC2 instance id the substituted API hands back from a launch.</summary>
    internal const string Ec2InstanceId = "i-0abcdef1234567890";

    /// <summary>The EBS volume id the substituted API reports for the launched instance's root disk.</summary>
    internal const string VolumeId = "vol-0fedcba9876543210";

    /// <summary>The public IPv4 the substituted API reports for the launched instance.</summary>
    internal const string PublicIp = "203.0.113.7";

    /// <summary>The private IPv4 the substituted API reports for the launched instance.</summary>
    internal const string PrivateIp = "10.0.1.15";

    /// <summary>The AMI every test launches from.</summary>
    internal const string ImageId = "ami-0abcdef1234567890";

    /// <summary>The instance type every test launches, chosen because the price snapshot knows it.</summary>
    internal const string InstanceType = "t3.medium";

    /// <summary>The availability zone the substituted API reports.</summary>
    internal const string AvailabilityZone = "us-east-1a";

    /// <summary>The URN the AWS access key id lives at.</summary>
    internal static SecretUrn AccessKeyIdUrn { get; } = SecretUrn.Create("global", "aws", "api", "access-key-id");

    /// <summary>The URN the AWS secret access key lives at.</summary>
    internal static SecretUrn SecretAccessKeyUrn { get; } = SecretUrn.Create("global", "aws", "api", "secret-access-key");

    /// <summary>The URN of the SSH private key produced descriptors point at. Never an AWS credential.</summary>
    internal const string SshCredentialUrn = "secret://connector/conn-1/ssh/private-key";

    internal AwsApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    /// <summary>Transport options a caller hands through to the SSH transport, untouched by this adapter.</summary>
    internal Dictionary<string, string> TransportOptions { get; } = new(StringComparer.Ordinal)
    {
        ["trustPolicy"] = "trustOnFirstUse",
        ["declaredChannels"] = "Exec,FileRead,FileWrite",
    };

    /// <summary>Builds a provisioner over this scenario's API double and secret store.</summary>
    /// <param name="withCredentials">Whether to seed the secret store with the fake AWS key pair.</param>
    /// <param name="sshUsername">The username produced descriptors authenticate as.</param>
    /// <param name="region">The region the provisioner acts on.</param>
    /// <param name="statePollAttempts">
    /// How many lifecycle-state reads the instance-type change path makes per step before giving up. Only the
    /// update-execution suite varies it; every other test builds a provisioner that never polls a state at all,
    /// so the default is the same small number the address poll uses and the wait is zero-length either way.
    /// </param>
    internal AwsEc2Provisioner Provisioner(
        bool withCredentials = true,
        string sshUsername = AwsEc2Provisioner.DefaultSshUsername,
        string region = Region,
        int statePollAttempts = 3)
    {
        if (withCredentials)
        {
            Secrets.Put(AccessKeyIdUrn, AccessKeyId);
            Secrets.Put(SecretAccessKeyUrn, SecretAccessKey);
        }

        return new AwsEc2Provisioner(
            Api.Client(),
            Secrets,
            new AwsSigningIdentity(AccessKeyIdUrn, SecretAccessKeyUrn),
            region,
            sshCredentialUrn: SshCredentialUrn,
            transportOptions: TransportOptions,
            sshUsername: sshUsername,
            timeProvider: TimeProvider.System,
            addressPollInterval: TimeSpan.Zero,
            addressPollAttempts: 3,
            statePollInterval: TimeSpan.Zero,
            statePollAttempts: statePollAttempts);
    }

    /// <summary>A provisioning request for a Palworld-sized instance, mirroring a cloud deployment profile.</summary>
    internal static ProvisioningRequest PalworldInstanceRequest(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? size = InstanceType)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = "palworld-01",
            ["image"] = ImageId,
            ["keyPair"] = "servyx-deploy",
            ["subnetId"] = "subnet-0123456789abcdef0",
            ["securityGroupId:0"] = "sg-0123456789abcdef0",
            ["sshPublicKey"] = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleKeyMaterial servyx",
        };

        if (size is not null)
        {
            parameters["size"] = size;
        }

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            parameters[pair.Key] = pair.Value;
        }

        return new ProvisioningRequest("palworld", "aws-ec2", ConnectorId, parameters);
    }

    /// <summary>The canonical Servyx tags the adapter stamps on an instance, as EC2 stores them.</summary>
    /// <remarks>
    /// Written out as the literal keys, with the dots intact, because that is the whole tag-encoding finding
    /// for this provider: unlike DigitalOcean there is no transformation between what Servyx spells and what
    /// the provider stores.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> CanonicalInstanceTags { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = ServyxEc2Tags.RoleInstance,
            ["Name"] = "palworld-01",
        };

    /// <summary>The canonical Servyx tags the adapter stamps on the launch's volumes.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalVolumeTags { get; } =
        new Dictionary<string, string>(CanonicalInstanceTags, StringComparer.Ordinal)
        {
            ["servyx.role"] = ServyxEc2Tags.RoleVolume,
        };

    /// <summary>Renders a tag dictionary as an EC2 <c>tagSet</c> element.</summary>
    internal static string TagSetXml(IReadOnlyDictionary<string, string>? tags) =>
        "<tagSet>"
        + string.Join(
            string.Empty,
            (tags ?? CanonicalInstanceTags)
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $"<item><key>{t.Key}</key><value>{t.Value}</value></item>"))
        + "</tagSet>";

    /// <summary>One <c>instancesSet/item</c> element as the EC2 Query API reports it.</summary>
    /// <remarks>
    /// The three trailing parameters exist for the maintenance suite and are additive: <paramref name="imageId"/>
    /// lets a test present an instance running a different AMI from the one a request names, and
    /// <paramref name="deleteOnTermination"/>/<paramref name="withBlockDevice"/> reproduce the three states of
    /// the one flag that decides what an update plan may claim about a caller's data — set true, set false, and
    /// not reported at all. The default is the flag AWS's stock AMIs set, so every pre-existing caller is
    /// unaffected.
    /// </remarks>
    internal static string InstanceXml(
        string instanceId = Ec2InstanceId,
        string state = "running",
        string instanceType = InstanceType,
        bool withPublicIp = true,
        bool withPrivateIp = true,
        IReadOnlyDictionary<string, string>? tags = null,
        string volumeId = VolumeId,
        string imageId = ImageId,
        string? deleteOnTermination = "true",
        bool withBlockDevice = true) =>
        "<item>"
        + $"<instanceId>{instanceId}</instanceId>"
        + $"<imageId>{imageId}</imageId>"
        + $"<instanceState><code>16</code><name>{state}</name></instanceState>"
        + (withPrivateIp ? $"<privateIpAddress>{PrivateIp}</privateIpAddress>" : string.Empty)
        + (withPublicIp ? $"<ipAddress>{PublicIp}</ipAddress>" : string.Empty)
        + $"<instanceType>{instanceType}</instanceType>"
        + "<launchTime>2026-07-27T10:00:00.000Z</launchTime>"
        + $"<placement><availabilityZone>{AvailabilityZone}</availabilityZone></placement>"
        + (withBlockDevice
            ? "<blockDeviceMapping><item><deviceName>/dev/xvda</deviceName>"
              + $"<ebs><volumeId>{volumeId}</volumeId><status>attached</status>"
              + (deleteOnTermination is null
                  ? string.Empty
                  : $"<deleteOnTermination>{deleteOnTermination}</deleteOnTermination>")
              + "</ebs></item></blockDeviceMapping>"
            : "<blockDeviceMapping></blockDeviceMapping>")
        + TagSetXml(tags)
        + "</item>";

    /// <summary>A <c>RunInstancesResponse</c> envelope.</summary>
    internal static string RunInstancesXml(string? instanceXml = null) =>
        Envelope(
            "RunInstancesResponse",
            "<reservationId>r-0123456789abcdef0</reservationId>"
            + "<instancesSet>"
            + (instanceXml ?? InstanceXml(withPublicIp: false, withPrivateIp: false, state: "pending"))
            + "</instancesSet>");

    /// <summary>A <c>DescribeInstancesResponse</c> envelope, optionally carrying a <c>nextToken</c>.</summary>
    internal static string DescribeInstancesXml(string? nextToken = null, params string[] instances) =>
        Envelope(
            "DescribeInstancesResponse",
            "<reservationSet><item><reservationId>r-0123456789abcdef0</reservationId><instancesSet>"
            + string.Join(string.Empty, instances.Length == 0 ? [InstanceXml()] : instances)
            + "</instancesSet></item></reservationSet>"
            + (nextToken is null ? string.Empty : $"<nextToken>{nextToken}</nextToken>"));

    /// <summary>One <c>volumeSet/item</c> element as the EC2 Query API reports it.</summary>
    internal static string VolumeXml(
        string volumeId = VolumeId,
        string state = "available",
        int sizeGib = 30,
        string? attachedTo = null,
        IReadOnlyDictionary<string, string>? tags = null) =>
        "<item>"
        + $"<volumeId>{volumeId}</volumeId>"
        + $"<size>{sizeGib.ToString(CultureInfo.InvariantCulture)}</size>"
        + $"<status>{state}</status>"
        + $"<availabilityZone>{AvailabilityZone}</availabilityZone>"
        + "<createTime>2026-07-27T10:00:00.000Z</createTime>"
        + "<attachmentSet>"
        + (attachedTo is null ? string.Empty : $"<item><instanceId>{attachedTo}</instanceId><status>attached</status></item>")
        + "</attachmentSet>"
        + TagSetXml(tags ?? CanonicalVolumeTags)
        + "</item>";

    /// <summary>A <c>DescribeVolumesResponse</c> envelope, optionally carrying a <c>nextToken</c>.</summary>
    internal static string DescribeVolumesXml(string? nextToken = null, params string[] volumes) =>
        Envelope(
            "DescribeVolumesResponse",
            "<volumeSet>"
            + string.Join(string.Empty, volumes)
            + "</volumeSet>"
            + (nextToken is null ? string.Empty : $"<nextToken>{nextToken}</nextToken>"));

    /// <summary>A <c>TerminateInstancesResponse</c> envelope.</summary>
    internal static string TerminateInstancesXml(string instanceId = Ec2InstanceId) =>
        Envelope(
            "TerminateInstancesResponse",
            $"<instancesSet><item><instanceId>{instanceId}</instanceId>"
            + "<currentState><code>32</code><name>shutting-down</name></currentState>"
            + "<previousState><code>16</code><name>running</name></previousState></item></instancesSet>");

    /// <summary>A <c>DeleteVolumeResponse</c> envelope.</summary>
    internal static string DeleteVolumeXml() => Envelope("DeleteVolumeResponse", "<return>true</return>");

    /// <summary>An EC2 error document, as the Query API returns one alongside a 4xx status.</summary>
    internal static string ErrorXml(string code, string message) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + "<Response><Errors><Error>"
        + $"<Code>{code}</Code><Message>{message}</Message>"
        + "</Error></Errors><RequestID>abcd1234-0000-0000-0000-000000000000</RequestID></Response>";

    /// <summary>Answers every read with one instance payload, and fails loudly on anything that mutates.</summary>
    /// <remarks>
    /// The "anything else" branch is the assertion, not the convenience: a read-only path must issue reads and
    /// nothing else, so a POST from it fails the test where it happens rather than being silently answered.
    /// </remarks>
    internal void RouteReadOnly(string? xml = null) =>
        Api.Responder = request => request.Method == HttpMethod.Get
            ? AwsApiDouble.Xml(HttpStatusCode.OK, xml ?? DescribeInstancesXml())
            : throw new InvalidOperationException(
                $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'.");

    /// <summary>Answers every read as EC2 does for an instance id it does not know.</summary>
    internal void RouteMissingInstance() =>
        Api.Responder = request => request.Method == HttpMethod.Get
            ? AwsApiDouble.Xml(
                HttpStatusCode.BadRequest,
                ErrorXml("InvalidInstanceID.NotFound", $"The instance ID '{Ec2InstanceId}' does not exist"))
            : throw new InvalidOperationException(
                $"A read-only path issued a mutating {request.Method} request to '{request.Uri}'.");

    /// <summary>
    /// Routes the standard launch-then-observe exchange: one POST that returns a pending, address-less
    /// instance, then GETs that return it running with an address.
    /// </summary>
    /// <remarks>
    /// Deliberately address-less on the launch response, because that is what EC2 actually returns for a
    /// pending instance and it is the only way the address-polling path gets exercised at all.
    /// </remarks>
    internal void RouteSuccessfulLaunch(string? runXml = null, string? describeXml = null) =>
        Api.Responder = request => request.Method == HttpMethod.Post
            ? AwsApiDouble.Xml(HttpStatusCode.OK, runXml ?? RunInstancesXml())
            : AwsApiDouble.Xml(HttpStatusCode.OK, describeXml ?? DescribeInstancesXml());

    /// <summary>Launches an instance through the full operation path and hands back the resource it produced.</summary>
    internal async Task<ProvisionedResource> CreateAsync(ProvisioningRequest? request = null)
    {
        RouteSuccessfulLaunch();

        var provisioner = Provisioner();
        var spec = provisioner.BuildSpec(request ?? PalworldInstanceRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
    }

    /// <summary>
    /// The handle Servyx would have recorded for the launched instance.
    /// </summary>
    internal static ResourceHandle RecordedHandle(
        string providerResourceId = Ec2InstanceId,
        string region = Region,
        string provisionerId = AwsEc2Provisioner.Id) =>
        new(
            provisionerId,
            providerResourceId,
            region,
            new Dictionary<string, string>(CanonicalInstanceTags, StringComparer.Ordinal));

    /// <summary>
    /// The handle Servyx would have recorded for the launched instance <em>if it had also recorded what the
    /// instance was launched as</em> — that is, carrying the two descriptive expectation tags a drift check
    /// needs.
    /// </summary>
    /// <remarks>
    /// A separate helper rather than extra parameters on <see cref="RecordedHandle"/> because the two answer
    /// different questions. <see cref="RecordedHandle"/> is the handle this adapter actually produces today, and
    /// every existing test that asserts on a handle's tags is asserting about that one. This is the handle a
    /// caller gets when it supplies <c>tag:servyx.size</c> / <c>tag:servyx.image</c> provisioning parameters,
    /// which is the only way to give a drift check something to compare against — see
    /// <c>ServyxTagKeys.Size</c>'s remarks. Passing <see langword="null"/> for either reproduces the weaker
    /// handle, which must report that aspect as unverifiable rather than as matching.
    /// </remarks>
    internal static ResourceHandle MaintenanceHandle(
        string? size = InstanceType,
        string? image = ImageId,
        string providerResourceId = Ec2InstanceId,
        string region = Region,
        string provisionerId = AwsEc2Provisioner.Id)
    {
        var tags = new Dictionary<string, string>(CanonicalInstanceTags, StringComparer.Ordinal);

        if (size is not null)
        {
            tags[ServyxTagKeys.Size] = size;
        }

        if (image is not null)
        {
            tags[ServyxTagKeys.Image] = image;
        }

        return new ResourceHandle(provisionerId, providerResourceId, region, tags);
    }

    private static string Envelope(string root, string inner) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + $"<{root} xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\">"
        + "<requestId>abcd1234-0000-0000-0000-000000000000</requestId>"
        + inner
        + $"</{root}>";
}
