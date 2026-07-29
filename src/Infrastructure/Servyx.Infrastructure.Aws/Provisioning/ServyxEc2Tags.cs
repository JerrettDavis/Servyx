using System.Diagnostics.CodeAnalysis;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The universal Servyx tags every EC2 resource this project creates must carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read this file next to <c>ServyxDropletTags</c> and <c>ServyxAzureTags</c>; the three-way comparison
/// is the finding.</strong>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <em>DigitalOcean</em> needed a whole <strong>encoder</strong>. Its tags are a flat <c>string[]</c> whose
/// charset excludes both <c>.</c> and <c>=</c>, so <c>servyx.managed=true</c> is not expressible and becomes
/// <c>servyx_managed:true</c> — a mapping that has to be proved reversible in both directions, with the
/// documented consequence that an instance id containing <c>.</c> cannot be provisioned at all.
/// </description></item>
/// <item><description>
/// <em>Azure</em> needed <strong>nothing</strong>. ARM tags are a native dictionary, a name may contain
/// anything except <c>&lt;&gt;%&amp;\?/</c>, and a value has no charset restriction at all.
/// </description></item>
/// <item><description>
/// <em>EC2</em> — this file — needs <strong>no encoding either, but unlike Azure it does need real
/// validation</strong>. EC2 tags are native key/value pairs and <c>.</c> is legal in both halves, so
/// <c>servyx.managed</c> is stored, filtered on, and read back as the literal string <c>servyx.managed</c>.
/// But EC2 does restrict the charset — letters, digits, whitespace and <c>+ - = . _ : / @</c>, to 128
/// characters for a key and 256 for a value — and it reserves the whole <c>aws:</c> prefix, refusing any write
/// that uses it. So this type is a <strong>validator</strong> where the DigitalOcean one is a codec and the
/// Azure one is barely either.
/// </description></item>
/// </list>
/// <para>
/// The practical effect for a caller is that EC2 sits between the other two: a Servyx instance id containing a
/// <c>.</c> is accepted here (DigitalOcean refuses it) but one containing, say, a <c>#</c> or a <c>%</c> is
/// refused here (Azure would accept it). That is stated rather than smoothed over, because a caller whose id is
/// rejected needs to know why — and because a tag that silently failed to apply would make a billing instance
/// invisible to <see cref="IProvisioner.ReconcileAsync"/>.
/// </para>
/// <para>
/// <strong>Validation happens before the launch, not during it.</strong> EC2 rejects an illegal tag with a 400
/// on the <c>RunInstances</c> call itself, which is actually the benign case — nothing is created. The reason
/// to check first anyway is that <see cref="AwsEc2Provisioner.PlanAsync"/> issues no HTTP request, so a plan
/// built from an untaggable identity would otherwise look fine on screen and fail only at apply time.
/// </para>
/// <para>
/// <strong>Nothing this adapter creates is untaggable.</strong> Worth stating explicitly, because it is the one
/// place AWS is materially better than Azure: <c>RunInstances</c> takes a <c>TagSpecification</c> per resource
/// <em>type</em>, so the instance and the EBS volumes created with it are both tagged by the same atomic call
/// that creates them. Azure's managed OS disk cannot be tagged at all and its subnet has no tag collection;
/// there is no EC2 equivalent of either gap.
/// </para>
/// </remarks>
public sealed class ServyxEc2Tags
{
    /// <summary>The maximum length EC2 accepts for a tag key.</summary>
    public const int MaxTagKeyLength = 128;

    /// <summary>The maximum length EC2 accepts for a tag value.</summary>
    public const int MaxTagValueLength = 256;

    /// <summary>The tag-key prefix AWS reserves for itself. A write using it is refused by EC2.</summary>
    public const string ReservedKeyPrefix = "aws:";

    /// <summary>The characters EC2 allows in a tag beyond letters, digits and whitespace.</summary>
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

    /// <summary>
    /// Names the role a resource plays in the host: <c>instance</c> or <c>volume</c>.
    /// </summary>
    /// <remarks>
    /// The same key, for the same reason, as the Azure adapter's <c>servyx.role</c>: a sweep here returns two
    /// kinds of object and <see cref="ResourceHandle"/> carries exactly one
    /// <see cref="ResourceHandle.ProviderResourceId"/> with no field saying what kind of thing it names. The
    /// role has to live somewhere, and the only free-form state a handle already carries is its tags.
    /// Descriptive rather than identifying, so — like <see cref="ServyxTagKeys.RootPath"/> — it travels as an
    /// ordinary extra and can never shadow a canonical key.
    /// </remarks>
    public const string RoleTag = ServyxTagKeys.Prefix + "role";

    /// <summary>The role value stamped on the EC2 instance.</summary>
    public const string RoleInstance = "instance";

    /// <summary>The role value stamped on the EBS volumes the launch creates.</summary>
    public const string RoleVolume = "volume";

    /// <summary>The EC2 tag key that gives a resource its display name in the console.</summary>
    /// <remarks>
    /// Not a Servyx key and not part of the identity — it exists because an untitled instance in the EC2
    /// console is indistinguishable from every other untitled instance, which makes a human audit of what
    /// Servyx owns needlessly hard. It is written as an ordinary extra, so it can never shadow a canonical key.
    /// </remarks>
    public const string NameTag = "Name";

    private ServyxEc2Tags(string instanceId, string jobId, string connectorId)
    {
        InstanceId = instanceId;
        JobId = jobId;
        ConnectorId = connectorId;
    }

    /// <summary>The Servyx server/instance the resources back.</summary>
    public string InstanceId { get; }

    /// <summary>The provisioning job that asked for them.</summary>
    public string JobId { get; }

    /// <summary>The connector they are reachable through.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The EC2 filter name that selects every Servyx-managed resource: <c>tag:servyx.managed</c>.
    /// </summary>
    /// <remarks>
    /// Note the shape of the win over the DigitalOcean equivalent, and the parity with Azure's: there, the
    /// filter is an <em>encoded</em> string (<c>servyx_managed:true</c>) that a human auditing the account has
    /// to know to type instead of the real key. Here the filter names the key as Servyx spells it, so what a
    /// human types into the console and what the code sends are the same string.
    /// </remarks>
    public static string ManagedFilterName { get; } = "tag:" + ManagedTag;

    /// <summary>
    /// The only way to obtain a <see cref="ServyxEc2Tags"/>. Every parameter is required and is checked against
    /// EC2's tag-value rules.
    /// </summary>
    /// <exception cref="ArgumentException">Any argument is blank or is not expressible as an EC2 tag value.</exception>
    public static ServyxEc2Tags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        RequireTaggableValue(instanceId, nameof(instanceId));
        RequireTaggableValue(jobId, nameof(jobId));
        RequireTaggableValue(connectorId, nameof(connectorId));

        return new ServyxEc2Tags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the canonical Servyx tag dictionary. Any <paramref name="additional"/> tags are applied first and
    /// the mandatory ones last, so an extra can never override one.
    /// </summary>
    /// <remarks>
    /// The ordering rule is <see cref="ServyxTagKeys.Build"/>'s, applied by calling it — the same single
    /// implementation the Docker, SSH, DigitalOcean and Azure adapters call. What differs is only what happens
    /// to the result afterwards: there is no encoding step here, so the dictionary this returns is
    /// byte-for-byte the dictionary that reaches EC2.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs tags from a live resource's tag dictionary, or returns <see langword="null"/> if the
    /// resource is not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxEc2Tags? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
            ? new ServyxEc2Tags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a resource's tags mark it as Servyx-managed.</summary>
    /// <remarks>
    /// Delegates to <see cref="ServyxTagKeys.IsManaged"/> — an exact ordinal match, not a truthiness test, for
    /// the same reason it is one there: a sweep's output is a delete list, and a sweep that guesses wrong here
    /// terminates someone else's instance.
    /// </remarks>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>
    /// Checks a whole tag dictionary against EC2's rules, before any resource has been created from it.
    /// </summary>
    /// <exception cref="ArgumentException">Any key or value would be rejected by EC2.</exception>
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
                    $"Tag key '{pair.Key}' is not a legal EC2 tag key. A key must be 1-{MaxTagKeyLength} "
                    + $"characters of letters, digits, whitespace, or {AdditionalAllowedCharacters}. Note that "
                    + "'.' is legal here, unlike in a DigitalOcean tag, so no encoding is applied.",
                    nameof(tags));
            }

            if (!IsTaggableValue(pair.Value))
            {
                throw new ArgumentException(
                    $"Tag value for '{pair.Key}' is not a legal EC2 tag value. A value must be "
                    + $"1-{MaxTagValueLength} characters of letters, digits, whitespace, or "
                    + $"{AdditionalAllowedCharacters} - a narrower set than Azure, which places no charset "
                    + "restriction on a tag value at all.",
                    nameof(tags));
            }
        }

        return tags;
    }

    /// <summary>Whether <paramref name="key"/> is a legal EC2 tag key.</summary>
    public static bool IsTaggableKey([NotNullWhen(true)] string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxTagKeyLength && IsTaggableText(key);

    /// <summary>Whether <paramref name="value"/> is a legal EC2 tag value.</summary>
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
                $"'{value}' cannot be carried as an EC2 tag value, so a resource created for it could not be "
                + "attributed back to Servyx by an orphan sweep. EC2 tag values may contain letters, digits, "
                + $"whitespace and {AdditionalAllowedCharacters}, to {MaxTagValueLength} characters - notably "
                + "'.' IS allowed, unlike in a DigitalOcean tag.",
                paramName);
        }
    }
}
