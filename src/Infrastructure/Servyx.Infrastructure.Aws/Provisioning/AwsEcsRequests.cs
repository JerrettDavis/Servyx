using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// Translates an <see cref="AwsFargateServiceSpec"/> into the two JSON bodies a Fargate deployment needs:
/// <c>RegisterTaskDefinition</c> and <c>CreateService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a separate file.</strong> Kept apart from the provisioner for the same reason
/// <see cref="AwsLightsailRequests"/> and <c>AwsEc2Requests</c> are: it is the exact place a silent mistake — a
/// missing required field, tags built in a nondeterministic order — turns into a resource that exists but is
/// untagged or unreproducible. It is worth reviewing on its own, and here more than for the other two, because
/// there are two bodies and each creates a different taggable object.
/// </para>
/// <para>
/// <strong>Two <c>tags</c> arrays, not one, and neither is optional.</strong> Both
/// <c>RegisterTaskDefinition</c> and <c>CreateService</c> take tags inline, so neither object ever exists
/// untagged. The two arrays differ in exactly one key — <see cref="ServyxEcsTags.RoleTag"/> — because a swept
/// handle otherwise could not say which kind of object it names.
/// </para>
/// <para>
/// <strong>Everything is emitted in a deterministic order</strong>, tags sorted by key and environment variables
/// sorted by name, for the same reason <see cref="AwsLightsailRequests"/> sorts its tags: the plan hash is
/// computed over the same values, and a body whose member order varied run to run would sign two different
/// payloads for one logical deployment.
/// </para>
/// </remarks>
internal static class AwsEcsRequests
{
    /// <summary>Builds the full <c>RegisterTaskDefinition</c> request body for <paramref name="spec"/>.</summary>
    /// <param name="spec">The deployment to register a task definition revision for.</param>
    /// <param name="tags">The service's tag set; the role key is replaced with the task-definition role here.</param>
    /// <param name="region">The AWS region, needed by the <c>awslogs</c> driver's own options.</param>
    internal static JsonObject RegisterTaskDefinition(
        AwsFargateServiceSpec spec,
        IReadOnlyDictionary<string, string> tags,
        string region)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var container = new JsonObject
        {
            ["name"] = spec.ContainerName,
            ["image"] = spec.Image,
            // A single-container task whose one container is not essential would be a task that stays "running"
            // with nothing in it, which is the one outcome a health story must not have.
            ["essential"] = true,
            ["portMappings"] = PortMappings(spec),
            ["environment"] = EnvironmentArray(spec),
            ["mountPoints"] = new JsonArray(
                new JsonObject
                {
                    ["sourceVolume"] = EfsVolumeMount.VolumeName,
                    ["containerPath"] = spec.Mount.ContainerPath,
                    ["readOnly"] = spec.Mount.ReadOnly,
                }),
        };

        if (spec.LogGroup is { Length: > 0 } logGroup)
        {
            container["logConfiguration"] = new JsonObject
            {
                ["logDriver"] = "awslogs",
                ["options"] = new JsonObject
                {
                    ["awslogs-group"] = logGroup,
                    ["awslogs-region"] = region,
                    ["awslogs-stream-prefix"] = "servyx",
                },
            };
        }

        var efs = new JsonObject
        {
            ["fileSystemId"] = spec.Mount.FileSystemId,
            ["rootDirectory"] = spec.Mount.RootDirectory,
            ["transitEncryption"] = EfsVolumeMount.TransitEncryption,
        };

        if (spec.Mount.AccessPointId is { Length: > 0 } accessPoint)
        {
            efs["authorizationConfig"] = new JsonObject
            {
                ["accessPointId"] = accessPoint,
                ["iam"] = "ENABLED",
            };
        }

        var body = new JsonObject
        {
            ["family"] = spec.Family,
            ["networkMode"] = AwsFargateServiceSpec.NetworkMode,
            ["requiresCompatibilities"] = new JsonArray(AwsFargateServiceSpec.LaunchType),
            // Strings on the wire, not numbers: ECS types both fields as strings. See EcsTaskDefinition.
            ["cpu"] = spec.CpuUnits.ToString(CultureInfo.InvariantCulture),
            ["memory"] = spec.MemoryMib.ToString(CultureInfo.InvariantCulture),
            ["volumes"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = EfsVolumeMount.VolumeName,
                    ["efsVolumeConfiguration"] = efs,
                }),
            ["containerDefinitions"] = new JsonArray(container),
            ["tags"] = TagsArray(tags, ServyxEcsTags.RoleTaskDefinition),
        };

        if (spec.ExecutionRoleArn is { Length: > 0 } executionRole)
        {
            body["executionRoleArn"] = executionRole;
        }

        if (spec.TaskRoleArn is { Length: > 0 } taskRole)
        {
            body["taskRoleArn"] = taskRole;
        }

        return body;
    }

    /// <summary>Builds the full <c>CreateService</c> request body for <paramref name="spec"/>.</summary>
    /// <param name="spec">The deployment to create a service for.</param>
    /// <param name="taskDefinitionArn">The revision ARN <see cref="RegisterTaskDefinition"/> produced.</param>
    /// <param name="tags">The service's tag set, applied inline in this same request.</param>
    /// <param name="registryArn">
    /// The ARN of the AWS Cloud Map service to register tasks into, or <see langword="null"/> for no service
    /// discovery — which is the default and leaves the body byte-for-byte what it was before discovery existed.
    /// </param>
    internal static JsonObject CreateService(
        AwsFargateServiceSpec spec,
        string taskDefinitionArn,
        IReadOnlyDictionary<string, string> tags,
        string? registryArn = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskDefinitionArn);
        ArgumentNullException.ThrowIfNull(tags);

        var subnets = new JsonArray();
        foreach (var subnet in spec.SubnetIds)
        {
            subnets.Add(subnet);
        }

        var awsvpc = new JsonObject
        {
            ["subnets"] = subnets,
            ["assignPublicIp"] = spec.AssignPublicIp ? "ENABLED" : "DISABLED",
        };

        if (spec.SecurityGroupIds.Count > 0)
        {
            var groups = new JsonArray();
            foreach (var group in spec.SecurityGroupIds)
            {
                groups.Add(group);
            }

            awsvpc["securityGroups"] = groups;
        }

        var body = new JsonObject
        {
            ["cluster"] = spec.ClusterName,
            ["serviceName"] = spec.ServiceName,
            // The exact revision, not the family: a bare family name resolves to whatever is latest at the time
            // ECS reads it, which would make the service launch a revision this plan never described.
            ["taskDefinition"] = taskDefinitionArn,
            ["desiredCount"] = AwsFargateServiceSpec.DesiredCount,
            ["launchType"] = AwsFargateServiceSpec.LaunchType,
            ["platformVersion"] = AwsFargateServiceSpec.PlatformVersion,
            ["networkConfiguration"] = new JsonObject { ["awsvpcConfiguration"] = awsvpc },
            // ECS Exec is off. Turning it on would need the SSM agent in the image and would still not produce
            // an IExecutionTarget - see AwsEcsFargateProvisioner.UnreachableReason - so it is left off rather
            // than enabled to imply a capability that does not follow from it.
            ["enableExecuteCommand"] = false,
            // Every task the service launches inherits the service's tags, so a task seen in a console or a cost
            // report attributes back to the same Servyx instance id even though nothing here tags a task.
            ["propagateTags"] = "SERVICE",
            ["tags"] = TagsArray(tags, ServyxEcsTags.RoleService),
        };

        if (registryArn is { Length: > 0 })
        {
            // The whole of the durable-address mechanism, and it is three words long. Naming a Cloud Map service
            // here makes ECS register the task's elastic network interface into it when a task starts and
            // deregister it when the task stops - on every routine replacement, for the life of the service,
            // with no further call from Servyx. Because it is part of the call that creates the service, there
            // is no window in which a running task exists and is not registered.
            //
            // No 'port', 'containerName' or 'containerPort' is sent, and that is not an omission: ECS requires
            // those only for SRV records, and Servyx registers an A record. See AwsFargateServiceDiscovery.
            body["serviceRegistries"] = new JsonArray(new JsonObject { ["registryArn"] = registryArn });
        }

        return body;
    }

    private static JsonArray PortMappings(AwsFargateServiceSpec spec)
    {
        var mappings = new JsonArray();

        foreach (var port in spec.Ports)
        {
            mappings.Add(new JsonObject
            {
                ["containerPort"] = port.Port,
                // In awsvpc mode the host port must equal the container port; ECS rejects anything else. So a
                // port mapping here is a declaration, not a translation.
                ["hostPort"] = port.Port,
                ["protocol"] = port.Protocol.ToLowerInvariant(),
            });
        }

        return mappings;
    }

    private static JsonArray EnvironmentArray(AwsFargateServiceSpec spec)
    {
        var variables = new JsonArray();

        foreach (var variable in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            variables.Add(new JsonObject { ["name"] = variable.Key, ["value"] = variable.Value });
        }

        return variables;
    }

    /// <summary>
    /// Renders a tag dictionary as an ECS <c>tags</c> array, with <see cref="ServyxEcsTags.RoleTag"/> forced to
    /// <paramref name="role"/> so each of the two objects a create produces says which one it is.
    /// </summary>
    private static JsonArray TagsArray(IReadOnlyDictionary<string, string> tags, string role)
    {
        var array = new JsonArray();

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["key"] = tag.Key,
                ["value"] = string.Equals(tag.Key, ServyxEcsTags.RoleTag, StringComparison.Ordinal) ? role : tag.Value,
            });
        }

        return array;
    }
}

/// <summary>
/// Translates a Fargate spec plus an <see cref="AwsFargateServiceDiscovery"/> into the one AWS Cloud Map request
/// body a Servyx deployment needs: <c>CreateService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One body, and separate from <see cref="AwsEcsRequests"/> because it is a different service's
/// protocol.</strong> Cloud Map's JSON is <c>PascalCase</c> throughout and its tag members are <c>Key</c> and
/// <c>Value</c> rather than ECS's <c>key</c> and <c>value</c>. Putting the two builders in one class would put
/// two casing conventions one method apart, which is exactly the kind of near-miss that produces a resource that
/// exists and reads as untagged.
/// </para>
/// <para>
/// <strong>There is no namespace-creating body here</strong>, deliberately, and no <c>RegisterInstance</c> body
/// either — the first because Servyx must not manufacture a second unattributable monthly charge, the second
/// because ECS performs every registration itself once the ECS service names this Cloud Map service. See
/// <see cref="AwsFargateServiceDiscovery"/> and <see cref="ServiceDiscoveryJsonApiClient"/>.
/// </para>
/// </remarks>
internal static class AwsCloudMapRequests
{
    /// <summary>The prefix of the deterministic <c>CreatorRequestId</c> every create carries.</summary>
    internal const string CreatorRequestIdPrefix = "servyx-";

    /// <summary>How many hex characters of the identity digest the <c>CreatorRequestId</c> carries.</summary>
    /// <remarks>
    /// Cloud Map caps <c>CreatorRequestId</c> at 64 characters. Thirty-two hex characters of SHA-256 plus the
    /// prefix is well inside that and is far past any collision that matters for one AWS account's servers.
    /// </remarks>
    internal const int CreatorRequestIdDigestLength = 32;

    /// <summary>Builds the full Cloud Map <c>CreateService</c> request body.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The <c>CreatorRequestId</c> is deterministic on purpose.</strong> Cloud Map treats a repeated
    /// <c>CreatorRequestId</c> as a retry of the same request rather than as a second create, so a provision
    /// retried after a timeout re-reads the service it already made instead of failing on
    /// <c>ServiceAlreadyExists</c> or — worse — succeeding under a second name that nothing would ever clean up.
    /// It is derived from the Servyx instance id and the service name, which are exactly the two things that make
    /// this registration <em>this server's</em>.
    /// </para>
    /// <para>
    /// <strong><c>HealthCheckCustomConfig</c> and never <c>HealthCheckConfig</c>.</strong> The latter is a Route
    /// 53 health check: it works only for public namespaces, it bills per check, and it would probe a private
    /// address it cannot reach. The former hands health to ECS, which already knows the container's state, and
    /// AWS makes no charge for it. It is also the only one of the two that can serve a private namespace at all.
    /// </para>
    /// </remarks>
    /// <param name="spec">The deployment being registered. Supplies the Cloud Map service's name and identity.</param>
    /// <param name="discovery">The namespace, record TTL and reachability attestation.</param>
    /// <param name="tags">The service's tag set, applied inline in this same request.</param>
    internal static JsonObject CreateService(
        AwsFargateServiceSpec spec,
        AwsFargateServiceDiscovery discovery,
        IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(tags);

        return new JsonObject
        {
            ["Name"] = spec.ServiceName,
            ["NamespaceId"] = discovery.NamespaceId,
            ["CreatorRequestId"] = CreatorRequestId(spec),
            ["Description"] =
                "Created by Servyx so an ECS service on Fargate has a DNS name that outlives the tasks it "
                + "replaces. Destroyed by Servyx when that ECS service is destroyed. The namespace this lives in "
                + "was not created by Servyx and is never deleted by it.",
            ["DnsConfig"] = new JsonObject
            {
                ["RoutingPolicy"] = AwsFargateServiceDiscovery.RoutingPolicy,
                ["DnsRecords"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = AwsFargateServiceDiscovery.RecordType,
                        ["TTL"] = discovery.RecordTtlSeconds,
                    }),
            },
            ["HealthCheckCustomConfig"] = new JsonObject
            {
                ["FailureThreshold"] = AwsFargateServiceDiscovery.HealthCheckFailureThreshold,
            },
            ["Tags"] = TagsArray(tags),
        };
    }

    /// <summary>The deterministic idempotency token for <paramref name="spec"/>'s registration.</summary>
    internal static string CreatorRequestId(AwsFargateServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{spec.Tags.InstanceId}\n{spec.ClusterName}\n{spec.ServiceName}"))));

        return CreatorRequestIdPrefix + digest[..CreatorRequestIdDigestLength];
    }

    /// <summary>
    /// Renders a tag dictionary as a Cloud Map <c>Tags</c> array — <c>Key</c>/<c>Value</c>, not
    /// <c>key</c>/<c>value</c> — with the role forced to <see cref="ServyxEcsTags.RoleDiscoveryService"/>.
    /// </summary>
    private static JsonArray TagsArray(IReadOnlyDictionary<string, string> tags)
    {
        var array = new JsonArray();

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["Key"] = tag.Key,
                ["Value"] = string.Equals(tag.Key, ServyxEcsTags.RoleTag, StringComparison.Ordinal)
                    ? ServyxEcsTags.RoleDiscoveryService
                    : tag.Value,
            });
        }

        return array;
    }
}
