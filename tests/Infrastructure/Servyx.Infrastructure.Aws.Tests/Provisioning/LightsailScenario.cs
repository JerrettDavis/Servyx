using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The shared setup every Lightsail provisioning test in this assembly builds on: one substituted Lightsail
/// endpoint, one in-memory secret store holding an AWS key pair, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// The direct counterpart of <see cref="AwsScenario"/>, reusing the same <see cref="AwsApiDouble"/> and
/// <see cref="RecordingSecretStore"/> (both provider-agnostic) but building JSON fixtures instead of XML ones,
/// since Lightsail speaks AWS JSON 1.1 rather than the EC2 Query API.
/// </remarks>
internal sealed class LightsailScenario
{
    /// <summary>The AWS region every test acts in.</summary>
    internal const string Region = "us-east-1";

    /// <summary>The availability zone every test creates into.</summary>
    internal const string AvailabilityZone = "us-east-1a";

    /// <summary>The access key id stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string AccessKeyId = AwsScenario.AccessKeyId;

    /// <summary>The secret access key stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string SecretAccessKey = AwsScenario.SecretAccessKey;

    /// <summary>The Servyx identity every instance in these tests carries.</summary>
    internal const string InstanceId = "srv-0001";

    /// <summary>The provisioning job every instance in these tests carries.</summary>
    internal const string JobId = "job-42";

    /// <summary>The connector every instance in these tests carries.</summary>
    internal const string ConnectorId = "conn-1";

    /// <summary>The Lightsail instance name every test creates - both the identity and the display name.</summary>
    internal const string InstanceName = "palworld-01";

    /// <summary>The public IPv4 the substituted API reports for the created instance.</summary>
    internal const string PublicIp = "203.0.113.7";

    /// <summary>The private IPv4 the substituted API reports for the created instance.</summary>
    internal const string PrivateIp = "10.0.1.15";

    /// <summary>The blueprint every test creates from.</summary>
    internal const string BlueprintId = "amazon_linux_2023";

    /// <summary>The bundle every test creates at, chosen because the price snapshot knows it.</summary>
    internal const string BundleId = "medium_3_0";

    /// <summary>The login username the substituted API reports for the blueprint above.</summary>
    internal const string Username = "ec2-user";

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
    internal AwsLightsailProvisioner Provisioner(bool withCredentials = true, string region = Region)
    {
        if (withCredentials)
        {
            Secrets.Put(AccessKeyIdUrn, AccessKeyId);
            Secrets.Put(SecretAccessKeyUrn, SecretAccessKey);
        }

        return new AwsLightsailProvisioner(
            Api.Client(),
            Secrets,
            new AwsSigningIdentity(AccessKeyIdUrn, SecretAccessKeyUrn),
            region,
            sshCredentialUrn: SshCredentialUrn,
            transportOptions: TransportOptions,
            timeProvider: TimeProvider.System,
            addressPollInterval: TimeSpan.Zero,
            addressPollAttempts: 3);
    }

    /// <summary>A provisioning request for a Palworld-sized instance, mirroring a cloud deployment profile.</summary>
    internal static ProvisioningRequest PalworldInstanceRequest(
        IReadOnlyDictionary<string, string>? overrides = null,
        string? size = BundleId)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = InstanceName,
            ["image"] = BlueprintId,
            ["availabilityZone"] = AvailabilityZone,
            ["keyPair"] = "servyx-deploy",
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

        return new ProvisioningRequest("palworld", "aws-lightsail", ConnectorId, parameters);
    }

    /// <summary>The canonical Servyx tags the adapter stamps on an instance, as Lightsail stores them.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalTags { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
        };

    /// <summary>Renders a tag dictionary as a Lightsail <c>tags</c> JSON array.</summary>
    internal static string TagsJson(IReadOnlyDictionary<string, string>? tags) =>
        "["
        + string.Join(
            ',',
            (tags ?? CanonicalTags)
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $$"""{"key":"{{t.Key}}","value":"{{t.Value}}"}"""))
        + "]";

    /// <summary>One <c>Instance</c> JSON object as <c>GetInstance</c>/<c>GetInstances</c> report it.</summary>
    internal static string InstanceJson(
        string instanceName = InstanceName,
        string state = "running",
        string bundleId = BundleId,
        bool withPublicIp = true,
        bool withPrivateIp = true,
        string? username = Username,
        IReadOnlyDictionary<string, string>? tags = null) =>
        $$"""
        {
            "name": "{{instanceName}}",
            "arn": "arn:aws:lightsail:us-east-1:111122223333:Instance/{{instanceName}}",
            "blueprintId": "{{BlueprintId}}",
            "bundleId": "{{bundleId}}",
            "createdAt": 1785000000.0,
            "location": { "availabilityZone": "{{AvailabilityZone}}", "regionName": "{{Region}}" },
            "state": { "code": 16, "name": "{{state}}" },
            {{(withPublicIp ? $"\"publicIpAddress\": \"{PublicIp}\"," : string.Empty)}}
            {{(withPrivateIp ? $"\"privateIpAddress\": \"{PrivateIp}\"," : string.Empty)}}
            {{(username is null ? string.Empty : $"\"username\": \"{username}\",")}}
            "resourceType": "Instance",
            "tags": {{TagsJson(tags)}}
        }
        """;

    /// <summary>A <c>CreateInstances</c> response envelope: an array of pending operations, never the instance itself.</summary>
    internal static string CreateInstancesJson(string instanceName = InstanceName) =>
        $$"""
        {
            "operations": [
                {
                    "id": "11111111-2222-3333-4444-555555555555",
                    "operationType": "CreateInstance",
                    "resourceName": "{{instanceName}}",
                    "resourceType": "Instance",
                    "status": "Started",
                    "isTerminal": false
                }
            ]
        }
        """;

    /// <summary>A <c>GetInstance</c> response envelope.</summary>
    internal static string GetInstanceJson(string? instanceJson = null) =>
        $$"""{ "instance": {{instanceJson ?? InstanceJson()}} }""";

    /// <summary>A <c>GetInstances</c> response envelope, optionally carrying a <c>nextPageToken</c>.</summary>
    internal static string GetInstancesJson(string? nextPageToken = null, params string[] instances)
    {
        var body = instances.Length == 0 ? [InstanceJson()] : instances;
        var next = nextPageToken is null ? string.Empty : $""", "nextPageToken": "{nextPageToken}" """;
        return $$"""{ "instances": [{{string.Join(',', body)}}]{{next}} }""";
    }

    /// <summary>A <c>DeleteInstance</c> response envelope.</summary>
    internal static string DeleteInstanceJson(string instanceName = InstanceName) =>
        $$"""
        {
            "operations": [
                {
                    "id": "66666666-7777-8888-9999-000000000000",
                    "operationType": "DeleteInstance",
                    "resourceName": "{{instanceName}}",
                    "resourceType": "Instance",
                    "status": "Started",
                    "isTerminal": false
                }
            ]
        }
        """;

    /// <summary>A Lightsail AWS-JSON-1.1 error document, as every action returns one alongside a 4xx status.</summary>
    internal static string ErrorJson(string type, string message) =>
        $$"""{ "__type": "{{type}}", "message": "{{message}}" }""";

    /// <summary>Answers every request with the fixed instance payload, routed by the <c>X-Amz-Target</c> action name.</summary>
    internal void RouteReadOnly(string? instanceJson = null) =>
        Api.Responder = request => request.LightsailAction switch
        {
            "GetInstance" => AwsApiDouble.Json(HttpStatusCode.OK, GetInstanceJson(instanceJson)),
            "GetInstances" => AwsApiDouble.Json(HttpStatusCode.OK, GetInstancesJson(null, instanceJson ?? InstanceJson())),
            _ => throw new InvalidOperationException(
                $"A read-only path issued a mutating Lightsail action '{request.LightsailAction}'."),
        };

    /// <summary>The Lightsail error type name used for a resource-not-found response, spelled out here as a
    /// literal because <c>LightsailErrorCodes</c> is internal to the production assembly and this test project
    /// has no <c>InternalsVisibleTo</c> access to it - the same convention <c>AwsScenario</c> follows for EC2's
    /// <c>InvalidInstanceID.NotFound</c>.</summary>
    internal const string NotFoundErrorType = "NotFoundException";

    /// <summary>Answers every read as Lightsail does for an instance name it does not know.</summary>
    internal void RouteMissingInstance() =>
        Api.Responder = request => request.LightsailAction is "GetInstance" or "DeleteInstance"
            ? AwsApiDouble.Json(
                HttpStatusCode.BadRequest,
                ErrorJson(NotFoundErrorType, $"The instance name '{InstanceName}' does not exist"))
            : throw new InvalidOperationException(
                $"Unexpected Lightsail action '{request.LightsailAction}' for a missing-instance route.");

    /// <summary>
    /// Routes the standard create-then-observe exchange: <c>CreateInstances</c> answers with operations only,
    /// then <c>GetInstance</c> answers with the running, addressed instance.
    /// </summary>
    internal void RouteSuccessfulCreate(string? createJson = null, string? getInstanceJson = null) =>
        Api.Responder = request => request.LightsailAction switch
        {
            "CreateInstances" => AwsApiDouble.Json(HttpStatusCode.OK, createJson ?? CreateInstancesJson()),
            "GetInstance" => AwsApiDouble.Json(HttpStatusCode.OK, getInstanceJson ?? GetInstanceJson()),
            _ => throw new InvalidOperationException(
                $"Unexpected Lightsail action '{request.LightsailAction}' during a create."),
        };

    /// <summary>Creates an instance through the full operation path and hands back the resource it produced.</summary>
    internal async Task<ProvisionedResource> CreateAsync(ProvisioningRequest? request = null)
    {
        RouteSuccessfulCreate();

        var provisioner = Provisioner();
        var spec = provisioner.BuildSpec(request ?? PalworldInstanceRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
    }

    /// <summary>The handle Servyx would have recorded for the created instance.</summary>
    internal static ResourceHandle RecordedHandle(
        string providerResourceId = InstanceName,
        string region = Region,
        string provisionerId = AwsLightsailProvisioner.Id) =>
        new(
            provisionerId,
            providerResourceId,
            region,
            new Dictionary<string, string>(CanonicalTags, StringComparer.Ordinal));
}
