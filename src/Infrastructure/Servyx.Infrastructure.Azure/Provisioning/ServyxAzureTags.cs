using System.Diagnostics.CodeAnalysis;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Azure.Provisioning;

/// <summary>
/// The universal Servyx tags every ARM resource this project creates must carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Compare this file with <c>ServyxDropletTags</c>; the difference is the finding.</strong> The
/// DigitalOcean file is an <em>encoder</em>, and has to be: DigitalOcean tags are a flat <c>string[]</c>
/// whose charset excludes both <c>.</c> and <c>=</c>, so Servyx's <c>servyx.managed=true</c> is not
/// expressible and becomes <c>servyx_managed:true</c> — a lossy-looking mapping that has to be proved
/// reversible in both directions, with a documented consequence that an instance id containing <c>.</c>
/// cannot be provisioned at all.
/// </para>
/// <para>
/// Azure resource tags are a native <c>IDictionary&lt;string, string&gt;</c>. Per
/// <see href="https://learn.microsoft.com/azure/azure-resource-manager/management/tag-resources">the ARM tag
/// reference</see> a tag name may be up to 512 characters and may contain anything except
/// <c>&lt;</c>, <c>&gt;</c>, <c>%</c>, <c>&amp;</c>, <c>\</c>, <c>?</c> and <c>/</c>; a value may be up to
/// 256 characters with no charset restriction at all. Every key in
/// <see cref="ServyxTagKeys"/> is legal as written, <c>.</c> included, and every Servyx identifier is legal
/// as a value. <strong>So there is no encoding here, and this type performs none.</strong>
/// <c>servyx.managed</c> is stored as <c>servyx.managed</c>; the sweep filter is the literal key and value,
/// not a substitute for them; and an instance id containing a <c>.</c> — rejected outright by the
/// DigitalOcean adapter — is accepted here without comment.
/// </para>
/// <para>
/// <strong>What remains is validation, and it is not decorative.</strong> ARM rejects an over-long or
/// illegally-charactered tag with a 400 <em>on the write that would have created the resource</em>. For the
/// resource group, the first write in the sequence, that is harmless. For the VM, the last write, it would
/// mean four already-created resources and a failure — so the tags are checked before the sequence starts
/// rather than discovered invalid halfway through it.
/// </para>
/// <para>
/// <strong>What is deliberately not tagged.</strong> Two of the objects an Azure host is made of cannot carry
/// tags at all, and no amount of care here changes that: a <em>subnet</em> is an ARM sub-resource of its
/// virtual network and has no tags collection, and the VM's <em>managed OS disk</em> is created implicitly by
/// the VM write rather than PUT by Servyx, so there is no request in which Servyx could attach tags to it.
/// Both are handled by lifetime instead of by tagging — the subnet dies with its (tagged) virtual network,
/// and the OS disk is declared <c>deleteOption: "Delete"</c> so it dies with the VM. Neither is discoverable
/// by an orphan sweep, and the provisioner's remarks say so rather than implying the sweep covers everything.
/// </para>
/// </remarks>
public sealed class ServyxAzureTags
{
    /// <summary>The maximum length ARM accepts for a tag name.</summary>
    public const int MaxTagNameLength = 512;

    /// <summary>The maximum length ARM accepts for a tag value.</summary>
    public const int MaxTagValueLength = 256;

    /// <summary>The characters ARM forbids in a tag name. A tag value has no such restriction.</summary>
    public const string ForbiddenTagNameCharacters = "<>%&\\?/";

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
    /// Names the role a resource plays in the multi-resource host: <c>virtual-machine</c>,
    /// <c>network-interface</c>, <c>public-ip</c>, <c>virtual-network</c>, <c>resource-group</c>.
    /// </summary>
    /// <remarks>
    /// A key the DigitalOcean adapter has no need for, because a droplet is the whole host. Here a sweep gets
    /// back five kinds of object and a caller has to be able to order a teardown correctly without parsing
    /// ARM resource-type strings, so the role is recorded on the resource itself. Descriptive rather than
    /// identifying, so — like <see cref="ServyxTagKeys.RootPath"/> — it travels as an ordinary extra and
    /// never shadows a canonical key.
    /// </remarks>
    public const string RoleTag = ServyxTagKeys.Prefix + "role";

    /// <summary>The role value stamped on the VM.</summary>
    public const string RoleVirtualMachine = "virtual-machine";

    /// <summary>The role value stamped on the network interface.</summary>
    public const string RoleNetworkInterface = "network-interface";

    /// <summary>The role value stamped on the public IP address.</summary>
    public const string RolePublicIp = "public-ip";

    /// <summary>The role value stamped on the virtual network.</summary>
    public const string RoleVirtualNetwork = "virtual-network";

    /// <summary>The role value stamped on a resource group Servyx itself created.</summary>
    public const string RoleResourceGroup = "resource-group";

    /// <summary>
    /// Records the ARM name of a subsidiary resource on the VM, so a teardown driven from the VM's handle
    /// alone knows what else it has to delete and in which order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the multi-resource shape leaking into the tag vocabulary, and it is worth being blunt
    /// about why.</strong> <see cref="ResourceHandle"/> carries exactly one
    /// <see cref="ResourceHandle.ProviderResourceId"/>. A droplet fits that perfectly. An Azure host does not:
    /// it is five objects, and a handle naming only the VM would let a destroy leave a billable public IP
    /// behind with nothing local pointing at it. Rather than widen a domain type shared by every adapter, the
    /// sibling names are recorded in the one place a handle already carries free-form state — its tags — which
    /// has the additional property that they survive on the resource at the provider even if Servyx's local
    /// record is lost.
    /// </para>
    /// <para>
    /// Recorded as names, not full ARM resource ids: the subscription is adapter state and the resource group
    /// is already a tag, so a name is enough to rebuild the id, and an ARM id would eat most of the
    /// 256-character value budget.
    /// </para>
    /// </remarks>
    public const string ResourceGroupTag = ServyxTagKeys.Prefix + "azure-resource-group";

    /// <summary>Records the ARM name of the virtual network created for the host.</summary>
    public const string VirtualNetworkTag = ServyxTagKeys.Prefix + "azure-virtual-network";

    /// <summary>Records the ARM name of the subnet created inside the virtual network.</summary>
    public const string SubnetTag = ServyxTagKeys.Prefix + "azure-subnet";

    /// <summary>Records the ARM name of the public IP address created for the host.</summary>
    public const string PublicIpTag = ServyxTagKeys.Prefix + "azure-public-ip";

    /// <summary>Records the ARM name of the network interface created for the host.</summary>
    public const string NetworkInterfaceTag = ServyxTagKeys.Prefix + "azure-network-interface";

    private ServyxAzureTags(string instanceId, string jobId, string connectorId)
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
    /// The ARM <c>$filter</c> fragment that selects every Servyx-managed resource in a subscription.
    /// </summary>
    /// <remarks>
    /// Note the shape of the win over the DigitalOcean equivalent: there, the filter is an <em>encoded</em>
    /// string (<c>servyx_managed:true</c>) that a human auditing the account has to know to type instead of
    /// the real key. Here the filter names the key and the value as Servyx spells them, so what a human types
    /// into the portal and what the code sends are the same two strings.
    /// </remarks>
    public static string ManagedFilter { get; } =
        $"tagName eq '{ManagedTag}' and tagValue eq '{ManagedTagValue}'";

    /// <summary>
    /// The only way to obtain a <see cref="ServyxAzureTags"/>. Every parameter is required and is checked
    /// against ARM's tag-value rules.
    /// </summary>
    /// <remarks>
    /// Unlike the DigitalOcean equivalent, this rejects almost nothing a caller is likely to pass: ARM tag
    /// values have no charset restriction, so only the 256-character ceiling can fail. An instance id
    /// containing <c>.</c> — which <c>ServyxDropletTags.For</c> refuses outright — is accepted here.
    /// </remarks>
    /// <exception cref="ArgumentException">Any argument is blank or is longer than <see cref="MaxTagValueLength"/>.</exception>
    public static ServyxAzureTags For(string instanceId, string jobId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        RequireTaggableValue(instanceId, nameof(instanceId));
        RequireTaggableValue(jobId, nameof(jobId));
        RequireTaggableValue(connectorId, nameof(connectorId));

        return new ServyxAzureTags(instanceId, jobId, connectorId);
    }

    /// <summary>
    /// Builds the canonical Servyx tag dictionary. Any <paramref name="additional"/> tags are applied first
    /// and the mandatory ones last, so an extra can never override one.
    /// </summary>
    /// <remarks>
    /// The ordering rule is <see cref="ServyxTagKeys.Build"/>'s, applied by calling it — the same single
    /// implementation the Docker, SSH and DigitalOcean adapters call. What differs is only what happens to the
    /// result afterwards: there is no encoding step here, so the dictionary this returns is byte-for-byte the
    /// dictionary that reaches ARM.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ToTags(IReadOnlyDictionary<string, string>? additional = null) =>
        ServyxTagKeys.Build(InstanceId, JobId, ConnectorId, additional);

    /// <summary>
    /// Reconstructs tags from a live resource's tag dictionary, or returns <see langword="null"/> if the
    /// resource is not Servyx-managed or is missing any mandatory tag. Never invents a value for a missing tag.
    /// </summary>
    public static ServyxAzureTags? FromTags(IReadOnlyDictionary<string, string>? tags) =>
        ServyxTagKeys.TryReadIdentity(tags, out var instanceId, out var jobId, out var connectorId)
            ? new ServyxAzureTags(instanceId, jobId, connectorId)
            : null;

    /// <summary>Whether a resource's tags mark it as Servyx-managed.</summary>
    /// <remarks>
    /// Delegates to <see cref="ServyxTagKeys.IsManaged"/> — an exact ordinal match, not a truthiness test, for
    /// the same reason it is one there: a sweep's output is a delete list, and a sweep that guesses wrong here
    /// destroys someone else's virtual machine.
    /// </remarks>
    public static bool IsManaged(IReadOnlyDictionary<string, string>? tags) => ServyxTagKeys.IsManaged(tags);

    /// <summary>
    /// Copies a resource's ARM tag dictionary into the ordinal-comparer dictionary the rest of Servyx expects.
    /// </summary>
    /// <remarks>
    /// The whole of the DigitalOcean adapter's <c>FromDropletTagsToDictionary</c> — decode each tag, skip any
    /// this encoding did not produce — reduces to this, because ARM already stores a dictionary. Tags applied
    /// by humans or other tools come back as ordinary entries and are simply not Servyx keys.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> FromArmTags(IReadOnlyDictionary<string, string>? armTags)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in armTags ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            tags[pair.Key] = pair.Value;
        }

        return tags;
    }

    /// <summary>
    /// Checks a whole tag dictionary against ARM's rules, before any resource has been created from it.
    /// </summary>
    /// <exception cref="ArgumentException">Any name or value would be rejected by ARM.</exception>
    public static IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        foreach (var pair in tags)
        {
            if (!IsTaggableName(pair.Key))
            {
                throw new ArgumentException(
                    $"Tag name '{pair.Key}' is not a legal Azure tag name. A name must be 1-{MaxTagNameLength} "
                    + $"characters and must not contain any of {ForbiddenTagNameCharacters}. Note that '.' is "
                    + "legal here, unlike in a DigitalOcean tag, so no encoding is applied.",
                    nameof(tags));
            }

            if (!IsTaggableValue(pair.Value))
            {
                throw new ArgumentException(
                    $"Tag value for '{pair.Key}' is not a legal Azure tag value. A value must be "
                    + $"1-{MaxTagValueLength} characters; Azure places no charset restriction on it.",
                    nameof(tags));
            }
        }

        return tags;
    }

    /// <summary>Whether <paramref name="value"/> is a legal ARM tag value.</summary>
    public static bool IsTaggableValue([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxTagValueLength;

    /// <summary>Whether <paramref name="name"/> is a legal ARM tag name.</summary>
    public static bool IsTaggableName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxTagNameLength)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (ForbiddenTagNameCharacters.Contains(c, StringComparison.Ordinal))
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
                $"'{value}' is {value.Length} characters and cannot be carried as an Azure tag value (the limit is "
                + $"{MaxTagValueLength}), so a resource created for it could not be attributed back to Servyx by an "
                + "orphan sweep.",
                paramName);
        }
    }
}
