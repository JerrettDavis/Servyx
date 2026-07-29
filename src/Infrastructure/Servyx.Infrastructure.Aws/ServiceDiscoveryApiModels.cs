using System.Text.Json.Nodes;

namespace Servyx.Infrastructure.Aws;

/// <summary>
/// The AWS Cloud Map (<c>servicediscovery</c>) objects this adapter reads, projected out of the API's JSON into
/// ordinary records.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fourth AWS service in this assembly, and the fourth to need nothing from the signer.</strong> Cloud
/// Map speaks AWS JSON 1.1 with an <c>X-Amz-Target</c> header exactly as ECS and Lightsail do, so
/// <see cref="AwsRequestSigner"/> serves it unmodified with <c>servicediscovery</c> replacing <c>ecs</c> in the
/// credential scope. Not one line of <c>AwsSigV4.cs</c> was touched to add it.
/// </para>
/// <para>
/// <strong>The one protocol wrinkle worth stating: Cloud Map capitalises its members.</strong> ECS writes
/// <c>{"key":…,"value":…}</c>; Cloud Map writes <c>{"Key":…,"Value":…}</c>, and every other member is
/// <c>PascalCase</c> too. <see cref="LightsailJson.Tags"/> therefore cannot be reused for a Cloud Map tag array,
/// which is why <see cref="ServiceDiscoveryJson.Tags"/> exists rather than the reader being shared. A silently
/// empty tag dictionary here would read as "not Servyx's", which is exactly the direction that stops a delete —
/// safe, but it would leave a resource behind for a reason nobody could see.
/// </para>
/// </remarks>
internal static class ServiceDiscoveryProtocol
{
    /// <summary>The AWS JSON 1.1 target prefix every Cloud Map action name is appended to.</summary>
    /// <remarks>
    /// The name and the date are both historical: Cloud Map was launched as "Route 53 Auto Naming" and AWS never
    /// re-versioned the wire protocol when it was renamed. Spelled out here so the one place it could be mistyped
    /// is the one place a test can pin.
    /// </remarks>
    internal const string TargetPrefix = "Route53AutoNaming_v20170314.";

    /// <summary>The service name in the SigV4 credential scope.</summary>
    internal const string ServiceName = "servicediscovery";

    /// <summary>The content type every Cloud Map request and response carries.</summary>
    internal const string ContentType = "application/x-amz-json-1.1";
}

/// <summary>The Cloud Map error type names this adapter distinguishes by.</summary>
/// <remarks>
/// Unlike ECS, Cloud Map reports every absence as an HTTP 400 with a typed error rather than in a
/// <c>failures</c> array, so all four of these arrive as exceptions and are turned back into values by
/// <see cref="ServiceDiscoveryJsonApiClient"/>. <see cref="ResourceInUse"/> is the one that is <em>not</em>
/// turned into a value: a Cloud Map service that still has registered instances is a service that must not be
/// abandoned, and swallowing that error is how a caller would come to believe a cleanup had happened.
/// </remarks>
internal static class ServiceDiscoveryErrorCodes
{
    /// <summary>No namespace exists with the id the request named.</summary>
    internal const string NamespaceNotFound = "NamespaceNotFound";

    /// <summary>No service exists with the id the request named.</summary>
    internal const string ServiceNotFound = "ServiceNotFound";

    /// <summary>A service with the same name already exists in the namespace.</summary>
    internal const string ServiceAlreadyExists = "ServiceAlreadyExists";

    /// <summary>The service still contains registered instances, so it cannot be deleted yet.</summary>
    internal const string ResourceInUse = "ResourceInUse";

    /// <summary>A request value was refused.</summary>
    internal const string InvalidInput = "InvalidInput";
}

/// <summary>Reading primitives for Cloud Map's PascalCase JSON. See <see cref="ServiceDiscoveryProtocol"/>.</summary>
internal static class ServiceDiscoveryJson
{
    /// <summary>Projects a Cloud Map <c>Tags</c> array, whose members are <c>Key</c>/<c>Value</c> and not <c>key</c>/<c>value</c>.</summary>
    internal static IReadOnlyDictionary<string, string> Tags(JsonArray? tags)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (tags is null)
        {
            return result;
        }

        foreach (var node in tags)
        {
            if (node is JsonObject tag && LightsailJson.Text(tag, "Key") is { } key)
            {
                result[key] = LightsailJson.Text(tag, "Value") ?? string.Empty;
            }
        }

        return result;
    }
}

/// <summary>
/// One AWS Cloud Map service — the object that owns a DNS name and the set of instances registered under it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the durable half of a Fargate address, and the only part of it Servyx creates.</strong> The
/// instances registered inside it come and go with the ECS task; the service, and therefore the name, does not.
/// See <c>AwsEcsFargateProvisioner</c>'s remarks on service discovery for the whole argument.
/// </para>
/// <para>
/// <see cref="InstanceCount"/> is worth reading before a delete is attempted, but is not relied on: Cloud Map
/// refuses <c>DeleteService</c> outright with <see cref="ServiceDiscoveryErrorCodes.ResourceInUse"/> while any
/// instance is registered, so the authority on whether a delete may proceed is the delete's own answer rather
/// than a count read a moment earlier.
/// </para>
/// </remarks>
/// <param name="Arn">The service's ARN — what an ECS <c>serviceRegistries</c> entry names, and what a tag read takes.</param>
/// <param name="Id">The service's id, e.g. <c>srv-0123456789abcdef</c>.</param>
/// <param name="Name">The service's name. The first label of the DNS name a client resolves.</param>
/// <param name="NamespaceId">The namespace the service lives in. Never created or destroyed by Servyx.</param>
/// <param name="InstanceCount">How many instances Cloud Map currently has registered. Advisory; see the type remarks.</param>
internal sealed record CloudMapService(
    string Arn,
    string? Id,
    string? Name,
    string? NamespaceId,
    int InstanceCount)
{
    /// <summary>Projects the <c>Service</c> object a create or get returns.</summary>
    internal static CloudMapService? From(JsonObject? item)
    {
        var arn = LightsailJson.Text(item, "Arn");
        if (arn is null)
        {
            return null;
        }

        var count = 0;
        if (item is not null && item.TryGetPropertyValue("InstanceCount", out var node) && node is not null)
        {
            try
            {
                count = node.GetValue<int>();
            }
            catch (InvalidOperationException)
            {
                count = 0;
            }
            catch (FormatException)
            {
                count = 0;
            }
        }

        return new CloudMapService(
            arn,
            LightsailJson.Text(item, "Id"),
            LightsailJson.Text(item, "Name"),
            LightsailJson.Text(item, "NamespaceId"),
            count);
    }
}

/// <summary>
/// One AWS Cloud Map namespace — the DNS suffix a service's name is completed by, and the object Servyx
/// deliberately never creates.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Type"/> is the field the whole reachability question turns on, and it is read from the
/// provider rather than assumed.</strong> A <see cref="HttpType"/> namespace has no DNS records at all — its
/// instances are discoverable only through Cloud Map's <c>DiscoverInstances</c> API, which no RCON client speaks
/// — so a name in one is not an address. A <see cref="PrivateDnsType"/> namespace resolves through the VPC's own
/// Route 53 Resolver and nowhere else. A <see cref="PublicDnsType"/> namespace resolves globally and is
/// <em>still</em> not reachable for this shape, because ECS registers the task's private IPv4 into it — AWS's own
/// service-discovery documentation states that DNS records "always register with the private IP address for the
/// task, rather than the public IP address, even when public namespaces are used". Both DNS types therefore land
/// in the same place: the name is durable, and it resolves to an address inside the VPC.
/// </para>
/// <para>
/// <strong>Servyx never creates a namespace, and the reason is the shape of the orphan it would become.</strong>
/// Creating a private DNS namespace creates a Route 53 private hosted zone, which bills monthly, has a lifetime
/// independent of any one server, and would be invisible to a sweep that enumerates ECS services in a cluster —
/// the same unattributable-billing shape as the EFS file system and ACI's storage account. Requiring it to
/// pre-exist moves it into the class of things this adapter names as REQUIRES and can therefore never leave
/// behind.
/// </para>
/// </remarks>
/// <param name="Id">The namespace's id, e.g. <c>ns-0123456789abcdef</c>.</param>
/// <param name="Name">The namespace's name, e.g. <c>servyx.local</c>. The DNS suffix of every service in it.</param>
/// <param name="Type">One of <see cref="PrivateDnsType"/>, <see cref="PublicDnsType"/> or <see cref="HttpType"/>.</param>
/// <param name="Arn">The namespace's ARN.</param>
internal sealed record CloudMapNamespace(string? Id, string? Name, string? Type, string? Arn)
{
    /// <summary>A namespace whose records resolve only through the VPC's Route 53 Resolver.</summary>
    internal const string PrivateDnsType = "DNS_PRIVATE";

    /// <summary>A namespace whose records resolve globally — which does not make this shape's address routable.</summary>
    internal const string PublicDnsType = "DNS_PUBLIC";

    /// <summary>A namespace with no DNS records at all; instances are found only via <c>DiscoverInstances</c>.</summary>
    internal const string HttpType = "HTTP";

    /// <summary>Whether this namespace publishes DNS records a socket could resolve.</summary>
    internal bool IsDns =>
        string.Equals(Type, PrivateDnsType, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, PublicDnsType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Projects the <c>Namespace</c> object a get returns.</summary>
    internal static CloudMapNamespace? From(JsonObject? item)
    {
        if (item is null)
        {
            return null;
        }

        var id = LightsailJson.Text(item, "Id");
        var arn = LightsailJson.Text(item, "Arn");

        return id is null && arn is null
            ? null
            : new CloudMapNamespace(
                id,
                LightsailJson.Text(item, "Name"),
                LightsailJson.Text(item, "Type"),
                arn);
    }
}
