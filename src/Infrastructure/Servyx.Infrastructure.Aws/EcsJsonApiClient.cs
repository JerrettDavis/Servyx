using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The only code in this assembly that talks to <c>ecs.&lt;region&gt;.amazonaws.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>AWS JSON 1.1 again, and the signer needed nothing.</strong> Every call here is a <c>POST /</c> routed
/// by an <c>X-Amz-Target: AmazonEC2ContainerServiceV20141113.&lt;Action&gt;</c> header with a JSON object body —
/// structurally identical to <see cref="LightsailJsonApiClient"/>, and the third service in this assembly to ride
/// <see cref="AwsRequestSigner"/> completely unmodified. The signer signs every <c>x-amz-*</c> header already
/// present on the outgoing message, so <c>X-Amz-Target</c> is covered by the same allow-list that covered
/// Lightsail's, with <c>service = "ecs"</c> replacing <c>"lightsail"</c> in the credential scope. Not one line of
/// <c>AwsSigV4.cs</c> or <c>AwsRequestSigner.cs</c> was touched to add this adapter, which is the strongest
/// available evidence for that file's claim that the algorithm was never EC2-specific.
/// </para>
/// <para>
/// <strong>Three objects, not one — the structural difference from every other adapter in this assembly.</strong>
/// An EC2 instance, a Lightsail instance and an ACI container group are each one provider object that one call
/// creates. A running Fargate workload is a <em>task definition revision</em> (registered), inside a
/// <em>cluster</em> (pre-existing), driven by a <em>service</em> (created), which launches a <em>task</em>
/// (scheduled). That is why this client has eight methods where the Lightsail client has four, why a create is
/// two writes rather than one, and why a refresh costs up to four reads. None of that is incidental complexity
/// that could be optimised away: it is the shape of the provider.
/// </para>
/// <para>
/// <strong>Batch limits are honoured rather than assumed away.</strong> <c>DescribeServices</c> accepts at most
/// ten services per call and <c>DescribeTasks</c> at most a hundred tasks, so both are chunked here. A sweep that
/// sent one oversized request would fail wholesale on exactly the accounts that most need sweeping — the ones
/// with many resources.
/// </para>
/// <para>
/// Nothing here logs, for the same reason nothing in <see cref="Ec2QueryApiClient"/> or
/// <see cref="LightsailJsonApiClient"/> does: this assembly references no logging package, so there is no
/// reachable path that could write a credential, a derived signing key, or a signature.
/// </para>
/// </remarks>
internal sealed class EcsJsonApiClient
{
    /// <summary>The most services <c>DescribeServices</c> accepts in one call.</summary>
    internal const int DescribeServicesBatchSize = 10;

    /// <summary>The most tasks <c>DescribeTasks</c> accepts in one call.</summary>
    internal const int DescribeTasksBatchSize = 100;

    /// <summary>
    /// A hard ceiling on pages followed during one sweep, so a service paging bug cannot turn a sweep into an
    /// unbounded loop. Matches the EC2, Lightsail, DigitalOcean and Azure clients.
    /// </summary>
    private const int MaxSweepPages = 200;

    private readonly HttpClient _http;
    private readonly AwsRequestSigner _signer;
    private readonly Uri _endpoint;
    private readonly string _region;

    internal EcsJsonApiClient(HttpClient http, AwsRequestSigner signer, string region, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        _http = http;
        _signer = signer;
        _region = region;
        _endpoint = endpoint ?? new Uri(DefaultEndpointFor(region), UriKind.Absolute);
    }

    /// <summary>The regional ECS endpoint for <paramref name="region"/>.</summary>
    /// <remarks>
    /// Regional exactly as the EC2 and Lightsail endpoints are, and for the same reason: the region names both
    /// the host and the SigV4 credential scope, so it is adapter state fixed at construction rather than a
    /// per-request parameter.
    /// </remarks>
    internal static string DefaultEndpointFor(string region) =>
        string.Create(CultureInfo.InvariantCulture, $"https://ecs.{region}.amazonaws.com/");

    /// <summary>The region every call from this client is scoped to.</summary>
    internal string Region => _region;

    /// <summary>
    /// Registers a task definition revision, applying every Servyx tag in the same call.
    /// </summary>
    /// <remarks>
    /// The first of the two writes a create performs, and the cheaper one in every sense: a task definition
    /// revision is free, launches nothing, and reserves nothing. It is also the call that validates most of the
    /// deployment — an invalid CPU/memory pair, a malformed EFS volume configuration or a missing execution role
    /// is refused here, before any service exists and therefore before anything can bill.
    /// </remarks>
    /// <returns>The registered revision, whose ARN the subsequent <c>CreateService</c> names.</returns>
    /// <exception cref="AwsApiException">ECS refused the registration, or answered without a task definition.</exception>
    internal async Task<EcsTaskDefinition> RegisterTaskDefinitionAsync(JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = await SendAsync("RegisterTaskDefinition", body, "register a task definition", ct)
            .ConfigureAwait(false);

        return EcsTaskDefinition.From(response?["taskDefinition"] as JsonObject)
            ?? throw new AwsApiException(
                HttpStatusCode.OK,
                null,
                "ECS accepted the RegisterTaskDefinition call but its response carried no taskDefinition, so "
                + "Servyx has no revision ARN to create a service from. Nothing billable was created by this "
                + "call; a task definition revision reserves no capacity.");
    }

    /// <summary>
    /// Creates one service, applying every Servyx tag in the same call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The billable write. Tags travel inline in this same request, so there is no window in which a service
    /// exists that a sweep could not find — the same guarantee <c>CreateInstances</c> gives on Lightsail, and for
    /// the same reason: ECS's <c>CreateService</c> takes a <c>tags</c> array.
    /// </para>
    /// <para>
    /// <strong>The response is an acceptance, not a running workload.</strong> ECS answers with a service whose
    /// <c>runningCount</c> is zero and whose <c>status</c> is already <c>ACTIVE</c>. Callers must confirm by
    /// reading a task; see <see cref="ListTaskArnsAsync"/> and <see cref="DescribeTasksAsync"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="AwsApiException">ECS refused the create, or answered without a service object.</exception>
    internal async Task<EcsService> CreateServiceAsync(JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = await SendAsync("CreateService", body, "create a service", ct).ConfigureAwait(false);

        return EcsService.From(response?["service"] as JsonObject)
            ?? throw new AwsApiException(
                HttpStatusCode.OK,
                null,
                "ECS accepted the CreateService call but its response carried no service object, so Servyx has "
                + "no ARN for a resource that may now exist and may be billing. Reconcile the cluster by tag "
                + "before assuming nothing was created.");
    }

    /// <summary>
    /// Reads one service by name or ARN, or <see langword="null"/> if the cluster does not have it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always asks for <c>include: ["TAGS"]</c>. Without it ECS returns the service with an empty <c>tags</c>
    /// array rather than omitting the member, which would make every Servyx-managed service look unmanaged — a
    /// silent failure that reads as "someone else's resource" and therefore as "do not sweep, do not destroy".
    /// </para>
    /// <para>
    /// An absent service comes back as a <c>failures</c> entry on a <em>successful</em> response; see
    /// <see cref="EcsFailure"/>. A missing <em>cluster</em> is different and is deliberately allowed to surface
    /// as an <see cref="AwsApiException"/>: a cluster this adapter was configured with and that does not exist is
    /// a misconfiguration, and answering "the service is gone" to it would let a sweep conclude that a whole
    /// cluster's worth of billing services had been cleaned up.
    /// </para>
    /// </remarks>
    internal async Task<EcsService?> DescribeServiceAsync(string cluster, string service, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var services = await DescribeServicesAsync(cluster, [service], ct).ConfigureAwait(false);
        return services.Count == 0 ? null : services[0];
    }

    /// <summary>
    /// Reads services by name or ARN, in batches of <see cref="DescribeServicesBatchSize"/>, with their tags.
    /// </summary>
    /// <remarks>
    /// Services ECS reports as <see cref="EcsFailure.MissingReason"/> are simply absent from the result — a
    /// caller asking about ten services and getting seven back has three that no longer exist, which is the
    /// answer it wanted.
    /// </remarks>
    internal async Task<IReadOnlyList<EcsService>> DescribeServicesAsync(
        string cluster,
        IReadOnlyList<string> services,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentNullException.ThrowIfNull(services);

        var results = new List<EcsService>();

        for (var offset = 0; offset < services.Count; offset += DescribeServicesBatchSize)
        {
            var batch = new JsonArray();
            for (var i = offset; i < Math.Min(offset + DescribeServicesBatchSize, services.Count); i++)
            {
                batch.Add(services[i]);
            }

            var body = new JsonObject
            {
                ["cluster"] = cluster,
                ["services"] = batch,
                // Not optional: without it every service reads as untagged, i.e. as not Servyx's.
                ["include"] = new JsonArray("TAGS"),
            };

            var response = await SendAsync("DescribeServices", body, "read services", ct).ConfigureAwait(false);

            if (response?["services"] is JsonArray found)
            {
                foreach (var node in found)
                {
                    var projected = EcsService.From(node as JsonObject);
                    if (projected is not null)
                    {
                        results.Add(projected);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Lists every Fargate service ARN in <paramref name="cluster"/>, following <c>nextToken</c> to the end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>ECS has no tag filter on any list call, and no cross-cluster list at all — the two real
    /// limitations of an ECS sweep, stated here rather than discovered later.</strong> <c>ListServices</c>
    /// accepts a cluster, a launch type and a paging token, and nothing else; there is no
    /// <c>Filter.1.Name=tag:…</c> equivalent as EC2's <c>DescribeInstances</c> has. So every service in the
    /// cluster crosses the wire, and the tag filtering is this process's own work — the same shape as Lightsail's
    /// <c>GetInstances</c>, one degree worse because the tags then need a second call to read.
    /// </para>
    /// <para>
    /// The absence of a cross-cluster list is the sharper one and cannot be worked around from inside this
    /// client: the Resource Groups Tagging API (<c>tagging.&lt;region&gt;.amazonaws.com</c>,
    /// <c>GetResources</c>) <em>can</em> find ECS services by tag across every cluster in a region, and this
    /// adapter deliberately does not call it — that would be a fifth AWS service, a fifth endpoint and a fifth
    /// IAM permission. The consequence is stated on <c>AwsEcsFargateProvisioner</c>: a sweep sees one cluster.
    /// </para>
    /// <para>
    /// <c>launchType: FARGATE</c> is sent because it is the one narrowing ECS does offer server-side, and
    /// because an EC2-launch-type service in the same cluster is not something this adapter could have created.
    /// </para>
    /// </remarks>
    internal async Task<IReadOnlyList<string>> ListFargateServiceArnsAsync(string cluster, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);

        var arns = new List<string>();
        string? nextToken = null;

        for (var page = 0; page < MaxSweepPages; page++)
        {
            var body = new JsonObject
            {
                ["cluster"] = cluster,
                ["launchType"] = "FARGATE",
                ["maxResults"] = 100,
            };

            if (nextToken is not null)
            {
                body["nextToken"] = nextToken;
            }

            var response = await SendAsync("ListServices", body, "list services", ct).ConfigureAwait(false);

            if (response?["serviceArns"] is JsonArray found)
            {
                foreach (var node in found)
                {
                    if (node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } arn)
                    {
                        arns.Add(arn);
                    }
                }
            }

            nextToken = LightsailJson.Text(response, "nextToken");
            if (nextToken is null)
            {
                break;
            }
        }

        return arns;
    }

    /// <summary>Lists the ARNs of a service's tasks in a given desired state, following <c>nextToken</c> to the end.</summary>
    internal async Task<IReadOnlyList<string>> ListTaskArnsAsync(
        string cluster,
        string serviceName,
        string desiredStatus,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredStatus);

        var arns = new List<string>();
        string? nextToken = null;

        for (var page = 0; page < MaxSweepPages; page++)
        {
            var body = new JsonObject
            {
                ["cluster"] = cluster,
                ["serviceName"] = serviceName,
                ["desiredStatus"] = desiredStatus,
            };

            if (nextToken is not null)
            {
                body["nextToken"] = nextToken;
            }

            var response = await SendAsync("ListTasks", body, "list a service's tasks", ct).ConfigureAwait(false);

            if (response?["taskArns"] is JsonArray found)
            {
                foreach (var node in found)
                {
                    if (node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } arn)
                    {
                        arns.Add(arn);
                    }
                }
            }

            nextToken = LightsailJson.Text(response, "nextToken");
            if (nextToken is null)
            {
                break;
            }
        }

        return arns;
    }

    /// <summary>Reads tasks by ARN, in batches of <see cref="DescribeTasksBatchSize"/>.</summary>
    internal async Task<IReadOnlyList<EcsTask>> DescribeTasksAsync(
        string cluster,
        IReadOnlyList<string> taskArns,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentNullException.ThrowIfNull(taskArns);

        var results = new List<EcsTask>();

        for (var offset = 0; offset < taskArns.Count; offset += DescribeTasksBatchSize)
        {
            var batch = new JsonArray();
            for (var i = offset; i < Math.Min(offset + DescribeTasksBatchSize, taskArns.Count); i++)
            {
                batch.Add(taskArns[i]);
            }

            var body = new JsonObject { ["cluster"] = cluster, ["tasks"] = batch };
            var response = await SendAsync("DescribeTasks", body, "read a service's tasks", ct).ConfigureAwait(false);

            results.AddRange(EcsTask.AllFrom(response?["tasks"] as JsonArray));
        }

        return results;
    }

    /// <summary>Reads one task definition revision by ARN or <c>family:revision</c>, or <see langword="null"/> if it is gone.</summary>
    internal async Task<EcsTaskDefinition?> DescribeTaskDefinitionAsync(string taskDefinition, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDefinition);

        var body = new JsonObject { ["taskDefinition"] = taskDefinition };

        try
        {
            var response = await SendAsync("DescribeTaskDefinition", body, "read a task definition", ct)
                .ConfigureAwait(false);

            return EcsTaskDefinition.From(response?["taskDefinition"] as JsonObject);
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, EcsErrorCodes.InvalidParameter, StringComparison.Ordinal))
        {
            // ECS answers InvalidParameterException, not a not-found code, for a revision it does not have. The
            // caller wants a cost figure from it; a revision that is gone is a missing figure, not a failure.
            return null;
        }
    }

    /// <summary>
    /// Deletes a service, stopping the tasks it is keeping alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>force: true</c> is required here and is not a bypass of anything.</strong> ECS refuses to
    /// delete a service whose desired count is above zero unless the flag is set; every service this adapter
    /// creates has a desired count of exactly one, so without the flag <c>DeleteService</c> would fail for every
    /// resource this adapter can produce and <see cref="Servyx.Domain.Provisioning.ProvisioningCapabilities.Destroy"/>
    /// would be an advertised capability rather than a real one. The alternative — <c>UpdateService</c> to zero,
    /// then delete — is two calls with a window between them and no safety gained: a scaled-to-zero service still
    /// exists, and what the flag actually authorises is stopping <em>this service's own</em> tasks, which is
    /// precisely what destroying it means. It does not widen the blast radius by one resource.
    /// </para>
    /// <para>
    /// <strong>The response is a submission.</strong> ECS answers <c>200 OK</c> with the service in
    /// <c>DRAINING</c>. The caller must poll until <c>INACTIVE</c>; see the provisioner's destroy path.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The service as ECS describes it immediately after the delete, or <see langword="null"/> if ECS never knew
    /// it (<see cref="EcsErrorCodes.ServiceNotFound"/>).
    /// </returns>
    internal async Task<EcsService?> DeleteServiceAsync(string cluster, string service, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var body = new JsonObject
        {
            ["cluster"] = cluster,
            ["service"] = service,
            ["force"] = true,
        };

        try
        {
            var response = await SendAsync("DeleteService", body, "delete a service", ct).ConfigureAwait(false);
            return EcsService.From(response?["service"] as JsonObject);
        }
        catch (AwsApiException e) when (
            string.Equals(e.ErrorCode, EcsErrorCodes.ServiceNotFound, StringComparison.Ordinal))
        {
            return null;
        }
    }

    private async Task<JsonObject?> SendAsync(string action, JsonObject body, string attempted, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, EcsProtocol.ContentType),
        };

        // The whole of ECS's operation routing, exactly as Lightsail's: one header, signed automatically because
        // AwsRequestSigner already covers every x-amz-* header on the message.
        request.Headers.TryAddWithoutValidation("X-Amz-Target", EcsProtocol.TargetPrefix + action);

        await _signer.SignAsync(request, ct).ConfigureAwait(false);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw BuildFailure(response.StatusCode, payload, attempted);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException e)
        {
            throw new AwsApiException(
                response.StatusCode,
                null,
                $"ECS's response to the attempt to {attempted} was not well-formed JSON.",
                e);
        }
    }

    /// <summary>
    /// Turns a non-success response into an <see cref="AwsApiException"/> carrying the status and ECS's own error
    /// type and message — and nothing from the request.
    /// </summary>
    /// <remarks>
    /// Identical in shape to <see cref="LightsailJsonApiClient"/>'s, because both services speak AWS JSON 1.1 and
    /// both put the error type in <c>__type</c>, sometimes namespaced as
    /// <c>com.amazon.coral.service#SomeException</c>. The namespace prefix is stripped so callers can match on
    /// the short names in <see cref="EcsErrorCodes"/>.
    /// </remarks>
    private static AwsApiException BuildFailure(HttpStatusCode status, string payload, string attempted)
    {
        string? code = null;
        string? message = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var error = JsonNode.Parse(payload) as JsonObject;
                var type = LightsailJson.Text(error, "__type");
                code = type is null ? null : type[(type.IndexOf('#') + 1)..];
                message = LightsailJson.Text(error, "message") ?? LightsailJson.Text(error, "Message");
            }
            catch (JsonException)
            {
                // A non-JSON error body (a load balancer's HTML, say) is reported by status alone rather than
                // being allowed to mask the failure it describes.
            }
        }

        return new AwsApiException(
            status,
            code,
            string.Create(
                CultureInfo.InvariantCulture,
                $"ECS refused the attempt to {attempted}: HTTP {(int)status}. {code} {message}").Trim());
    }
}
