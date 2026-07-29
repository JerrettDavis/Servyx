using System.Diagnostics.CodeAnalysis;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The universal Servyx tags every Lightsail resource this project creates must carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The charset question the report asked to have answered: Lightsail's tag rules are, character for
/// character, EC2's.</strong> AWS's own Lightsail tagging documentation states the allowed charset as "letters,
/// numbers, and spaces, and the following characters: <c>+ - = . _ : / @</c>", a maximum key length of 128 and
/// value length of 256, and the same reserved <c>aws:</c> prefix — every one of those figures matches
/// <c>ServyxEc2Tags</c> exactly. That is not a coincidence: both services sit on the same underlying AWS
/// resource-tagging convention. So unlike the EC2-vs-DigitalOcean comparison, which is a real divergence (EC2
/// allows <c>.</c>, DigitalOcean does not), there is no tag-charset finding to report here beyond "identical to
/// EC2" — this type exists to give Lightsail its own validator rather than to encode a different rule.
/// </para>
/// <para>
/// <strong>What is genuinely simpler than EC2's version: there is no <c>servyx.role</c> key.</strong>
/// <c>ServyxEc2Tags</c> needs one because <c>RunInstances</c> creates two kinds of taggable object (the instance
/// and its EBS volumes) and a swept <see cref="ResourceHandle"/> has nowhere else to record which is which.
/// Lightsail's bundle price already includes the instance's SSD storage — there is no separate, separately
/// billed, separately orphanable volume resource an instance launch creates — so a Lightsail sweep only ever
/// finds one kind of object and the role distinction has nothing to disambiguate.
/// </para>
/// <para>
/// <strong>What is also simpler: there is no synthetic <c>Name</c> tag.</strong> <c>ServyxEc2Tags.NameTag</c>
/// exists because an EC2 instance id (<c>i-0123...</c>) is not human-readable on its own, so the EC2 adapter
/// stamps a <c>Name</c> tag purely so the console shows something legible. A Lightsail instance's identity
/// <em>is</em> the caller-chosen name — it is already what the console displays — so there is nothing this type
/// needs to add for the same purpose.
/// </para>
/// </remarks>
public sealed class ServyxLightsailTags
{
    /// <summary>The maximum length Lightsail accepts for a tag key.</summary>
    public const int MaxTagKeyLength = 128;

    /// <summary>The maximum length Lightsail accepts for a tag value.</summary>
    public const int MaxTagValueLength = 256;

    /// <summary>The tag-key prefix AWS reserves for itself. A write using it is refused by Lightsail.</summary>
    public const string ReservedKeyPrefix = "aws:";

    /// <summary>The characters Lightsail allows in a tag beyond letters, digits and whitespace.</summary>
    public const string AdditionalAllowedCharacters = "+-=._:/@";

    /// <summary>Marks a resource as created and owned by Servyx.</summary>
    public const string ManagedTag = ServyxTagKeys.Managed;

    /// <summary>The only value <see cref="ManagedTag"/> is ever set to.</summary>
    public const string ManagedTagValue = ServyxTagKeys.ManagedValue;

    /// <summary>Identifies the Servyx server/instance the resource backs.</summary>
    public const string InstanceIdTag = ServyxTagKeys.InstanceId;

    /// <summary>Identifies the provisioning job that asked for the resource.</summary>
    public const string JobIdTag = ServyxTagKeys.JobId;

    /// <summary>Identifies the connector the resource is reachable through.</summary>
    public const string ConnectorIdTag = ServyxTagKeys.ConnectorId;

    private ServyxLightsailTags(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the resource backs.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for it.</summary>
    public string JobId { get; }

    /// <summary>The connector it is reachable through.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The only way to obtain a <see cref="ServyxLightsailTags"/>. Every parameter is required and is checked
    /// against Lightsail's tag-value rules.
    /// </summary>
    /// <exception cref="ArgumentException">Any argument is blank or is not expressible as a Lightsail tag value.</exception>
    public static ServyxLightsailTags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        RequireTaggableValue(instanceId, nameof(instanceId));
        RequireTaggableValue(jobId, nameof(jobId));
        RequireTaggableValue(connectorId, nameof(connectorId));

        return new ServyxLightsailTags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the canonical Servyx tag dictionary. Any <paramref name="additional"/> tags are applied first and
    /// the mandatory ones last, so an extra can never override one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs tags from a live resource's tag dictionary, or returns <see langword="null"/> if the
    /// resource is not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxLightsailTags? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
            ? new ServyxLightsailTags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a resource's tags mark it as Servyx-managed.</summary>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>
    /// Checks a whole tag dictionary against Lightsail's rules, before any resource has been created from it.
    /// </summary>
    /// <remarks>
    /// Checked up front for the same reason <c>ServyxEc2Tags.Validate</c> is: <c>AwsLightsailProvisioner.PlanAsync</c>
    /// issues no HTTP request, so a plan built from an untaggable identity would otherwise look fine on screen
    /// and fail only when <c>CreateInstances</c> is actually called.
    /// </remarks>
    /// <exception cref="ArgumentException">Any key or value would be rejected by Lightsail.</exception>
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
                    $"Tag key '{pair.Key}' is not a legal Lightsail tag key. A key must be 1-{MaxTagKeyLength} "
                    + $"characters of letters, digits, whitespace, or {AdditionalAllowedCharacters}.",
                    nameof(tags));
            }

            if (!IsTaggableValue(pair.Value))
            {
                throw new ArgumentException(
                    $"Tag value for '{pair.Key}' is not a legal Lightsail tag value. A value must be "
                    + $"1-{MaxTagValueLength} characters of letters, digits, whitespace, or "
                    + $"{AdditionalAllowedCharacters}.",
                    nameof(tags));
            }
        }

        return tags;
    }

    /// <summary>Whether <paramref name="key"/> is a legal Lightsail tag key.</summary>
    public static bool IsTaggableKey([NotNullWhen(true)] string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxTagKeyLength && IsTaggableText(key);

    /// <summary>Whether <paramref name="value"/> is a legal Lightsail tag value.</summary>
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
                $"'{value}' cannot be carried as a Lightsail tag value, so a resource created for it could not "
                + "be attributed back to Servyx by an orphan sweep. Lightsail tag values may contain letters, "
                + $"digits, whitespace and {AdditionalAllowedCharacters}, to {MaxTagValueLength} characters.",
                paramName);
        }
    }
}
