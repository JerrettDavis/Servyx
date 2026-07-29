using System.Diagnostics.CodeAnalysis;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The universal Servyx tags every Amazon ECS resource this project creates must carry, plus the pointer keys
/// that record the things it depends on and never creates.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The charset is EC2's and Lightsail's, character for character.</strong> AWS's ECS tagging
/// documentation states the same allowed set — letters, numbers, spaces, and <c>+ - = . _ : / @</c> — the same
/// 128/256 key/value length limits, and the same reserved <c>aws:</c> prefix. As with
/// <see cref="ServyxLightsailTags"/>, that is not a coincidence: these are all the same underlying AWS
/// resource-tagging convention. This type exists to give ECS its own validator and its own pointer keys, not to
/// encode a different rule.
/// </para>
/// <para>
/// <strong>Both taggable objects a create produces are tagged in the call that creates them.</strong>
/// <c>RegisterTaskDefinition</c> and <c>CreateService</c> each accept a <c>tags</c> array, so neither the
/// revision nor the service ever exists untagged. The service additionally carries
/// <c>propagateTags: "SERVICE"</c>, which makes ECS stamp the same tags onto every task it launches — so a task
/// found in a console or a cost report attributes back to the same Servyx instance id even though this adapter
/// never tags a task itself.
/// </para>
/// <para>
/// <strong>Four pointer keys, and what each one is honestly worth.</strong> The service is the only object a
/// sweep enumerates, so anything it depends on can only be named <em>through</em> the service's tags — and that
/// pointer dies when the service does. <see cref="ClusterTag"/> and <see cref="TaskDefinitionFamilyTag"/> point
/// at things that are free, so losing the pointer costs nothing but tidiness.
/// <see cref="FileSystemTag"/> and <see cref="AccessPointTag"/> point at an EFS file system that is separately
/// billed and holds the save data, and losing that pointer is the same permanent, unfixable-from-inside-the-adapter
/// gap <c>docs/provisioning.md</c> §11.4 records for Azure Container Instances' storage account. It is written
/// down because a sweep that finds the service can at least name the file system while the service exists; it is
/// not a claim that the file system is sweepable.
/// </para>
/// <para>
/// <strong>Why the family name and not the revision ARN.</strong> <see cref="TaskDefinitionFamilyTag"/> records
/// the task definition <em>family</em>, which is known before any API call, rather than the revision ARN, which
/// exists only after <c>RegisterTaskDefinition</c> returns. The tag set is materialised at operation
/// construction so it can be committed to the write-ahead ledger <em>before</em> the create runs, so a value that
/// does not exist yet is not a value this key could carry. The family is also the right granularity for the
/// question the tag is for: <c>ListTaskDefinitions</c> filters by family prefix, not by revision.
/// </para>
/// </remarks>
public sealed class ServyxEcsTags
{
    /// <summary>The maximum length ECS accepts for a tag key.</summary>
    public const int MaxTagKeyLength = 128;

    /// <summary>The maximum length ECS accepts for a tag value.</summary>
    public const int MaxTagValueLength = 256;

    /// <summary>The tag-key prefix AWS reserves for itself. A write using it is refused by ECS.</summary>
    public const string ReservedKeyPrefix = "aws:";

    /// <summary>The characters ECS allows in a tag beyond letters, digits and whitespace.</summary>
    public const string AdditionalAllowedCharacters = "+-=._:/@";

    /// <summary>Marks a resource as created and owned by Servyx.</summary>
    public const string ManagedTag = ServyxTagKeys.Managed;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxTagKeys.ManagedValue;

    /// <summary>
    /// Distinguishes the two kinds of object a create tags, since both carry the same identity keys.
    /// </summary>
    /// <remarks>
    /// Needed here for the reason <c>ServyxEc2Tags</c> needs one and <see cref="ServyxLightsailTags"/> does not:
    /// a create produces two taggable objects of different kinds — a task definition revision and a service — and
    /// a swept <see cref="ResourceHandle"/> has nowhere else to record which it is looking at.
    /// </remarks>
    public const string RoleTag = ServyxTagKeys.Prefix + "role";

    /// <summary>The <see cref="RoleTag"/> value stamped on the ECS service.</summary>
    public const string RoleService = "ecs-service";

    /// <summary>The <see cref="RoleTag"/> value stamped on the task definition revision.</summary>
    public const string RoleTaskDefinition = "ecs-task-definition";

    /// <summary>Records the ECS cluster the service lives in. A pointer; the cluster is never created by Servyx.</summary>
    public const string ClusterTag = ServyxTagKeys.Prefix + "aws-ecs-cluster";

    /// <summary>Records the task definition family the service launches from. See the type remarks for why the family and not the revision.</summary>
    public const string TaskDefinitionFamilyTag = ServyxTagKeys.Prefix + "aws-ecs-task-definition-family";

    /// <summary>Records the EFS file system the task mounts. A pointer; the file system is never created or destroyed by Servyx.</summary>
    public const string FileSystemTag = ServyxTagKeys.Prefix + "aws-efs-file-system";

    /// <summary>Records the EFS access point the task mounts through, when one is used.</summary>
    public const string AccessPointTag = ServyxTagKeys.Prefix + "aws-efs-access-point";

    /// <summary>
    /// Records the AWS Cloud Map namespace the service is registered into, when service discovery is configured.
    /// A pointer; the namespace is never created or destroyed by Servyx.
    /// </summary>
    /// <remarks>
    /// The same kind of key as <see cref="FileSystemTag"/> and worth much less, which is the point of saying so.
    /// The namespace is billable — it is a Route 53 hosted zone — but it is shared by every service in it and has
    /// a lifetime no single server governs, so this pointer is for an operator reading a swept service, not for
    /// anything that could clean up.
    /// </remarks>
    public const string DiscoveryNamespaceTag = ServyxTagKeys.Prefix + "aws-cloud-map-namespace";

    /// <summary>
    /// Records the <em>name</em> of the AWS Cloud Map service Servyx created alongside this ECS service.
    /// </summary>
    /// <remarks>
    /// The name and not the ARN, for exactly the reason <see cref="TaskDefinitionFamilyTag"/> records the family
    /// and not the revision ARN: the tag set is materialised at operation construction so it can reach the
    /// write-ahead ledger before any create runs, and the Cloud Map service's ARN does not exist until Cloud
    /// Map's own <c>CreateService</c> has returned. The ARN is not lost by this — it is read back from the ECS
    /// service's <c>serviceRegistries</c>, which is where the authoritative link lives.
    /// </remarks>
    public const string DiscoveryServiceTag = ServyxTagKeys.Prefix + "aws-cloud-map-service";

    /// <summary>The <see cref="RoleTag"/> value stamped on the Cloud Map service.</summary>
    /// <remarks>
    /// A third kind of object a create can produce, and the second one Servyx must destroy. Without a role of its
    /// own, a Cloud Map service carrying the same identity keys as the ECS service would be indistinguishable
    /// from it in a tag read taken on the destroy path.
    /// </remarks>
    public const string RoleDiscoveryService = "cloud-map-service";

    /// <summary>Identifies the Servyx server/instance the resource backs.</summary>
    public const string InstanceIdTag = ServyxTagKeys.InstanceId;

    /// <summary>Identifies the provisioning job that asked for the resource.</summary>
    public const string JobIdTag = ServyxTagKeys.JobId;

    /// <summary>Identifies the connector the resource is reachable through.</summary>
    public const string ConnectorIdTag = ServyxTagKeys.ConnectorId;

    private ServyxEcsTags(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the resource backs.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for it.</summary>
    public string JobId { get; }

    /// <summary>The connector it is attributed to.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The only way to obtain a <see cref="ServyxEcsTags"/>. Every parameter is required and is checked against
    /// ECS's tag-value rules.
    /// </summary>
    /// <exception cref="ArgumentException">Any argument is blank or is not expressible as an ECS tag value.</exception>
    public static ServyxEcsTags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        RequireTaggableValue(instanceId, nameof(instanceId));
        RequireTaggableValue(jobId, nameof(jobId));
        RequireTaggableValue(connectorId, nameof(connectorId));

        return new ServyxEcsTags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the canonical Servyx tag dictionary. Any <paramref name="additional"/> tags are applied first and
    /// the mandatory ones last, so an extra can never override one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs tags from a live resource's tag dictionary, or returns <see langword="null"/> if the resource
    /// is not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxEcsTags? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
            ? new ServyxEcsTags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a resource's tags mark it as Servyx-managed.</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>
    /// Checks a whole tag dictionary against ECS's rules, before any resource has been created from it.
    /// </summary>
    /// <remarks>
    /// Checked up front for the same reason <c>ServyxEc2Tags.Validate</c> and
    /// <see cref="ServyxLightsailTags.Validate"/> are: <c>AwsEcsFargateProvisioner.PlanAsync</c> issues no HTTP
    /// request, so a plan built from an untaggable identity would otherwise look fine on screen and fail only
    /// when <c>RegisterTaskDefinition</c> is actually called.
    /// </remarks>
    /// <exception cref="ArgumentException">Any key or value would be rejected by ECS.</exception>
    public static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var pair in tags)
        {
            if (pair.Key.StartsWith(ReservedKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Tag key '{pair.Key}' uses the '{ReservedKeyPrefix}' prefix, which AWS reserves for itself "
                    + "and refuses on any write. Servyx keys all begin with 'servyx.'.",
                    nameof(tags));
            }

            if (!IsTaggableKey(pair.Key))
            {
                throw new ArgumentException(
                    $"Tag key '{pair.Key}' is not a legal ECS tag key. A key must be 1-{MaxTagKeyLength} "
                    + $"characters of letters, digits, whitespace, or {AdditionalAllowedCharacters}.",
                    nameof(tags));
            }

            if (!IsTaggableValue(pair.Value))
            {
                throw new ArgumentException(
                    $"Tag value for '{pair.Key}' is not a legal ECS tag value. A value must be "
                    + $"1-{MaxTagValueLength} characters of letters, digits, whitespace, or "
                    + $"{AdditionalAllowedCharacters}.",
                    nameof(tags));
            }
        }

        return tags;
    }

    /// <summary>Whether <paramref name="key"/> is a legal ECS tag key.</summary>
    public static bool IsTaggableKey([NotNullWhen(true)] string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxTagKeyLength && IsTaggableText(key);

    /// <summary>Whether <paramref name="value"/> is a legal ECS tag value.</summary>
    public static bool IsTaggableValue([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxTagValueLength && IsTaggableText(value);

    private static bool IsTaggableText(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c)
                && !char.IsWhiteSpace(c)
                && !AdditionalAllowedCharacters.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireTaggableValue(string value, string paramName)
    {
        if (!IsTaggableValue(value))
        {
            throw new ArgumentException(
                $"'{value}' cannot be carried as an ECS tag value, so a service created for it could not be "
                + "attributed back to Servyx by an orphan sweep - and an ECS service bills for a Fargate task "
                + "every second it keeps one running. ECS tag values may contain letters, digits, whitespace "
                + $"and {AdditionalAllowedCharacters}, to {MaxTagValueLength} characters.",
                paramName);
        }
    }
}
