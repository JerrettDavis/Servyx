using System.Globalization;
using System.Net;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Aws.Provisioning;

namespace Servyx.Infrastructure.Aws.Tests.Provisioning;

/// <summary>
/// The shared setup every ECS/Fargate provisioning test in this assembly builds on: one substituted ECS
/// endpoint, one in-memory secret store holding an AWS key pair, and one provisioner wired to both.
/// </summary>
/// <remarks>
/// <para>
/// The direct counterpart of <see cref="LightsailScenario"/>, reusing the same <see cref="AwsApiDouble"/> and
/// <see cref="RecordingSecretStore"/> and building AWS JSON 1.1 fixtures the same way. Two things are genuinely
/// different and both are the shape of the provider rather than of the test.
/// </para>
/// <para>
/// First, a Fargate deployment is three objects — a task definition revision, a service, and a task — so a create
/// fixture has to route four distinct actions where a Lightsail create routes two. Second, ECS reports an absent
/// service as a <c>failures</c> entry on a <em>successful</em> response rather than as an error status, so
/// <see cref="MissingServiceJson"/> is a 200 and not a 400.
/// </para>
/// <para>
/// Nothing here opens a socket. A test that expects <em>no</em> request proves it by asserting
/// <see cref="AwsApiDouble.Requests"/> is empty, which is a stronger claim than "the call failed".
/// </para>
/// </remarks>
internal sealed class EcsScenario
{
    /// <summary>The AWS region every test acts in.</summary>
    internal const string Region = "us-east-1";

    /// <summary>The AWS account id every ARN in these fixtures names.</summary>
    internal const string AccountId = "111122223333";

    /// <summary>The ECS cluster every test creates into and sweeps. Pre-existing; this adapter never creates one.</summary>
    internal const string Cluster = "servyx-games";

    /// <summary>The access key id stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string AccessKeyId = AwsScenario.AccessKeyId;

    /// <summary>The secret access key stored in <see cref="Secrets"/>. Deliberately distinctive so a leak is findable.</summary>
    internal const string SecretAccessKey = AwsScenario.SecretAccessKey;

    internal const string InstanceId = "srv-fargate-0001";
    internal const string JobId = "job-fargate-7";
    internal const string ConnectorId = "conn-fargate-1";

    /// <summary>The ECS service's name at the provider, and the default task definition family.</summary>
    internal const string ServiceName = "palworld-fargate";

    /// <summary>The service's ARN — the stable identity this adapter records as its ProviderResourceId.</summary>
    internal const string ServiceArn =
        "arn:aws:ecs:" + Region + ":" + AccountId + ":service/" + Cluster + "/" + ServiceName;

    /// <summary>A service ARN in a cluster this provisioner was not configured with. Used to prove the narrowing.</summary>
    internal const string ForeignClusterServiceArn =
        "arn:aws:ecs:" + Region + ":" + AccountId + ":service/someone-elses-cluster/their-service";

    /// <summary>The task definition revision the substituted ECS reports for a registration.</summary>
    internal const string TaskDefinitionArn =
        "arn:aws:ecs:" + Region + ":" + AccountId + ":task-definition/" + ServiceName + ":7";

    /// <summary>The task ARN the substituted ECS reports for the service's running task.</summary>
    internal const string TaskArn =
        "arn:aws:ecs:" + Region + ":" + AccountId + ":task/" + Cluster + "/0123456789abcdef0123456789abcdef";

    /// <summary>The OCI image the container runs. Note: not an AMI id.</summary>
    internal const string Image = "docker.io/thijsvanloef/palworld-server-docker:latest";

    /// <summary>The pre-existing EFS file system holding the saves. Never created or destroyed by Servyx.</summary>
    internal const string FileSystemId = "fs-0123456789abcdef0";

    /// <summary>The pre-existing EFS access point the task mounts through.</summary>
    internal const string AccessPointId = "fsap-0123456789abcdef0";

    /// <summary>Where the EFS volume is mounted inside the container.</summary>
    internal const string MountPath = "/palworld";

    /// <summary>The subnet the task's network interface is placed in.</summary>
    internal const string SubnetId = "subnet-0aaaaaaaaaaaaaaaa";

    /// <summary>A second subnet, so the ordering of an indexed parameter list is testable.</summary>
    internal const string SecondSubnetId = "subnet-0bbbbbbbbbbbbbbbb";

    /// <summary>The pre-existing security group the task joins. Referenced, never created or modified.</summary>
    internal const string SecurityGroupId = "sg-0ccccccccccccccc0";

    /// <summary>The IAM role ECS assumes to pull the image and write logs. Referenced, never created.</summary>
    internal const string ExecutionRoleArn = "arn:aws:iam::" + AccountId + ":role/ecsTaskExecutionRole";

    /// <summary>The CloudWatch Logs group the container writes to. Referenced, never created.</summary>
    internal const string LogGroup = "/servyx/palworld";

    /// <summary>The private IPv4 the substituted ECS reports for the task's network interface.</summary>
    internal const string PrivateIp = "10.0.1.15";

    /// <summary>The task ENI's id. Present so a test can prove it is never resolved to a public address.</summary>
    internal const string NetworkInterfaceId = "eni-0ddddddddddddddd0";

    /// <summary>The task CPU reservation these tests provision, in ECS CPU units. 1024 units is one vCPU.</summary>
    internal const int CpuUnits = 1024;

    /// <summary>The task memory reservation these tests provision, in MiB.</summary>
    internal const int MemoryMib = 2048;

    /// <summary>The URN the AWS access key id lives at.</summary>
    internal static SecretUrn AccessKeyIdUrn { get; } = SecretUrn.Create("global", "aws", "api", "access-key-id");

    /// <summary>The URN the AWS secret access key lives at.</summary>
    internal static SecretUrn SecretAccessKeyUrn { get; } = SecretUrn.Create("global", "aws", "api", "secret-access-key");

    internal AwsApiDouble Api { get; } = new();

    internal RecordingSecretStore Secrets { get; } = new();

    /// <summary>Builds a provisioner over this scenario's API double and secret store.</summary>
    /// <param name="withCredentials">Whether the AWS key pair is present in the store.</param>
    /// <param name="region">The region the provisioner acts on.</param>
    /// <param name="cluster">The cluster the provisioner creates into and sweeps.</param>
    /// <param name="pollAttempts">How many readiness/deletion polls before giving up.</param>
    internal AwsEcsFargateProvisioner Provisioner(
        bool withCredentials = true,
        string region = Region,
        string cluster = Cluster,
        int pollAttempts = 3)
    {
        if (withCredentials)
        {
            Secrets.Put(AccessKeyIdUrn, AccessKeyId);
            Secrets.Put(SecretAccessKeyUrn, SecretAccessKey);
        }

        return new AwsEcsFargateProvisioner(
            Api.Client(),
            Secrets,
            new AwsSigningIdentity(AccessKeyIdUrn, SecretAccessKeyUrn),
            region,
            cluster,
            timeProvider: TimeProvider.System,
            pollInterval: TimeSpan.Zero,
            pollAttempts: pollAttempts);
    }

    /// <summary>A provisioning request for a Palworld Fargate service with the mandatory mount and subnet configured.</summary>
    /// <remarks>
    /// An override whose value is empty removes the key, so a test can prove a required parameter is required —
    /// except for the <c>ingress:</c> keys, whose empty value legitimately means "any source".
    /// </remarks>
    internal static ProvisioningRequest PalworldRequest(IReadOnlyDictionary<string, string>? overrides = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId,
            ["jobId"] = JobId,
            ["connectorId"] = ConnectorId,
            ["name"] = ServiceName,
            ["image"] = Image,
            ["fileSystemId"] = FileSystemId,
            ["efsAccessPointId"] = AccessPointId,
            ["mountPath"] = MountPath,
            ["subnetId:0"] = SubnetId,
            ["securityGroupId:0"] = SecurityGroupId,
            ["executionRoleArn"] = ExecutionRoleArn,
            ["logGroup"] = LogGroup,
            ["cpu"] = CpuUnits.ToString(CultureInfo.InvariantCulture),
            ["memory"] = MemoryMib.ToString(CultureInfo.InvariantCulture),
            ["ingress:8211/udp"] = string.Empty,
            ["ingress:25575/tcp"] = string.Empty,
        };

        foreach (var pair in overrides ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (pair.Value.Length == 0
                && parameters.ContainsKey(pair.Key)
                && !pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                parameters.Remove(pair.Key);
                continue;
            }

            parameters[pair.Key] = pair.Value;
        }

        return new ProvisioningRequest("palworld", AwsEcsFargateProvisioner.Id, ConnectorId, parameters);
    }

    /// <summary>The spec the request above produces, for tests that want the typed overloads.</summary>
    internal AwsFargateServiceSpec Spec(IReadOnlyDictionary<string, string>? overrides = null) =>
        Provisioner().BuildSpec(PalworldRequest(overrides));

    /// <summary>The canonical Servyx tags stamped on a service, including the four pointer keys.</summary>
    internal static IReadOnlyDictionary<string, string> CanonicalTags { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["servyx.managed"] = "true",
            ["servyx.instance-id"] = InstanceId,
            ["servyx.job-id"] = JobId,
            ["servyx.connector-id"] = ConnectorId,
            ["servyx.role"] = "ecs-service",
            ["servyx.aws-ecs-cluster"] = Cluster,
            ["servyx.aws-ecs-task-definition-family"] = ServiceName,
            ["servyx.aws-efs-file-system"] = FileSystemId,
            ["servyx.aws-efs-access-point"] = AccessPointId,
        };

    /// <summary>Renders a tag dictionary as an ECS <c>tags</c> JSON array.</summary>
    internal static string TagsJson(IReadOnlyDictionary<string, string>? tags) =>
        "["
        + string.Join(
            ',',
            (tags ?? CanonicalTags)
                .OrderBy(t => t.Key, StringComparer.Ordinal)
                .Select(t => $$"""{"key":"{{t.Key}}","value":"{{t.Value}}"}"""))
        + "]";

    /// <summary>One <c>Service</c> JSON object, as every ECS action that touches one reports it.</summary>
    internal static string ServiceJson(
        string arn = ServiceArn,
        string serviceName = ServiceName,
        string status = "ACTIVE",
        int runningCount = 1,
        string? taskDefinition = TaskDefinitionArn,
        IReadOnlyDictionary<string, string>? tags = null) =>
        $$"""
        {
            "serviceArn": "{{arn}}",
            "serviceName": "{{serviceName}}",
            "clusterArn": "arn:aws:ecs:{{Region}}:{{AccountId}}:cluster/{{Cluster}}",
            "status": "{{status}}",
            "desiredCount": 1,
            "runningCount": {{runningCount.ToString(CultureInfo.InvariantCulture)}},
            "pendingCount": 0,
            "launchType": "FARGATE",
            {{(taskDefinition is null ? string.Empty : $"\"taskDefinition\": \"{taskDefinition}\",")}}
            "createdAt": 1785000000.0,
            "tags": {{TagsJson(tags)}}
        }
        """;

    /// <summary>A <c>DescribeServices</c>/<c>CreateService</c>-shaped envelope carrying services and no failures.</summary>
    internal static string DescribeServicesJson(params string[] services)
    {
        var body = services.Length == 0 ? [ServiceJson()] : services;
        return $$"""{ "services": [{{string.Join(',', body)}}], "failures": [] }""";
    }

    /// <summary>
    /// The answer ECS gives for a service it does not know: HTTP 200 with the ARN moved into <c>failures</c>.
    /// </summary>
    internal static string MissingServiceJson(string arn = ServiceArn) =>
        $$"""{ "services": [], "failures": [{ "arn": "{{arn}}", "reason": "MISSING" }] }""";

    /// <summary>A <c>CreateService</c>/<c>DeleteService</c> response envelope.</summary>
    internal static string ServiceEnvelopeJson(string? serviceJson = null) =>
        $$"""{ "service": {{serviceJson ?? ServiceJson()}} }""";

    /// <summary>A <c>ListServices</c> response envelope, optionally carrying a <c>nextToken</c>.</summary>
    internal static string ListServicesJson(string? nextToken = null, params string[] arns)
    {
        var body = arns.Length == 0 ? [ServiceArn] : arns;
        var next = nextToken is null ? string.Empty : $""", "nextToken": "{nextToken}" """;
        return $$"""{ "serviceArns": [{{string.Join(',', body.Select(a => $"\"{a}\""))}}]{{next}} }""";
    }

    /// <summary>One <c>TaskDefinition</c> JSON object.</summary>
    internal static string TaskDefinitionJson(
        string arn = TaskDefinitionArn,
        string cpu = "1024",
        string memory = "2048",
        string status = "ACTIVE") =>
        $$"""
        {
            "taskDefinitionArn": "{{arn}}",
            "family": "{{ServiceName}}",
            "revision": 7,
            "cpu": "{{cpu}}",
            "memory": "{{memory}}",
            "status": "{{status}}",
            "networkMode": "awsvpc",
            "requiresCompatibilities": ["FARGATE"]
        }
        """;

    /// <summary>A <c>RegisterTaskDefinition</c>/<c>DescribeTaskDefinition</c> response envelope.</summary>
    internal static string TaskDefinitionEnvelopeJson(string? taskDefinitionJson = null) =>
        $$"""{ "taskDefinition": {{taskDefinitionJson ?? TaskDefinitionJson()}} }""";

    /// <summary>A <c>ListTasks</c> response envelope.</summary>
    internal static string ListTasksJson(params string[] arns)
    {
        var body = arns.Length == 0 ? [TaskArn] : arns;
        return $$"""{ "taskArns": [{{string.Join(',', body.Select(a => $"\"{a}\""))}}] }""";
    }

    /// <summary>One <c>Task</c> JSON object, with its elastic network interface attachment.</summary>
    internal static string TaskJson(
        string lastStatus = "RUNNING",
        string desiredStatus = "RUNNING",
        string? privateIp = PrivateIp,
        string? stoppedReason = null) =>
        $$"""
        {
            "taskArn": "{{TaskArn}}",
            "lastStatus": "{{lastStatus}}",
            "desiredStatus": "{{desiredStatus}}",
            {{(stoppedReason is null ? string.Empty : $"\"stoppedReason\": \"{stoppedReason}\",")}}
            "attachments": [
                {
                    "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    "type": "ElasticNetworkInterface",
                    "status": "ATTACHED",
                    "details": [
                        { "name": "subnetId", "value": "{{SubnetId}}" },
                        { "name": "networkInterfaceId", "value": "{{NetworkInterfaceId}}" }
                        {{(privateIp is null ? string.Empty : $", {{ \"name\": \"privateIPv4Address\", \"value\": \"{privateIp}\" }}")}}
                    ]
                }
            ]
        }
        """;

    /// <summary>A <c>DescribeTasks</c> response envelope.</summary>
    internal static string DescribeTasksJson(params string[] tasks)
    {
        var body = tasks.Length == 0 ? [TaskJson()] : tasks;
        return $$"""{ "tasks": [{{string.Join(',', body)}}], "failures": [] }""";
    }

    /// <summary>An ECS AWS-JSON-1.1 error document, as every action returns one alongside a 4xx status.</summary>
    internal static string ErrorJson(string type, string message) =>
        $$"""{ "__type": "{{type}}", "message": "{{message}}" }""";

    /// <summary>The ECS error type name for a cluster the account does not have.</summary>
    /// <remarks>
    /// Spelled out as a literal because <c>EcsErrorCodes</c> is internal to the production assembly and this test
    /// project has no <c>InternalsVisibleTo</c> access to it — the same convention <see cref="LightsailScenario"/>
    /// follows for Lightsail's <c>NotFoundException</c>.
    /// </remarks>
    internal const string ClusterNotFoundErrorType = "ClusterNotFoundException";

    /// <summary>The ECS error type name for a service a mutating call names and cannot find.</summary>
    internal const string ServiceNotFoundErrorType = "ServiceNotFoundException";

    /// <summary>
    /// Routes the standard create exchange: register, create, list one task, describe it as RUNNING.
    /// </summary>
    internal void RouteSuccessfulCreate(
        string? serviceJson = null,
        string? taskJson = null,
        string? listTasksJson = null) =>
        Api.Responder = request => request.EcsAction switch
        {
            "RegisterTaskDefinition" => AwsApiDouble.Json(HttpStatusCode.OK, TaskDefinitionEnvelopeJson()),
            "CreateService" => AwsApiDouble.Json(HttpStatusCode.OK, ServiceEnvelopeJson(serviceJson)),
            "ListTasks" => AwsApiDouble.Json(HttpStatusCode.OK, listTasksJson ?? ListTasksJson()),
            "DescribeTasks" => AwsApiDouble.Json(HttpStatusCode.OK, DescribeTasksJson(taskJson ?? TaskJson())),
            _ => throw new InvalidOperationException(
                $"Unexpected ECS action '{request.EcsAction}' during a create. This adapter is only ever "
                + "supposed to register a task definition, create a service, and read tasks - never an "
                + "elasticfilesystem, ec2, iam or logs call."),
        };

    /// <summary>
    /// Routes the standard read exchange a refresh performs: describe the service, its tasks, and its task
    /// definition.
    /// </summary>
    internal void RouteReadOnly(
        string? describeServicesJson = null,
        string? taskJson = null,
        string? listTasksJson = null,
        string? taskDefinitionJson = null) =>
        Api.Responder = request => request.EcsAction switch
        {
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                describeServicesJson ?? DescribeServicesJson()),
            "ListTasks" => AwsApiDouble.Json(HttpStatusCode.OK, listTasksJson ?? ListTasksJson()),
            "DescribeTasks" => AwsApiDouble.Json(HttpStatusCode.OK, DescribeTasksJson(taskJson ?? TaskJson())),
            "DescribeTaskDefinition" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                TaskDefinitionEnvelopeJson(taskDefinitionJson)),
            _ => throw new InvalidOperationException(
                $"A read-only path issued the ECS action '{request.EcsAction}'."),
        };

    /// <summary>Routes a sweep: list the cluster's services, then describe them with their tags.</summary>
    internal void RouteSweep(string? listServicesJson = null, string? describeServicesJson = null) =>
        Api.Responder = request => request.EcsAction switch
        {
            "ListServices" => AwsApiDouble.Json(HttpStatusCode.OK, listServicesJson ?? ListServicesJson()),
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                describeServicesJson ?? DescribeServicesJson()),
            _ => throw new InvalidOperationException(
                $"A sweep issued the ECS action '{request.EcsAction}', which is not part of one."),
        };

    /// <summary>
    /// Routes a destroy: the delete answers DRAINING, and the following describes answer with
    /// <paramref name="settleTo"/>.
    /// </summary>
    internal void RouteDestroy(string settleTo = "INACTIVE")
    {
        var describes = 0;

        Api.Responder = request => request.EcsAction switch
        {
            "DeleteService" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                ServiceEnvelopeJson(ServiceJson(status: "DRAINING", runningCount: 1))),
            "DescribeServices" => AwsApiDouble.Json(
                HttpStatusCode.OK,
                DescribeServicesJson(ServiceJson(status: ++describes >= 1 ? settleTo : "DRAINING", runningCount: 0))),
            _ => throw new InvalidOperationException(
                $"A destroy issued the ECS action '{request.EcsAction}', which is not part of one."),
        };
    }

    /// <summary>Creates a service through the full operation path and hands back the resource it produced.</summary>
    internal async Task<ProvisionedResource> CreateAsync(ProvisioningRequest? request = null)
    {
        RouteSuccessfulCreate();

        var provisioner = Provisioner();
        var spec = provisioner.BuildSpec(request ?? PalworldRequest());

        return await provisioner.CreateOperation(spec).CreateAsync();
    }

    /// <summary>The handle Servyx would have recorded for the created service.</summary>
    internal static ResourceHandle RecordedHandle(
        string providerResourceId = ServiceArn,
        string region = Region,
        string provisionerId = AwsEcsFargateProvisioner.Id) =>
        new(
            provisionerId,
            providerResourceId,
            region,
            new Dictionary<string, string>(CanonicalTags, StringComparer.Ordinal));
}
