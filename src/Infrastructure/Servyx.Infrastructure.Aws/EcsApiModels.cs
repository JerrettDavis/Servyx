using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The Amazon ECS objects this adapter reads, projected out of the API's JSON into ordinary records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hand-projected for the same reason <c>Ec2Xml</c> and <c>LightsailJson</c> are.</strong> ECS speaks the
/// AWS JSON 1.1 protocol, exactly as Lightsail does, so the reading primitives are the same ones — and they are
/// literally the same ones: this file calls <see cref="LightsailJson"/> rather than restating it. Those helpers
/// are protocol-level, not service-level (a <c>tags</c> array of <c>{"key":…,"value":…}</c> objects is the AWS
/// JSON convention, not a Lightsail invention), and the name simply predates the second JSON-protocol adapter in
/// this assembly. Renaming it would edit the Lightsail suite without changing one byte of behaviour, so it was
/// declined; duplicating it would create two copies of a null-and-type-safety contract that must not drift.
/// </para>
/// <para>
/// <strong>ECS reports absence in the body, not as an error status — the one protocol difference that matters
/// here.</strong> Lightsail answers a read for a name it does not know with an HTTP 400 carrying
/// <c>NotFoundException</c>, which <see cref="LightsailJsonApiClient"/> turns into <see langword="null"/>. ECS's
/// <c>DescribeServices</c> and <c>DescribeTasks</c> answer <c>200 OK</c> with the unknown ARN moved into a
/// <c>failures</c> array carrying <c>reason: "MISSING"</c> — the resource is absent and the call
/// <em>succeeded</em>. An adapter that only read the <c>services</c> array would see an empty list and have no
/// way to distinguish "gone" from "the request named nothing", so <see cref="EcsFailure"/> exists and is read.
/// </para>
/// </remarks>
internal static class EcsProtocol
{
    /// <summary>The AWS JSON 1.1 target prefix every ECS action name is appended to for the <c>X-Amz-Target</c> header.</summary>
    /// <remarks>
    /// The date is ECS's original API version and has nothing to do with when a call is made; AWS never
    /// re-versioned the service. It is spelled out here rather than inline so the one place it could be
    /// mistyped is the one place a test can pin.
    /// </remarks>
    internal const string TargetPrefix = "AmazonEC2ContainerServiceV20141113.";

    /// <summary>The service name in the SigV4 credential scope.</summary>
    internal const string ServiceName = "ecs";

    /// <summary>The content type every ECS request and response carries.</summary>
    internal const string ContentType = "application/x-amz-json-1.1";
}

/// <summary>
/// One entry of an ECS <c>failures</c> array: a resource the request named that the service could not act on.
/// </summary>
/// <remarks>
/// See <see cref="EcsProtocol"/>'s remarks. This is how ECS says "no such service" on a successful call, and
/// treating it as anything other than a first-class part of the answer is how an adapter reports a destroyed
/// resource as merely unreadable.
/// </remarks>
/// <param name="Arn">The ARN the request named.</param>
/// <param name="Reason">Why ECS could not act on it — <see cref="MissingReason"/> being the one that means "gone".</param>
/// <param name="Detail">ECS's own detail text, when it supplies one.</param>
internal sealed record EcsFailure(string? Arn, string? Reason, string? Detail)
{
    /// <summary>The reason ECS reports for an ARN it does not know: the resource does not exist.</summary>
    internal const string MissingReason = "MISSING";

    /// <summary>Whether this failure means the resource is absent rather than unreadable.</summary>
    internal bool IsMissing => string.Equals(Reason, MissingReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>Projects a whole <c>failures</c> array, skipping anything that is not an object.</summary>
    internal static IReadOnlyList<EcsFailure> AllFrom(JsonArray? items)
    {
        var results = new List<EcsFailure>();

        if (items is null)
        {
            return results;
        }

        foreach (var node in items)
        {
            if (node is JsonObject item)
            {
                results.Add(new EcsFailure(
                    LightsailJson.Text(item, "arn"),
                    LightsailJson.Text(item, "reason"),
                    LightsailJson.Text(item, "detail")));
            }
        }

        return results;
    }
}

/// <summary>An ECS service, as <c>CreateService</c>/<c>DescribeServices</c>/<c>DeleteService</c> describe it.</summary>
/// <remarks>
/// <para>
/// <strong>The service is the resource with a stable identity, and that is why this adapter creates one.</strong>
/// A Fargate task's ARN changes every time the scheduler replaces it — which it does on host retirement, on a
/// platform-version rollout, and whenever the task exits — so a <c>ResourceHandle.ProviderResourceId</c> naming a
/// task would go stale without anything having gone wrong. The service ARN does not move.
/// </para>
/// <para>
/// <strong><see cref="RunningCount"/> is ECS's bookkeeping, not a confirmation.</strong>
/// <c>CreateService</c> answers <c>200 OK</c> with <see cref="Status"/> <c>ACTIVE</c> and
/// <see cref="RunningCount"/> zero: the service was accepted, and nothing is running. This adapter never treats
/// either field as evidence that the workload started; see <c>AwsEcsFargateProvisioner</c>'s create path, which
/// confirms by reading a task's own <c>lastStatus</c>.
/// </para>
/// </remarks>
/// <param name="ServiceArn">The service's ARN — its stable identity, and this adapter's <c>ProviderResourceId</c>.</param>
/// <param name="ServiceName">The service's name, chosen by the caller.</param>
/// <param name="ClusterArn">The ARN of the cluster the service lives in. Never created by this adapter.</param>
/// <param name="Status">The service's lifecycle status: <c>ACTIVE</c>, <c>DRAINING</c>, or <c>INACTIVE</c>.</param>
/// <param name="TaskDefinition">The task definition revision the service currently launches tasks from.</param>
/// <param name="DesiredCount">How many tasks ECS is trying to keep running. Always 1 for this adapter.</param>
/// <param name="RunningCount">How many tasks ECS believes are running. Bookkeeping; see the type remarks.</param>
/// <param name="LaunchType">The launch type, e.g. <c>FARGATE</c>.</param>
/// <param name="CreatedAt">When ECS reports the service was created.</param>
/// <param name="Tags">The service's tags, decoded from its <c>tags</c> array. Empty unless <c>include: ["TAGS"]</c> was asked for.</param>
internal sealed record EcsService(
    string ServiceArn,
    string? ServiceName,
    string? ClusterArn,
    string? Status,
    string? TaskDefinition,
    int DesiredCount,
    int RunningCount,
    string? LaunchType,
    DateTimeOffset? CreatedAt,
    IReadOnlyDictionary<string, string> Tags)
{
    /// <summary>The status a live service reports.</summary>
    internal const string ActiveStatus = "ACTIVE";

    /// <summary>The status a service reports while its tasks are being stopped after a delete.</summary>
    internal const string DrainingStatus = "DRAINING";

    /// <summary>The status a service reports once the delete has actually taken effect.</summary>
    /// <remarks>
    /// The only status that means a <c>DeleteService</c> finished. AWS keeps an <c>INACTIVE</c> service
    /// describable for a period afterwards, so "still visible" is not "still there" — but <c>DRAINING</c> very
    /// much is, and an adapter that returned success on the <c>DeleteService</c> response alone would be
    /// reporting a submission as a completion.
    /// </remarks>
    internal const string InactiveStatus = "INACTIVE";

    /// <summary>Whether ECS considers the service finished being deleted.</summary>
    internal bool IsInactive => string.Equals(Status, InactiveStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Projects one element of a <c>services</c> array, or the <c>service</c> object a mutation returns.</summary>
    internal static EcsService? From(JsonObject? item)
    {
        var arn = LightsailJson.Text(item, "serviceArn");
        if (arn is null)
        {
            return null;
        }

        return new EcsService(
            arn,
            LightsailJson.Text(item, "serviceName"),
            LightsailJson.Text(item, "clusterArn"),
            LightsailJson.Text(item, "status"),
            LightsailJson.Text(item, "taskDefinition"),
            Count(item, "desiredCount"),
            Count(item, "runningCount"),
            LightsailJson.Text(item, "launchType"),
            LightsailJson.UnixSeconds(item, "createdAt"),
            LightsailJson.Tags(item?["tags"] as JsonArray));
    }

    private static int Count(JsonObject? item, string property)
    {
        if (item is null || !item.TryGetPropertyValue(property, out var node) || node is null)
        {
            return 0;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}

/// <summary>One Fargate task, as <c>DescribeTasks</c> describes it.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="LastStatus"/> is the only honest confirmation that a workload started.</strong> The
/// interesting failures of a Fargate task all happen after ECS has happily accepted every API call: an image that
/// cannot be pulled, an EFS mount target missing in the task's availability zone, a security group with no
/// inbound NFS rule. Every one of those produces a task that reaches <c>PROVISIONING</c> or <c>PENDING</c> and
/// then <c>STOPPED</c>, with <see cref="StoppedReason"/> naming the cause — while <c>CreateService</c>'s
/// response, minutes earlier, said <c>200 OK</c>.
/// </para>
/// <para>
/// <strong>The address is a property of the task and dies with it.</strong> With <c>awsvpc</c> networking each
/// task gets its own elastic network interface, whose private IPv4 address arrives in the attachment details
/// below. A replacement task gets a different ENI and a different address, and the service replaces tasks as
/// ordinary operation. There is no public IPv4 field here at all: obtaining one means calling
/// <c>ec2:DescribeNetworkInterfaces</c> with the ENI id, which is a different AWS service and which this adapter
/// does not do.
/// </para>
/// </remarks>
/// <param name="TaskArn">The task's ARN. Ephemeral by design; never used as a resource handle.</param>
/// <param name="LastStatus">The task's observed lifecycle state, e.g. <c>PROVISIONING</c>, <c>PENDING</c>, <c>RUNNING</c>, <c>STOPPED</c>.</param>
/// <param name="DesiredStatus">The state ECS is driving the task towards.</param>
/// <param name="PrivateIpv4Address">The task ENI's private IPv4 address, when one has been attached.</param>
/// <param name="NetworkInterfaceId">The task ENI's id, recorded so a failure message can name it. Never resolved to a public address here.</param>
/// <param name="StoppedReason">Why ECS stopped the task, when it did. The single most useful field on this record.</param>
internal sealed record EcsTask(
    string TaskArn,
    string? LastStatus,
    string? DesiredStatus,
    string? PrivateIpv4Address,
    string? NetworkInterfaceId,
    string? StoppedReason)
{
    /// <summary>The one <see cref="LastStatus"/> value that means the workload is actually running.</summary>
    internal const string RunningStatus = "RUNNING";

    /// <summary>The <see cref="LastStatus"/> value that means the task will not be starting.</summary>
    internal const string StoppedStatus = "STOPPED";

    /// <summary>Whether the workload is running, as the task itself reports it.</summary>
    internal bool IsRunning => string.Equals(LastStatus, RunningStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the task has stopped, one way or the other.</summary>
    internal bool IsStopped => string.Equals(LastStatus, StoppedStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>ECS's own words about why a task stopped, or a plain statement that it supplied none.</summary>
    internal string StoppedText =>
        string.IsNullOrWhiteSpace(StoppedReason)
            ? "ECS reported no stoppedReason for the task."
            : StoppedReason;

    /// <summary>Projects one element of a <c>tasks</c> array.</summary>
    internal static EcsTask? From(JsonObject? item)
    {
        var arn = LightsailJson.Text(item, "taskArn");
        if (arn is null)
        {
            return null;
        }

        var (address, eni) = ReadNetworkInterface(item?["attachments"] as JsonArray);

        return new EcsTask(
            arn,
            LightsailJson.Text(item, "lastStatus"),
            LightsailJson.Text(item, "desiredStatus"),
            address,
            eni,
            LightsailJson.Text(item, "stoppedReason"));
    }

    /// <summary>
    /// Reads the task's ENI attachment, whose interesting values live in a name/value <c>details</c> array
    /// rather than as named members.
    /// </summary>
    /// <remarks>
    /// The shape is genuinely a list of key/value pairs on the wire — ECS models an attachment generically so a
    /// future attachment type can carry different details — so this is a lookup rather than a field read. Only
    /// the <c>ElasticNetworkInterface</c> attachment is considered; anything else is not the task's address.
    /// </remarks>
    private static (string? PrivateIpv4Address, string? NetworkInterfaceId) ReadNetworkInterface(JsonArray? attachments)
    {
        if (attachments is null)
        {
            return (null, null);
        }

        foreach (var node in attachments)
        {
            if (node is not JsonObject attachment
                || !string.Equals(
                    LightsailJson.Text(attachment, "type"),
                    "ElasticNetworkInterface",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? address = null;
            string? eni = null;

            if (attachment["details"] is JsonArray details)
            {
                foreach (var detailNode in details)
                {
                    if (detailNode is not JsonObject detail)
                    {
                        continue;
                    }

                    var name = LightsailJson.Text(detail, "name");
                    if (string.Equals(name, "privateIPv4Address", StringComparison.OrdinalIgnoreCase))
                    {
                        address = LightsailJson.Text(detail, "value");
                    }
                    else if (string.Equals(name, "networkInterfaceId", StringComparison.OrdinalIgnoreCase))
                    {
                        eni = LightsailJson.Text(detail, "value");
                    }
                }
            }

            if (address is not null || eni is not null)
            {
                return (address, eni);
            }
        }

        return (null, null);
    }

    /// <summary>Projects a whole <c>tasks</c> array, skipping anything that is not an object.</summary>
    internal static IReadOnlyList<EcsTask> AllFrom(JsonArray? items)
    {
        var results = new List<EcsTask>();

        if (items is null)
        {
            return results;
        }

        foreach (var node in items)
        {
            var task = From(node as JsonObject);
            if (task is not null)
            {
                results.Add(task);
            }
        }

        return results;
    }
}

/// <summary>One task definition revision, as <c>RegisterTaskDefinition</c>/<c>DescribeTaskDefinition</c> describe it.</summary>
/// <remarks>
/// <para>
/// <strong>A task definition revision is immortal, and that is a fact about ECS rather than a limitation of this
/// adapter.</strong> <c>DeregisterTaskDefinition</c> marks a revision <c>INACTIVE</c>; it does not remove it, and
/// an <c>INACTIVE</c> revision stays describable and stays referenced by any task still draining from it.
/// <c>DeleteTaskDefinitions</c> exists but acts only on already-<c>INACTIVE</c> revisions. What makes this
/// tolerable rather than an orphan problem is the other half of the fact: a task definition revision is
/// <strong>free</strong>, holds no data, and runs nothing. See <c>AwsEcsFargateProvisioner</c>'s remarks for what
/// the sweep therefore does and does not attempt.
/// </para>
/// <para>
/// <see cref="Cpu"/> and <see cref="Memory"/> are strings on the wire — ECS types them that way because a task
/// definition may express them either as raw units (<c>"1024"</c>) or, for EC2 launch type, in vCPU/GB notation.
/// This adapter only ever writes the raw-unit form Fargate requires, and reads back defensively.
/// </para>
/// </remarks>
/// <param name="TaskDefinitionArn">The revision's full ARN, e.g. <c>…:task-definition/family:7</c>.</param>
/// <param name="Family">The family name. Every provision of the same server adds a revision to it.</param>
/// <param name="Revision">The revision number within the family.</param>
/// <param name="Cpu">The task-level CPU reservation, in ECS CPU units, as a string.</param>
/// <param name="Memory">The task-level memory reservation, in MiB, as a string.</param>
/// <param name="Status">The revision's status: <c>ACTIVE</c> or <c>INACTIVE</c>.</param>
internal sealed record EcsTaskDefinition(
    string TaskDefinitionArn,
    string? Family,
    int Revision,
    string? Cpu,
    string? Memory,
    string? Status)
{
    /// <summary>Projects the <c>taskDefinition</c> object a register or describe call returns.</summary>
    internal static EcsTaskDefinition? From(JsonObject? item)
    {
        var arn = LightsailJson.Text(item, "taskDefinitionArn");
        if (arn is null)
        {
            return null;
        }

        var revision = 0;
        if (item is not null && item.TryGetPropertyValue("revision", out var node) && node is not null)
        {
            try
            {
                revision = node.GetValue<int>();
            }
            catch (InvalidOperationException)
            {
                revision = 0;
            }
            catch (FormatException)
            {
                revision = 0;
            }
        }

        return new EcsTaskDefinition(
            arn,
            LightsailJson.Text(item, "family"),
            revision,
            LightsailJson.Text(item, "cpu"),
            LightsailJson.Text(item, "memory"),
            LightsailJson.Text(item, "status"));
    }
}

/// <summary>
/// The ECS error type names this adapter distinguishes by.
/// </summary>
/// <remarks>
/// ECS splits "no such thing" across two mechanisms, unlike Lightsail's single <c>NotFoundException</c>. A
/// missing <em>cluster</em> is an exception (<see cref="ClusterNotFound"/>) because the request could not be
/// scoped at all; a missing <em>service</em> or <em>task</em> inside a cluster that does exist is reported in the
/// <c>failures</c> array of an otherwise successful response (see <see cref="EcsFailure"/>). Only
/// <c>DeleteService</c> and <c>UpdateService</c> raise <see cref="ServiceNotFound"/> as an exception, because
/// those name a single resource they must act on.
/// </remarks>
internal static class EcsErrorCodes
{
    /// <summary>The cluster named in the request does not exist. Always an exception, never a <c>failures</c> entry.</summary>
    internal const string ClusterNotFound = "ClusterNotFoundException";

    /// <summary>The service named in a mutating request does not exist.</summary>
    internal const string ServiceNotFound = "ServiceNotFoundException";

    /// <summary>The service exists but is not <c>ACTIVE</c>, so it cannot be acted on.</summary>
    internal const string ServiceNotActive = "ServiceNotActiveException";

    /// <summary>ECS refused a parameter — a duplicate service name, an invalid CPU/memory pair, an unknown subnet.</summary>
    internal const string InvalidParameter = "InvalidParameterException";
}
