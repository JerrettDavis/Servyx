using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates AWS Lightsail instances — and nothing else. The fourth
/// implementation of "shape I" in this codebase, and the second under AWS's SigV4 signer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lightsail is AWS's DigitalOcean, and the adapter mostly proves that out.</strong> Flat bundle
/// pricing, one API call to create, no VPC/subnet/security-group concepts to reason about, an all-in cost
/// figure — every one of those is true, and <see cref="BuildSpec"/> is visibly shorter than
/// <c>AwsEc2Provisioner.BuildSpec</c> because there is no <c>NetworkInterface</c>-vs-top-level placement split
/// and no security-group index list to parse. What does <em>not</em> collapse to "just like DigitalOcean" is the
/// transport: this adapter still authenticates with the same hand-rolled SigV4 signer EC2 uses, because that is
/// an AWS-account property, not a per-service one. So the honest summary is narrower than "cheapest adapter,
/// full stop": it is the cheapest <em>request-building and resource-topology</em> story of the four, riding on
/// authentication machinery that was already paid for by the EC2 adapter and is reused here completely
/// unchanged.
/// </para>
/// <para>
/// <strong>JSON over HTTPS, not the EC2 Query API — confirmed, and the one place the request shape genuinely
/// differs.</strong> Every Lightsail call in <see cref="LightsailJsonApiClient"/> is a <c>POST /</c> whose
/// action is named by an <c>X-Amz-Target: Lightsail_20161128.&lt;Action&gt;</c> header, carrying a JSON object
/// body — there is no GET anywhere, because the AWS JSON 1.1 protocol has no query-string parameter shape at
/// all, not even for a pure read like <c>GetInstances</c>. That is a real asymmetry with EC2 (which sends reads
/// as GETs precisely so both halves of <c>AwsSigV4.CanonicalQuery</c> get exercised by real traffic), stated
/// rather than smoothed over. Signing needed no change at all: <see cref="AwsRequestSigner"/> already signs
/// every <c>x-amz-*</c> header on the outgoing message, so <c>X-Amz-Target</c> is covered by the same allow-list
/// that already covered <c>x-amz-security-token</c>.
/// </para>
/// <para>
/// <strong>Shape I still produces a host, not a game server.</strong> Exactly as the other three: no SteamCMD
/// invocation, no package manager, no game definition, no cloud-init authoring. The adapter creates a machine,
/// waits for it to have an address, and hands back a <see cref="TargetDescriptor"/> whose
/// <see cref="TargetDescriptor.TransportId"/> is <c>"ssh"</c>. See <c>AwsLightsailShapeIToShapeHCompositionTests</c>,
/// a near-transcription of the EC2 and DigitalOcean composition suites.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request, resolves no credential,
/// and computes no signature — the same claim <c>AwsEc2Provisioner.PlanAsync</c> makes, pinned by the same kind
/// of test.
/// </para>
/// <para>
/// <strong>Region is still adapter state, not a request parameter, for the same structural reason as EC2's.</strong>
/// Lightsail's endpoint carries the region in its hostname (<c>lightsail.us-east-1.amazonaws.com</c>) and the
/// region also names the SigV4 credential scope, so <see cref="OrphanScope.ProviderWide"/> means "region-wide"
/// here exactly as it does for EC2 - the same honest caveat, carried forward rather than re-argued.
/// </para>
/// <para>
/// <strong>Identity is caller-chosen, and that changes the orphan and compensation stories for the better.</strong>
/// An EC2 instance id and a DigitalOcean droplet id are provider-generated and unknowable before a create call
/// succeeds, which is why both of those adapters' compensation logic falls back to a tag sweep when a create
/// fails without ever reporting an id. A Lightsail instance's name <em>is</em> its identity, chosen by the
/// caller in the same <see cref="ProvisioningRequest"/> that will create it - so
/// <see cref="InstanceCreateOperation.CompensateAsync"/> can always call <c>DeleteInstance</c> by the exact name
/// a failed create would have used, with no sweep required. It also means there is no second "instance vs
/// volume" role to disambiguate on a swept <see cref="ResourceHandle"/>, unlike EC2's <c>servyx.role</c> tag,
/// because Lightsail's bundle price bakes the boot disk in - there is only ever one kind of object to find.
/// </para>
/// <para>
/// <strong>Tagging is at least as strong as EC2's, and Lightsail states something EC2 does not.</strong>
/// <c>CreateInstances</c> accepts a <c>tags</c> array applied in the same call that creates the instance, so
/// there is no window in which a billing instance exists untagged - the same guarantee EC2's
/// <c>TagSpecification</c> gives. Lightsail's own documentation goes one further: "If tags cannot be applied
/// during resource creation, Lightsail rolls back the resource creation process," i.e. the platform itself
/// promises the create is all-or-nothing with respect to tagging, not merely that Servyx never observes an
/// untagged window. There is deliberately no call to <c>TagResource</c> anywhere in this file, for the same
/// reason <c>Ec2QueryApiClient</c> has no <c>CreateTags</c> call.
/// </para>
/// <para>
/// <strong>The login username is read from the provider, not guessed.</strong> This is the one place this
/// adapter is unambiguously better than <c>AwsEc2Provisioner</c>, not merely different: EC2's
/// <c>DescribeInstances</c> has no field naming an AMI's default login account, so that adapter must carry a
/// constructor-supplied default (<c>ec2-user</c>) that is silently wrong for roughly half the AMI families a
/// caller might name. Lightsail's <c>Instance</c> object reports the blueprint's login account directly as
/// <c>username</c> - <c>bitnami</c> for an application blueprint, <c>ubuntu</c>, <c>ec2-user</c> for Amazon
/// Linux, and so on - so this adapter has no <c>sshUsername</c> constructor parameter at all and instead reads
/// the value Lightsail itself reports at create time and at every <see cref="RefreshAsync"/>. See
/// <see cref="FallbackSshUsername"/> for the narrow, defensive exception.
/// </para>
/// <para>
/// <strong>What the plan does not need to say, and that is itself the finding.</strong> EC2's plan carries an
/// explicit stage warning that the public IPv4 address is billed separately at $0.005/hour since 2024-02-01.
/// Lightsail does not meter a public address that way - it is part of the flat bundle price - so there is no
/// equivalent stage here at all, not merely an equivalent stage with a smaller number in it.
/// </para>
/// <para>
/// <strong>Ingress is still not applied, but the honest caveat is not EC2's.</strong> This adapter never calls
/// <c>PutInstancePublicPorts</c>, so a requested <see cref="MachineSpec.Ingress"/> rule is reported in the plan
/// as not applied, exactly as EC2's is. What differs is the default it is measured against: an EC2 security
/// group denies all inbound traffic until one is opened, so an unapplied rule there is actively closed.
/// Lightsail assigns each new instance a blueprint-default port configuration that, for the stock blueprints,
/// already opens inbound SSH (port 22) and sometimes HTTP/HTTPS - so "not applied" here means "no port beyond
/// the blueprint's own defaults was opened," not "every port is closed by default." Both are stated in the
/// stage text rather than one being assumed from the other.
/// </para>
/// <para>
/// <strong>Capabilities are what is implemented, not what Lightsail offers - and every omission is pinned by a
/// test.</strong> Lightsail can resize a bundle (by creating a new instance from a snapshot, not in place),
/// snapshot an instance, attach a free static IP, and manage per-instance public ports; this adapter calls none
/// of those, so <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
/// <see cref="ProvisioningCapabilities.StaticAddress"/> and <see cref="ProvisioningCapabilities.FirewallRules"/>
/// are all absent, mirroring EC2's same four omissions. There is no <see cref="IMaintainer"/> implementation
/// either, consistent with EC2 and unlike the two ARM/HTTP-header adapters.
/// </para>
/// <para>
/// <strong>Host-key trust is unresolved here exactly as it is for the other three.</strong> Lightsail exposes no
/// documented equivalent of EC2's <c>GetConsoleOutput</c> boot-log fingerprint text that this adapter could
/// parse, so - as with DigitalOcean, Azure and EC2 - it stamps no <c>trustPolicy</c> of its own and forwards the
/// caller's transport options to the SSH transport's existing host-key mechanism unchanged.
/// </para>
/// </remarks>
public sealed class AwsLightsailProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "aws-lightsail";

    /// <summary>
    /// The login username used only when Lightsail reports none for an instance - a defensive last resort, not
    /// the primary source of truth. See the type remarks: the primary source is the <c>username</c> field
    /// Lightsail's own API reports.
    /// </summary>
    public const string FallbackSshUsername = "ec2-user";

    /// <summary>The port the produced <see cref="TargetDescriptor"/> endpoint names.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this provisioner
    /// produces — the <em>existing</em> SSH transport, not a Lightsail-specific one.
    /// </summary>
    /// <remarks>
    /// Kept as a constant here for the same layering reason as the other three adapters: this project cannot
    /// reference <c>Servyx.Infrastructure.Ssh</c>, so drift between this string and <c>SshTransport.TransportId</c>
    /// is caught by the composition test rather than by a runtime "no transport for id" failure.
    /// </remarks>
    internal const string SshTransportId = "ssh";

    /// <summary>
    /// The root path stamped on every descriptor this provisioner produces. Always <c>/</c>: shape I hands back
    /// a host, so there is no per-server data directory for it to record.
    /// </summary>
    public const string HostRootPath = "/";

    private const string PlanCostUnavailable =
        "No Lightsail bundle was named in the provisioning request, so no list price could be looked up.";

    private readonly LightsailJsonApiClient _api;
    private readonly string _region;
    private readonly string? _sshCredentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _addressPollInterval;
    private readonly int _addressPollAttempts;

    /// <summary>
    /// Creates a provisioner acting on one AWS region as the identity whose key pair is stored at
    /// <paramref name="identity"/>'s URNs.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every API call. Its base address is not used - the Lightsail endpoint is derived
    /// from <paramref name="region"/>, or supplied explicitly via <paramref name="endpoint"/>. Tests substitute
    /// its <see cref="HttpMessageHandler"/>, so no network access is required or attempted.
    /// </param>
    /// <param name="secretStore">Where the AWS key pair is resolved from, freshly, on every request.</param>
    /// <param name="identity">
    /// The URNs of the access key id and secret access key - the same <see cref="AwsSigningIdentity"/> type EC2
    /// uses, since both adapters authenticate as the same AWS account with the same algorithm.
    /// </param>
    /// <param name="region">The AWS region this provisioner acts on, e.g. <c>us-east-1</c>.</param>
    /// <param name="sshCredentialUrn">
    /// The <see cref="TargetDescriptor.CredentialUrn"/> to stamp on produced descriptors - the URN of the SSH
    /// private key matching the Lightsail key pair the instance boots with. Never a literal credential, and
    /// never the AWS secret access key.
    /// </param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/> the SSH transport reads. Applied before Servyx-owned
    /// option keys, so they can never override one.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry, request signing, and waiting on a new address.</param>
    /// <param name="addressPollInterval">How long to wait between instance-readiness polls. Defaults to five seconds.</param>
    /// <param name="addressPollAttempts">How many polls to make before giving up. Defaults to 60.</param>
    /// <param name="endpoint">Override for the regional Lightsail endpoint. Defaults to <c>https://lightsail.{region}.amazonaws.com/</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/> is blank.</exception>
    public AwsLightsailProvisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        string? sshCredentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        TimeProvider? timeProvider = null,
        TimeSpan? addressPollInterval = null,
        int addressPollAttempts = 60,
        Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentOutOfRangeException.ThrowIfLessThan(addressPollAttempts, 1);

        _region = region;
        _sshCredentialUrn = sshCredentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _addressPollInterval = addressPollInterval ?? TimeSpan.FromSeconds(5);
        _addressPollAttempts = addressPollAttempts;

        _api = new LightsailJsonApiClient(
            httpClient,
            new AwsRequestSigner(secretStore, identity, region, LightsailJsonApiClient.ServiceName, _timeProvider),
            region,
            endpoint);
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <summary>The AWS region this provisioner acts on. Fixed at construction; see the type remarks.</summary>
    public string Region => _region;

    /// <inheritdoc />
    /// <remarks>
    /// The same four bits as <c>AwsEc2Provisioner.Capabilities</c>, for the same reasons - see the type remarks
    /// for what each omission means for Lightsail specifically rather than restating EC2's version of the same
    /// argument.
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the instance spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Issues no HTTP request whatsoever.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(BuildPlan(BuildSpec(request)));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the
    /// spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    public ProvisioningPlan BuildPlan(AwsLightsailInstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var tags = ServyxLightsailTags.Validate(TagsFor(spec));

        var stages = new List<ProvisioningStage>
        {
            new(
                "create-instance",
                Id,
                $"Create one '{spec.Machine.SizeRef}' Lightsail instance named '{spec.InstanceName}' in "
                + $"availability zone '{spec.AvailabilityZone}' from blueprint '{spec.Machine.ImageRef}', with "
                + $"{tags.Count} Servyx tag(s) applied by the same call (Lightsail states it rolls the whole "
                + "create back if tagging fails, and there is only one taggable object here - the bundle's "
                + "storage is billed as part of the instance, not as a resource of its own), "
                + (spec.KeyPairName is null
                    ? "no Lightsail key pair named (the account's default key pair will be used), "
                    : $"Lightsail key pair '{spec.KeyPairName}', ")
                + (string.IsNullOrEmpty(spec.Machine.CloudInit)
                    ? "and no user-data (this provisioner never authors cloud-init; nothing is installed on the machine here)."
                    : $"and {spec.Machine.CloudInit.Length} character(s) of caller-supplied user-data, forwarded "
                      + "verbatim as plain text (Lightsail, unlike EC2, needs no base64 transcoding on the wire).")),
            new(
                "await-instance-ready",
                Id,
                "Poll the instance by name until Lightsail reports a public IPv4 address for it. No change is "
                + "made to the instance by this stage."),
            new(
                "handoff-ssh-target",
                Id,
                "Hand back an 'ssh://<username>@<address>:22' target descriptor for the new host, where "
                + "<username> is read from Lightsail's own report of the blueprint's default login account "
                + "rather than assumed from a constant. Provisioning stops here: installing a game server onto "
                + "this host is a separate stage run by the SSH host-install provisioner, identically to any "
                + "bare-metal SSH box."),
        };

        if (spec.Machine.Ingress.Count > 0)
        {
            stages.Add(new ProvisioningStage(
                "ingress-not-applied",
                Id,
                $"NOT APPLIED: {spec.Machine.Ingress.Count} inbound rule(s) were requested "
                + $"({string.Join(", ", spec.Machine.Ingress.Select(r => $"{r.Protocol}/{r.Port} from {r.SourceCidr ?? "any"}"))}), "
                + "but this provisioner does not implement FirewallRules and will not call PutInstancePublicPorts. "
                + "Unlike EC2's default-deny security group, Lightsail's blueprint-assigned default port "
                + "configuration typically already allows inbound SSH (port 22) and sometimes HTTP/HTTPS - so "
                + "'not applied' means no port beyond the blueprint's own defaults was opened, not that every "
                + "port is closed. Apply the requested rules separately."));
        }

        var planHash = ComputePlanHash(spec, tags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.InstanceName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: string.IsNullOrWhiteSpace(spec.Machine.SizeRef)
                ? CostEstimate.Unknown(PlanCostUnavailable + " " + AwsLightsailPricing.Source)
                : AwsLightsailPricing.For(spec.Machine.SizeRef),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the instance back from Lightsail by name - the registry-backed shape, so the answer reflects the
    /// instance as Lightsail currently describes it. An instance Lightsail no longer knows
    /// (<c>NotFoundException</c>), or one whose tags no longer identify it as Servyx-managed, yields
    /// <see langword="null"/>. Unlike EC2, there is no separate "gone but still reported for an hour" state to
    /// filter here - see <see cref="LightsailJsonApiClient.GetInstanceAsync"/>'s remarks for the honest limit on
    /// how firmly that could be confirmed.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The instance exists but reports no IPv4 address yet, so no SSH target can be described for it. This is a
    /// transient boot state, not a missing resource, and is deliberately distinguished from
    /// <see langword="null"/>.
    /// </exception>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId))
        {
            return null;
        }

        var instance = await _api.GetInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (instance is null)
        {
            return null;
        }

        var identity = ServyxLightsailTags.FromTags(instance.Tags);
        if (identity is null)
        {
            return null;
        }

        var (address, username) = RequireSshTarget(instance);

        return new ProvisionedResource(
            Handle: HandleFor(instance),
            ConnectorId: identity.ConnectorId,
            Target: BuildTargetDescriptor(username, address),
            Facts: BuildFacts(instance));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Asks Lightsail for every instance in the region carrying <c>servyx.managed=true</c>, independent of any
    /// Servyx-local record, following <c>nextPageToken</c> pagination to the end for the same reason the EC2
    /// sweep follows <c>nextToken</c>. See <see cref="LightsailJsonApiClient.GetInstancesByTagAsync"/>'s remarks
    /// for the one real cost of this shape: Lightsail's <c>GetInstances</c> has no server-side tag filter at
    /// all, so every instance in the region - tagged or not - crosses the wire on every sweep, unlike EC2's
    /// <c>Filter.1.Name=tag:...</c> parameter which narrows the response before it is ever sent.
    /// </para>
    /// <para>
    /// Only <see cref="OrphanScope.ProviderWide"/> is served, and a <see cref="OrphanScope.Region"/> naming a
    /// different region reports nothing - the same two rules EC2 enforces, for the same reason: this client can
    /// only reach one regional endpoint.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope is not OrphanScope.ProviderWide || !string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        if (scope.Region is not null && !string.Equals(scope.Region, _region, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var instances = await _api
            .GetInstancesByTagAsync(ServyxLightsailTags.ManagedTag, ServyxLightsailTags.ManagedTagValue, ct)
            .ConfigureAwait(false);

        return instances
            .Where(i => ServyxLightsailTags.IsManaged(i.Tags))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(HandleFor)
            .ToList();
    }

    /// <summary>
    /// Returns the mutating operation that creates the instance described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>, matching every other adapter in this
    /// codebase. Calling this method creates nothing on its own.
    /// </remarks>
    public IProvisioningOperation CreateOperation(AwsLightsailInstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new InstanceCreateOperation(this, spec);
    }

    /// <inheritdoc />
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys an instance this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <remarks>
    /// Unlike <c>AwsEc2Provisioner.DestroyAsync</c>, there is no dispatch on the handle's id shape: Lightsail's
    /// bundle price bakes the boot disk into the instance, so there is only ever one kind of resource this
    /// adapter creates and therefore only one kind to destroy.
    /// </remarks>
    /// <returns><see langword="true"/> if the instance was destroyed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return await _api.DeleteInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into an instance spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the Lightsail instance's name (its identity, not merely a label).</description></item>
    /// <item><description><c>image</c> — required, a blueprint id, e.g. <c>amazon_linux_2023</c>.</description></item>
    /// <item><description><c>size</c> — required, a bundle id, e.g. <c>medium_3_0</c>.</description></item>
    /// <item><description><c>availabilityZone</c> — optional; defaults to this provisioner's region with an "a" suffix.</description></item>
    /// <item><description><c>keyPair</c> — the name of a Lightsail key pair already registered in the account.</description></item>
    /// <item><description><c>sshPublicKey</c> — the operator's declared public key. Part of the plan hash but not sent; <c>CreateInstances</c> takes only a key-pair name.</description></item>
    /// <item><description><c>cloudInit</c> — user-data, forwarded verbatim as plain text. Nothing here authors one.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. Recorded and reported as NOT applied; see the type remarks.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// There is deliberately no <c>subnetId</c> or <c>securityGroupId:&lt;n&gt;</c> key at all, unlike EC2's:
    /// Lightsail has no VPC concept for a caller to name a member of. There is likewise no <c>region</c>,
    /// <c>credentialUrn</c> or <c>sshUsername</c> key: the first two are fixed at construction for the same
    /// reason EC2's are, and the third does not exist on this adapter at all - see the type remarks on why.
    /// </remarks>
    /// <param name="request">The request to translate.</param>
    /// <exception cref="ArgumentException">A required parameter is missing, the name is not a legal Lightsail resource name, or a value is not expressible as a Lightsail tag.</exception>
    public AwsLightsailInstanceSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxLightsailTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var ingress = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                ingress.Add(ParseIngress(pair.Key["ingress:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var name = Required(parameters, "name");

        var machine = new MachineSpec(
            ImageRef: Required(parameters, "image"),
            SizeRef: Required(parameters, "size"),
            Region: _region,
            SshPublicKey: parameters.TryGetValue("sshPublicKey", out var publicKey) ? publicKey : string.Empty,
            CloudInit: parameters.TryGetValue("cloudInit", out var cloudInit) && !string.IsNullOrEmpty(cloudInit)
                ? cloudInit
                : null,
            Ingress: ingress
                .OrderBy(r => r.Port)
                .ThenBy(r => r.Protocol, StringComparer.Ordinal)
                .ToList(),
            Tags: tags.ToTags(extraTags));

        return new AwsLightsailInstanceSpec(name, machine, tags)
        {
            AvailabilityZone = parameters.TryGetValue("availabilityZone", out var az) && !string.IsNullOrWhiteSpace(az)
                ? az
                : DefaultAvailabilityZone(_region),
            KeyPairName = parameters.TryGetValue("keyPair", out var keyPair) && !string.IsNullOrWhiteSpace(keyPair)
                ? keyPair
                : null,
            AdditionalTags = extraTags,
        };
    }

    /// <summary>
    /// The availability zone <see cref="BuildSpec"/> falls back to when a caller does not name one: the
    /// region with an <c>"a"</c> zone suffix, which is virtually always valid - every AWS/Lightsail region
    /// exposed by <c>GetRegions</c> has at least a zone <c>a</c>. A caller in the rare region where that does
    /// not hold must supply <c>availabilityZone</c> explicitly.
    /// </summary>
    internal static string DefaultAvailabilityZone(string region) =>
        string.Create(CultureInfo.InvariantCulture, $"{region}a");

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for an instance. This is the whole hand-off: the value returned
    /// here names the <em>existing</em> SSH transport, carries an ordinary <c>ssh://user@host:port</c> endpoint,
    /// and contains not one Lightsail-specific field.
    /// </summary>
    /// <param name="username">The login username, as read from Lightsail's own report - see the type remarks.</param>
    /// <param name="address">The address the endpoint names.</param>
    internal TargetDescriptor BuildTargetDescriptor(string username, string address)
    {
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            ["rootPath"] = HostRootPath,
        };

        return new TargetDescriptor(
            TransportId: SshTransportId,
            Endpoint: string.Create(CultureInfo.InvariantCulture, $"ssh://{username}@{address}:{SshPort}"),
            CredentialUrn: _sshCredentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>The full Servyx tag dictionary for a spec: caller extras first, canonical keys last.</summary>
    private static IReadOnlyDictionary<string, string> TagsFor(AwsLightsailInstanceSpec spec) =>
        spec.Tags.ToTags(spec.AdditionalTags);

    private ResourceHandle HandleFor(LightsailInstance instance) =>
        new(Id, instance.Name, _region, new Dictionary<string, string>(instance.Tags, StringComparer.Ordinal));

    private static ResourceFacts BuildFacts(LightsailInstance instance) =>
        new(
            PublicAddress: instance.PublicIpAddress,
            PrivateAddress: instance.PrivateIpAddress,
            Cost: AwsLightsailPricing.For(instance.BundleId),
            CreatedAt: instance.CreatedAt ?? DateTimeOffset.UnixEpoch);

    /// <summary>The address and username a descriptor names, requiring both before a target can be described.</summary>
    private static (string Address, string Username) RequireSshTarget(LightsailInstance instance)
    {
        var address = instance.PublicIpAddress
            ?? instance.PrivateIpAddress
            ?? throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Instance '{instance.Name}' exists (state '{instance.StateName}') but Lightsail reports no IPv4 address for it yet, so no SSH target can be described. This is a transient boot state, not a missing instance - the instance is billing and must not be treated as gone."));

        var username = string.IsNullOrWhiteSpace(instance.Username) ? FallbackSshUsername : instance.Username;

        return (address, username);
    }

    private string ComputePlanHash(AwsLightsailInstanceSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.InstanceName).Append('\n');
        builder.Append(spec.Machine.ImageRef).Append('\n');
        builder.Append(spec.Machine.SizeRef).Append('\n');
        builder.Append(_region).Append('\n');
        builder.Append(spec.AvailabilityZone).Append('\n');
        builder.Append(spec.Machine.SshPublicKey).Append('\n');
        builder.Append(spec.Machine.CloudInit ?? string.Empty).Append('\n');
        builder.Append(spec.KeyPairName ?? string.Empty).Append('\n');
        builder.Append(HostRootPath).Append('\n');

        foreach (var rule in spec.Machine.Ingress)
        {
            builder.Append(CultureInfo.InvariantCulture, $"ingress {rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}\n");
        }

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        foreach (var option in _transportOptions.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"option {option.Key}={option.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static FirewallRule ParseIngress(string portKey, string sourceCidr)
    {
        var slash = portKey.IndexOf('/', StringComparison.Ordinal);
        var portText = slash < 0 ? portKey : portKey[..slash];
        var protocol = slash < 0 ? "tcp" : portKey[(slash + 1)..];

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException(
                $"'{portKey}' is not a valid 'ingress:<number>[/<protocol>]' provisioning parameter key.",
                nameof(portKey));
        }

        return new FirewallRule(port, protocol.ToLowerInvariant(), string.IsNullOrWhiteSpace(sourceCidr) ? null : sourceCidr);
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"Provisioning parameter '{key}' is required by the '{Id}' provisioner.",
                nameof(parameters));
        }

        return value;
    }

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside the
    /// provisioner so it — and only it — can reach the API client the provisioner is configured with.
    /// </summary>
    private sealed class InstanceCreateOperation : IProvisioningOperation
    {
        private readonly AwsLightsailProvisioner _owner;
        private readonly AwsLightsailInstanceSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;

        internal InstanceCreateOperation(AwsLightsailProvisioner owner, AwsLightsailInstanceSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in order
            // to commit them to the write-ahead ledger - so they must be the same values that later reach the
            // provider, not a set recomputed later.
            _tags = ServyxLightsailTags.Validate(TagsFor(spec));
        }

        public string ProvisionerId => Id;

        public string? Region => _owner._region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Creates the instance, waits for it to have an address, and hands back an SSH target for it.
        /// </summary>
        /// <remarks>
        /// Tags are applied by the same call that creates the instance, so there is no window in which a
        /// billing instance exists that a sweep could not find. This method runs no command on the machine,
        /// uploads nothing to it, and never opens an SSH connection at all.
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var body = AwsLightsailRequests.CreateInstances(_spec, _tags);
            await _owner._api.CreateInstancesAsync(body, ct).ConfigureAwait(false);

            var instance = await WaitForReadyAsync(ct).ConfigureAwait(false);
            var (address, username) = RequireSshTarget(instance);

            return new ProvisionedResource(
                Handle: _owner.HandleFor(instance),
                ConnectorId: _spec.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(username, address),
                Facts: BuildFacts(instance));
        }

        /// <summary>
        /// Deletes the instance this operation was creating, by the name it was always going to use.
        /// </summary>
        /// <remarks>
        /// Unlike <c>AwsEc2Provisioner.InstanceLaunchOperation.CompensateAsync</c>, there is no fallback tag
        /// sweep here: EC2 needs one because its instance id is provider-generated and unknown until
        /// <c>RunInstances</c> succeeds, so a failure before that point leaves compensation with no id to act
        /// on. Lightsail's instance name is chosen by the caller before the request is ever sent, so
        /// compensation can always target it directly. A <c>DeleteInstance</c> against a name Lightsail never
        /// created simply answers <c>NotFoundException</c>, which
        /// <see cref="LightsailJsonApiClient.DeleteInstanceAsync"/> turns into a harmless <see langword="false"/>.
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default) =>
            await _owner._api.DeleteInstanceAsync(_spec.InstanceName, ct).ConfigureAwait(false);

        private async Task<LightsailInstance> WaitForReadyAsync(CancellationToken ct)
        {
            for (var attempt = 0; attempt < _owner._addressPollAttempts; attempt++)
            {
                var instance = await _owner._api.GetInstanceAsync(_spec.InstanceName, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Instance '{_spec.InstanceName}' was created but Lightsail no longer reports it. Servyx cannot describe a target for it; reconcile by tag before assuming nothing is billing."));

                if (instance.PublicIpAddress is not null || instance.PrivateIpAddress is not null)
                {
                    return instance;
                }

                await Task.Delay(_owner._addressPollInterval, _owner._timeProvider, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Instance '{_spec.InstanceName}' did not report an IPv4 address within {_owner._addressPollAttempts} poll(s). The instance exists and is billing; compensation will delete it."));
        }
    }
}
