using System.Globalization;

using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The AWS Cloud Map registration an <see cref="AwsEcsFargateProvisioner"/> attaches to every service it creates,
/// and the operator's own statement of whether the resulting name is reachable from Servyx's control plane.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this buys, stated before the caveats so the caveats are not mistaken for the whole story.</strong>
/// A Fargate task's address belongs to its elastic network interface and dies every time the ECS service replaces
/// the task — which is the service's entire purpose. Registering the service in Cloud Map produces a DNS name
/// that belongs to the <em>service</em> instead: <c>&lt;service&gt;.&lt;namespace&gt;</c>. ECS registers the task's
/// interface into it when a task starts and deregisters it when the task stops, on every routine replacement, with
/// no Servyx involvement at any point. The record's contents change; the name does not. That is a genuinely
/// durable address, and it is what makes <see cref="ControlChannelAddress.Durable"/> answerable for this shape at
/// all.
/// </para>
/// <para>
/// <strong>And here is the caveat, which is not small.</strong> AWS's ECS service-discovery documentation states
/// that the DNS records created for a service discovery service "always register with the private IP address for
/// the task, rather than the public IP address, <em>even when public namespaces are used</em>". There is no
/// option to register the public address; <c>assignPublicIp</c> does not change it. So the durable name resolves
/// to an RFC 1918 address inside the task's VPC, and a private DNS namespace additionally resolves only through
/// that VPC's own Route 53 Resolver. A name that is durable and unroutable is
/// <see cref="ControlChannelAddress.Ephemeral"/>'s trap wearing a different costume, and this type exists so that
/// Servyx never walks into it by assumption.
/// </para>
/// <para>
/// <strong>Which is why reachability is an attestation and not a boolean.</strong> Whether Servyx's control plane
/// can resolve and route into that VPC is a fact about the operator's own topology — a control plane running in
/// the same VPC, a peering connection, a Transit Gateway attachment, a VPN — and there is no AWS call that
/// answers it. <c>GetNamespace</c> does not even report the VPC a private namespace was created for, so Servyx
/// cannot compare it to anything. <see cref="ControlPlaneVpcAccess"/> is therefore a string in which the operator
/// says <em>how</em> the route exists, and that sentence is carried verbatim into
/// <see cref="ControlChannelAddress.Durable.Justification"/> — so the evidence for the one claim in that type
/// that is expensive to get wrong is the operator's own, attributed, and visible on screen. Leaving it
/// <see langword="null"/> is the default and yields <see cref="ControlChannelAddress.NoAddress"/> naming the
/// durable name that exists and cannot be used. A <c>bool</c> would have been settable by accident; a sentence
/// somebody had to write is not.
/// </para>
/// <para>
/// <strong>The namespace must already exist, and that is the orphan argument.</strong> Servyx creates the Cloud
/// Map <em>service</em> and destroys it. It does not create the namespace, because creating a private DNS
/// namespace creates a Route 53 private hosted zone: separately billed every month, with a lifetime spanning
/// every server in it, invisible to a sweep that enumerates ECS services in one cluster, and therefore exactly
/// the unattributable-billing shape the EFS file system and ACI's storage account already are. One of those per
/// deployment is a finding; manufacturing a second would be a choice.
/// </para>
/// </remarks>
public sealed record AwsFargateServiceDiscovery
{
    /// <summary>The prefix every Cloud Map namespace id carries.</summary>
    public const string NamespaceIdPrefix = "ns-";

    /// <summary>The DNS record type ECS registers a Fargate task's interface as.</summary>
    /// <remarks>
    /// <c>A</c> rather than <c>SRV</c>. An <c>SRV</c> record would carry the port as well, which sounds useful —
    /// but the port a control channel connects on is definition-level knowledge that lives in
    /// <c>RconControlChannelSpec</c>, not provider knowledge, and an <c>SRV</c> lookup is not something an RCON
    /// client performs. An <c>A</c> record is what a plain host name resolves to, and a plain host name is
    /// exactly what <see cref="ControlChannelAddress.Durable"/> carries.
    /// </remarks>
    public const string RecordType = "A";

    /// <summary>The Cloud Map routing policy this adapter writes.</summary>
    /// <remarks>
    /// <c>MULTIVALUE</c> is ECS's own default for service discovery and is correct even at a desired count of
    /// one: during a replacement the old task may still be registered while the new one appears, and a weighted
    /// policy would answer with one of them by weight rather than with the set.
    /// </remarks>
    public const string RoutingPolicy = "MULTIVALUE";

    /// <summary>
    /// The default DNS TTL, in seconds, of the record ECS registers.
    /// </summary>
    /// <remarks>
    /// Short on purpose. The name is durable; the address behind it is not, and it changes precisely when the
    /// service replaces the task. A long TTL would leave a resolver caching a dead task's address for exactly as
    /// long as the TTL says, which converts a durable name back into an intermittently wrong one.
    /// </remarks>
    public const int DefaultRecordTtlSeconds = 15;

    /// <summary>
    /// The consecutive-failure threshold of the ECS-managed custom health check.
    /// </summary>
    /// <remarks>
    /// <c>HealthCheckCustomConfig</c> rather than <c>HealthCheckConfig</c>: the latter is a Route 53 health check
    /// that only works for public namespaces and bills per check, while the former hands health entirely to ECS,
    /// which already knows the container's state and reports it to Cloud Map at no extra charge. AWS's own
    /// service-discovery guidance recommends it for ECS, and it is the only one of the two that can work for a
    /// private namespace.
    /// </remarks>
    public const int HealthCheckFailureThreshold = 1;

    /// <summary>Creates a service-discovery configuration.</summary>
    /// <param name="namespaceId">
    /// The id (<c>ns-…</c>) or ARN of a Cloud Map namespace that <strong>must already exist</strong>. Servyx
    /// creates no namespace; see the type remarks for why.
    /// </param>
    /// <param name="controlPlaneVpcAccess">
    /// The operator's own statement of how Servyx's control plane resolves and routes into the namespace's VPC —
    /// for example "the control plane runs in the same VPC and subnet as the tasks". <see langword="null"/>, the
    /// default, means no such route has been claimed, and the provisioner will then refuse to hand a control
    /// channel the name even though the name is durable. Never verified by Servyx, because no AWS call can
    /// verify it; carried verbatim into the durability justification so the claim is attributable.
    /// </param>
    /// <param name="recordTtlSeconds">The DNS TTL of the registered record. Defaults to <see cref="DefaultRecordTtlSeconds"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="namespaceId"/> is blank or is not a namespace id or ARN.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recordTtlSeconds"/> is not positive.</exception>
    public AwsFargateServiceDiscovery(
        string namespaceId,
        string? controlPlaneVpcAccess = null,
        int recordTtlSeconds = DefaultRecordTtlSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(recordTtlSeconds, 1);

        if (!namespaceId.StartsWith(NamespaceIdPrefix, StringComparison.Ordinal)
            && !namespaceId.StartsWith("arn:", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{namespaceId}' is not an AWS Cloud Map namespace id. One looks like "
                + $"'{NamespaceIdPrefix}0123456789abcdef' - or an ARN, for a namespace shared with this account. "
                + "Servyx never creates the namespace, so this must name one that already exists; checked here "
                + "rather than at CreateService, because by then a caller has already approved a plan.",
                nameof(namespaceId));
        }

        if (controlPlaneVpcAccess is not null && string.IsNullOrWhiteSpace(controlPlaneVpcAccess))
        {
            throw new ArgumentException(
                "A control-plane VPC access attestation must say something. Pass null to state that no route "
                + "exists - which is the safe default and yields no control address - rather than an empty "
                + "string, which would put a blank justification on a Durable address a control channel is "
                + "about to be opened on.",
                nameof(controlPlaneVpcAccess));
        }

        NamespaceId = namespaceId;
        ControlPlaneVpcAccess = controlPlaneVpcAccess;
        RecordTtlSeconds = recordTtlSeconds;
    }

    /// <summary>The pre-existing Cloud Map namespace every service is registered into. Never created or destroyed by Servyx.</summary>
    public string NamespaceId { get; }

    /// <summary>
    /// The operator's statement of how the control plane reaches the namespace's VPC, or <see langword="null"/>
    /// when none has been made. Never verified; see the type remarks.
    /// </summary>
    public string? ControlPlaneVpcAccess { get; }

    /// <summary>The DNS TTL, in seconds, of the record ECS registers for the task.</summary>
    public int RecordTtlSeconds { get; }

    /// <summary>Whether a durable control address may be reported at all for names in this namespace.</summary>
    /// <remarks>
    /// A name being durable and a name being usable are different questions, and this is the second one. See
    /// <c>AwsEcsFargateProvisioner.ResolveControlAddressAsync</c>.
    /// </remarks>
    public bool ControlPlaneCanReachVpc => ControlPlaneVpcAccess is { Length: > 0 };
}

/// <summary>
/// A point-in-time snapshot of AWS Cloud Map's published list price, and the arithmetic that folds it into a
/// Fargate estimate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>THIS TABLE IS A SNAPSHOT AND WILL GO STALE</strong>, on the same terms as
/// <see cref="AwsFargatePricing"/> and for the same reason: <c>AwsEcsFargateProvisioner.PlanAsync</c> issues no
/// HTTP request, so a cost figure a plan carries has to be pure computation over published rates.
/// </para>
/// <para>
/// <strong>What is included, exactly.</strong> One registered resource. A Servyx Fargate service runs one task,
/// ECS registers that task's interface as one Cloud Map instance, and Cloud Map bills
/// <see cref="PerRegisteredResourcePerMonth"/> per registered resource per month. That is a real, predictable,
/// per-server charge and it is added to the Fargate compute figure rather than mentioned in prose, because a
/// number a caller compares against another provider should include everything Servyx knows will be billed for
/// the thing it is about to create.
/// </para>
/// <para>
/// <strong>What is excluded, and why each exclusion is honest rather than convenient.</strong>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>The Route 53 hosted zone behind the namespace</strong> — currently 0.50 USD per zone per month for
/// the first 25. Excluded because Servyx does not create the namespace and the zone is shared by every service
/// in it: attributing the whole of it to one server would overstate that server's cost, and attributing a
/// fraction would require knowing how many other services share it. It is named in <see cref="Source"/> instead.
/// </description></item>
/// <item><description>
/// <strong>DNS queries</strong> — currently 0.40 USD per million standard Route 53 queries. Excluded because the
/// count is a property of how often clients resolve the name, which Servyx cannot know and which for a control
/// channel resolving once per operation is negligible against a rounding error. Named in <see cref="Source"/>
/// rather than guessed at.
/// </description></item>
/// <item><description>
/// <strong>Cloud Map <c>DiscoverInstances</c> API calls</strong> — 1.00 USD per million. Excluded because this
/// adapter makes none: the address is resolved by DNS, not by the discovery API.
/// </description></item>
/// </list>
/// <para>
/// Everything <see cref="AwsFargatePricing"/> already excludes stays excluded; this class adds one line to that
/// figure and adds its own caveats to the prose, it does not make the estimate all-in.
/// </para>
/// </remarks>
public static class AwsCloudMapPricing
{
    /// <summary>The date the rate below was read off AWS's public pricing page.</summary>
    public const string SnapshotDate = "2026-07-29";

    /// <summary>The published list price per registered resource per month.</summary>
    public const decimal PerRegisteredResourcePerMonth = 0.10m;

    /// <summary>How many resources a Servyx Fargate service registers: one task, therefore one.</summary>
    public const int RegisteredResourcesPerService = 1;

    /// <summary>The human-readable provenance of the Cloud Map half of a folded estimate.</summary>
    public const string Source =
        "PLUS AWS Cloud Map service discovery: "
        + "0.10 USD per registered resource per month x 1 registered resource (one task), list price snapshot "
        + "taken " + SnapshotDate + " from https://aws.amazon.com/cloud-map/pricing/ (not refreshed at runtime). "
        + "NOT INCLUDED in that addition: the Route 53 hosted zone behind the Cloud Map namespace (currently "
        + "0.50 USD per zone per month), because Servyx does not create the namespace and the zone is shared by "
        + "every service in it; and Route 53 DNS query charges (currently 0.40 USD per million), because the "
        + "query count depends on how often clients resolve the name. Cloud Map's DiscoverInstances API charge "
        + "does not apply: this adapter resolves the address by DNS and makes no discovery API call.";

    /// <summary>
    /// Adds the Cloud Map registration's cost to a Fargate compute estimate.
    /// </summary>
    /// <remarks>
    /// An estimate whose <see cref="CostEstimate.Confidence"/> is <see cref="CostConfidence.Unknown"/> is
    /// returned with its prose extended and its figures still absent. Adding a known number to an unknown one
    /// produces a number that looks like a total and is not, which is the single most misleading thing a cost
    /// line can do.
    /// </remarks>
    /// <param name="compute">The Fargate compute estimate to fold into.</param>
    /// <returns>The combined estimate, carrying both sources.</returns>
    public static CostEstimate Fold(CostEstimate compute)
    {
        ArgumentNullException.ThrowIfNull(compute);

        var monthly = PerRegisteredResourcePerMonth * RegisteredResourcesPerService;

        if (compute.Confidence == CostConfidence.Unknown || compute.Hourly is null || compute.Monthly is null)
        {
            return compute with
            {
                Source = compute.Source + " " + Source
                    + " That addition could not be applied here, because the compute figure itself is unknown "
                    + "and a partial total would read as a complete one.",
            };
        }

        return compute with
        {
            Hourly = decimal.Round(
                compute.Hourly.Value + (monthly / AwsFargatePricing.HoursPerMonth),
                4,
                MidpointRounding.AwayFromZero),
            Monthly = decimal.Round(compute.Monthly.Value + monthly, 2, MidpointRounding.AwayFromZero),
            Source = compute.Source + " " + Source,
        };
    }

    /// <summary>A plain statement of the Cloud Map charge, for a plan stage to say out loud.</summary>
    public static string DescribeCharge() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"BILLABLE: {PerRegisteredResourcePerMonth} USD per registered resource per month, and a Servyx service registers exactly {RegisteredResourcesPerService} (its one task). This is folded into the plan's cost estimate. NOT folded in, and billed anyway: the namespace's Route 53 hosted zone (shared, and not Servyx's to create) and Route 53 DNS query charges (volume-dependent).");
}
