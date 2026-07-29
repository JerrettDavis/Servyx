using System.Globalization;
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
    internal static JsonObject CreateService(
        AwsFargateServiceSpec spec,
        string taskDefinitionArn,
        IReadOnlyDictionary<string, string> tags)
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

        return new JsonObject
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
