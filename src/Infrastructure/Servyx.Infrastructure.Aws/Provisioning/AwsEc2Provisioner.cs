using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that launches AWS EC2 instances — and nothing else. The third implementation
/// of "shape I" in this codebase, and the first one whose authentication is an algorithm rather than a header.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The headline: SigV4 was the whole objection, and it is 400 lines of pure function.</strong> AWS was
/// previously deferred here on the grounds that Signature Version 4 "isn't reasonably hand-rolled". That
/// judgement does not survive contact with the specification. SigV4 is deterministic, fully documented, and —
/// decisively — AWS publishes test vectors for it, so an implementation is not merely believed correct, it is
/// <em>pinned</em> against the same expected signatures every AWS SDK is pinned against. See
/// <see cref="AwsSigV4"/>, whose four steps map one-to-one onto AWS's four documented tasks, and the test
/// suite, which asserts the exact published signature for AWS's own <c>get-vanilla</c> and
/// <c>get-vanilla-query-order-key-case</c> cases. What the exercise cost is stated in the report, not hidden:
/// it is the largest single piece of provider-specific machinery in any of the three cloud adapters, and it is
/// the only one where a one-character mistake produces an opaque 403 rather than a readable error.
/// </para>
/// <para>
/// <strong>Shape I produces a host, not a game server.</strong> Visible, as in the two existing cloud adapters,
/// in what this type does not contain: no SteamCMD invocation, no package manager, no archive extraction, no
/// game definition, no shell script, no cloud-init authoring — no install step of any kind. The adapter
/// launches a machine, waits for it to have an address, and hands back a <see cref="TargetDescriptor"/> whose
/// <see cref="TargetDescriptor.TransportId"/> is <c>"ssh"</c>. From there the existing SSH host-install adapter
/// (shape H) installs onto it exactly as it would onto a bare-metal box a human had racked.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request at all, and — the AWS
/// -specific form of that claim — resolves no credential and computes no signature. A plan is pure computation
/// over the request, including its cost figure, which is why <see cref="AwsEc2Pricing"/> is a static snapshot
/// rather than a Price List API call. There is no request to audit, and no way for a plan to spend money or to
/// touch the key pair.
/// </para>
/// <para>
/// <strong>Region is adapter state, not a provisioning parameter, and that is a real divergence.</strong>
/// DigitalOcean takes <c>region</c> as a request parameter; Azure takes <c>location</c> as one. EC2 cannot:
/// the region is in the endpoint hostname (<c>ec2.us-east-1.amazonaws.com</c>) <em>and</em> in the SigV4
/// credential scope, so changing it changes both the host and every signature. The consequences run through
/// the whole type — <see cref="BuildSpec"/> is an instance method where its two siblings are static, and
/// <see cref="ReconcileAsync"/>'s "provider-wide" sweep is really region-wide (see below). An account using
/// several regions needs one provisioner per region.
/// </para>
/// <para>
/// <strong>What <see cref="ReconcileAsync"/> does and does not clean up — stated precisely, because
/// overstating it would be the most expensive mistake in this file.</strong> The sweep asks EC2 for every
/// instance <em>and</em> every EBS volume in the region carrying <c>servyx.managed=true</c>, following
/// <c>nextToken</c> pagination to the end. It therefore does find, independently of any local record:
/// </para>
/// <list type="bullet">
/// <item><description>
/// running, pending and stopped instances — including one launched by a <c>RunInstances</c> call whose response
/// never reached Servyx, because <c>TagSpecification</c> applies the tags in the same call that creates the
/// instance and there is no window in which it exists untagged;
/// </description></item>
/// <item><description>
/// EBS volumes, attached or detached — the case that matters is a root volume whose AMI left
/// <c>DeleteOnTermination</c> off, which survives its instance, is attached to nothing afterwards, and bills
/// per GB-month forever. Nothing about the instance would find it, which is why volumes are swept separately.
/// </description></item>
/// </list>
/// <para>
/// It does <strong>not</strong> report, and the reasons differ:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Terminated and shutting-down instances.</strong> Deliberate, not a gap. EC2 keeps a terminated
/// instance visible to <c>DescribeInstances</c> for up to about an hour after it stops billing, so including
/// them would put already-dead machines on a delete list. "Gone" is a <em>state</em> at this provider, not a
/// 404, which is a distinction neither existing adapter had to make.
/// </description></item>
/// <item><description>
/// <strong>Anything outside the configured region.</strong> Structural: the client can only reach one regional
/// endpoint. An <see cref="OrphanScope.ProviderWide"/> handed to this adapter therefore means "everything in
/// <em>this</em> region", and a scope naming a different region reports nothing rather than silently sweeping
/// the wrong one. A caller reading <see cref="ProvisioningCapabilities.TagQuery"/> as an account-wide guarantee
/// would be wrong, and this is the sentence that says so.
/// </description></item>
/// <item><description>
/// <strong>Security groups.</strong> Not a gap either, because this adapter never creates one — it attaches
/// groups the caller names, or lets EC2 apply the VPC default. <c>RunInstances</c> cannot create a security
/// group; only an adapter that made a separate <c>CreateSecurityGroup</c> call first could orphan one, and
/// there is no such call in this assembly. The absence of <see cref="ProvisioningCapabilities.FirewallRules"/>
/// and the absence of that orphan are the same fact.
/// </description></item>
/// </list>
/// <para>
/// <strong>The residual risk, named.</strong> Nothing this adapter creates is untaggable, which is a materially
/// better position than the Azure adapter's (a resource group, a subnet and a managed OS disk are each
/// invisible to its sweep). The honest remainder is narrower and it is about <em>deletion</em>, not discovery:
/// this adapter sends no <c>BlockDeviceMapping</c>, so whether the root volume dies with the instance is
/// decided by the AMI's own <c>DeleteOnTermination</c> default. Setting it would require knowing the AMI's root
/// device name, which needs a <c>DescribeImages</c> call this adapter does not make. So a volume can outlive
/// its instance — and when it does, the sweep finds it, which is the whole point of tagging volumes at launch.
/// </para>
/// <para>
/// <strong>Host-key trust is unresolved for this adapter, as it is for the other two.</strong>
/// <c>docs/provisioning.md</c> §6 asserts a freshly created VM's host key is "captured at creation from the
/// provider API or console output and pinned". AWS is, like Azure and unlike DigitalOcean, capable of it:
/// <c>GetConsoleOutput</c> returns the base64 boot log, and stock Linux AMIs print the sshd host-key
/// fingerprints into it between <c>-----BEGIN SSH HOST KEY FINGERPRINTS-----</c> markers. What this adapter
/// does about it is nothing, on purpose: the log is eventually consistent (it is typically empty for the first
/// several minutes after launch), a hardened or non-cloud-init AMI may never print the markers, and parsing
/// free text into a security decision is a feature with a design of its own rather than a line in an adapter.
/// Until it is built, this adapter stamps no <c>trustPolicy</c>, invents no bypass, and passes the caller's
/// transport options through to the SSH transport's existing host-key mechanism unchanged. An unattended
/// launch-then-install pipeline will still stop at the SSH handshake unless the fingerprint was obtained out of
/// band and fed back through the <c>pinnedFingerprints</c> transport option this adapter forwards untouched.
/// </para>
/// <para>
/// <strong>The default login user is a per-AMI fact, not a constant, and getting it wrong fails silently.</strong>
/// DigitalOcean images all take <c>root</c>; Azure refuses <c>root</c> and this codebase uses
/// <c>azureuser</c>. AWS has no single answer: Amazon Linux uses <c>ec2-user</c>, Ubuntu AMIs use
/// <c>ubuntu</c>, Debian uses <c>admin</c>, and a community AMI may use anything. <see cref="DefaultSshUsername"/>
/// is <c>ec2-user</c> because that is right for the Amazon Linux family, and it is a constructor parameter
/// precisely because it is wrong for roughly half of the images a caller might name. A wrong value here does
/// not produce an error from EC2 — it produces an SSH authentication failure minutes later, at the install
/// stage, attributed to the wrong component.
/// </para>
/// <para>
/// <strong>Capabilities are what is implemented, not what EC2 offers.</strong> EC2 can resize instances
/// (<c>ModifyInstanceAttribute</c>), snapshot volumes, allocate Elastic IPs and manage security groups; this
/// adapter calls none of those, so <see cref="ProvisioningCapabilities.Resize"/>,
/// <see cref="ProvisioningCapabilities.Snapshot"/>, <see cref="ProvisioningCapabilities.StaticAddress"/> and
/// <see cref="ProvisioningCapabilities.FirewallRules"/> are all absent. In particular a
/// <see cref="MachineSpec.Ingress"/> rule is <em>described in the plan as not applied</em> rather than quietly
/// ignored, because a caller who believed a port had been opened when nothing had would expose a server it
/// thought was firewalled. Note the sharper edge here, as on Azure: a security group's default is to deny all
/// inbound traffic, so a game port a caller asked for is not merely un-opened, it is actively closed.
/// </para>
/// <para>
/// <strong>Maintenance is implemented, and planning is still read-only.</strong> <see cref="IMaintainer"/>
/// lives in <c>AwsEc2Provisioner.Maintenance.cs</c>: it detects drift against a recorded handle and produces an
/// <see cref="UpdatePlan"/>, issuing nothing but <c>DescribeInstances</c> reads — there is no
/// <c>ModifyInstanceAttribute</c>, no <c>StopInstances</c> and no <c>TerminateInstances</c> on any planning
/// path. Read that file's remarks for the one thing that makes EC2's answers different from every sibling
/// adapter's: this adapter sends no <c>BlockDeviceMapping</c>, so what a replacement costs a caller's data is
/// decided by a <c>DeleteOnTermination</c> flag Servyx never set and can only read back.
/// </para>
/// <para>
/// <strong>Exactly one of the operations that planning describes can also be carried out.</strong>
/// <see cref="IUpdateApplier"/> lives in <c>AwsEc2Provisioner.InstanceType.cs</c> and executes an approved
/// instance-type change — the one difference whose <see cref="DataImpact"/> is
/// <see cref="DataImpact.Preserved"/>, and the only one whose EC2 route keeps the instance and its EBS volumes.
/// It is three calls, not one: <c>StopInstances</c>, <c>ModifyInstanceAttribute</c>, <c>StartInstances</c>, each
/// polled to an observed conclusion. <strong>The image change is deliberately not executable by that path</strong>
/// — it is a terminate-and-launch whose impact is <see cref="DataImpact.Destroyed"/> or
/// <see cref="DataImpact.AtRisk"/>, and it is refused there without a single mutating request, exactly as the
/// droplet rebuild and the Azure VM replacement were before they got reviewed changes of their own.
/// </para>
/// </remarks>
public sealed partial class AwsEc2Provisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "aws-ec2";

    /// <summary>
    /// The username produced endpoints authenticate as when the caller names none.
    /// </summary>
    /// <remarks>
    /// Correct for the Amazon Linux family and wrong for Ubuntu (<c>ubuntu</c>) and Debian (<c>admin</c>) AMIs.
    /// See the type remarks: this is the one adapter default whose wrongness surfaces as somebody else's error.
    /// </remarks>
    public const string DefaultSshUsername = "ec2-user";

    /// <summary>The port the produced <see cref="TargetDescriptor"/> endpoint names.</summary>
    public const int SshPort = 22;

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this provisioner
    /// produces — the <em>existing</em> SSH transport, not an AWS-specific one.
    /// </summary>
    /// <remarks>
    /// Kept as a constant here (this project cannot reference <c>Servyx.Infrastructure.Ssh</c>: infrastructure
    /// projects reference <c>Servyx.Domain</c> and nothing else) and asserted equal to
    /// <c>SshTransport.TransportId</c> by the composition test, so drift is caught by a test rather than by a
    /// runtime "no transport for id" failure.
    /// </remarks>
    internal const string SshTransportId = "ssh";

    /// <summary>
    /// The root path stamped on every descriptor this provisioner produces.
    /// </summary>
    /// <remarks>
    /// Always <c>/</c>: shape I hands back a <em>host</em>, so there is no per-server data directory for it to
    /// record. A game's <c>dataDir</c> is chosen by the install stage that runs afterwards.
    /// </remarks>
    public const string HostRootPath = "/";

    private const string PlanCostUnavailable =
        "No EC2 instance type was named in the provisioning request, so no list price could be looked up.";

    private readonly Ec2QueryApiClient _api;
    private readonly string _region;
    private readonly string? _sshCredentialUrn;
    private readonly IReadOnlyDictionary<string, string> _transportOptions;
    private readonly string _sshUsername;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _addressPollInterval;
    private readonly int _addressPollAttempts;
    private readonly TimeSpan _statePollInterval;
    private readonly int _statePollAttempts;

    /// <summary>
    /// Creates a provisioner acting on one AWS region as the identity whose key pair is stored at
    /// <paramref name="identity"/>'s URNs.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every API call. Its base address is not used — the EC2 endpoint is derived from
    /// <paramref name="region"/>, or supplied explicitly via <paramref name="endpoint"/>. Tests substitute its
    /// <see cref="HttpMessageHandler"/>, so no network access is required or attempted.
    /// </param>
    /// <param name="secretStore">Where the AWS key pair is resolved from, freshly, on every request.</param>
    /// <param name="identity">The URNs of the access key id and secret access key. Only URNs are held.</param>
    /// <param name="region">The AWS region this provisioner acts on, e.g. <c>us-east-1</c>. See the type remarks.</param>
    /// <param name="sshCredentialUrn">
    /// The <see cref="TargetDescriptor.CredentialUrn"/> to stamp on produced descriptors — the URN of the SSH
    /// private key matching the EC2 key pair the instance boots with. Never a literal credential, and never the
    /// AWS secret access key.
    /// </param>
    /// <param name="transportOptions">
    /// Additional <see cref="TargetDescriptor.Options"/> the SSH transport reads (<c>usernameUrn</c>,
    /// <c>passphraseUrn</c>, <c>trustPolicy</c>, <c>pinnedFingerprints</c>, <c>declaredChannels</c>). Applied
    /// before Servyx-owned option keys, so they can never override one. This adapter adds nothing of its own to
    /// the host-key question — see the type remarks.
    /// </param>
    /// <param name="sshUsername">
    /// The username produced endpoints authenticate as. Fixed at construction rather than per-request because
    /// <see cref="RefreshAsync"/> must be able to rebuild an identical descriptor from the instance alone, and
    /// an instance does not record which user Servyx intends to log in as. See the type remarks on why the
    /// default is right for only some AMIs.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry, request signing, and waiting on a new address.</param>
    /// <param name="addressPollInterval">How long to wait between address polls. Defaults to five seconds.</param>
    /// <param name="addressPollAttempts">How many address polls to make before giving up. Defaults to 60.</param>
    /// <param name="endpoint">Override for the regional EC2 endpoint. Defaults to <c>https://ec2.{region}.amazonaws.com/</c>.</param>
    /// <param name="statePollInterval">
    /// How long to wait between reads while waiting for an instance to reach a lifecycle state during an
    /// approved instance-type change. Defaults to five seconds. See
    /// <c>AwsEc2Provisioner.InstanceType.cs</c>: a type change stops the instance, and the wait for
    /// <c>stopped</c> is what makes the subsequent attribute write legal rather than merely attempted.
    /// </param>
    /// <param name="statePollAttempts">
    /// How many state reads to make before giving up on a stop or a start. Defaults to 60, so the default
    /// wait is five minutes per step. Running out is never reported as a success.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="region"/> or <paramref name="sshUsername"/> is blank.</exception>
    public AwsEc2Provisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        string? sshCredentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string sshUsername = DefaultSshUsername,
        TimeProvider? timeProvider = null,
        TimeSpan? addressPollInterval = null,
        int addressPollAttempts = 60,
        Uri? endpoint = null,
        TimeSpan? statePollInterval = null,
        int statePollAttempts = 60)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshUsername);
        ArgumentOutOfRangeException.ThrowIfLessThan(addressPollAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(statePollAttempts, 1);

        _region = region;
        _sshCredentialUrn = sshCredentialUrn;
        _transportOptions = transportOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(transportOptions, StringComparer.Ordinal);
        _sshUsername = sshUsername;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _addressPollInterval = addressPollInterval ?? TimeSpan.FromSeconds(5);
        _addressPollAttempts = addressPollAttempts;
        _statePollInterval = statePollInterval ?? TimeSpan.FromSeconds(5);
        _statePollAttempts = statePollAttempts;

        _api = new Ec2QueryApiClient(
            httpClient,
            new AwsRequestSigner(secretStore, identity, region, Ec2QueryApiClient.ServiceName, _timeProvider),
            region,
            endpoint);
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <summary>The AWS region this provisioner acts on. Fixed at construction; see the type remarks.</summary>
    public string Region => _region;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: instances bill by the
    /// second and volumes by the GB-month, so an orphan that cannot be found by tag bills forever. It is the
    /// strong, registry-backed form of the capability — <c>RunInstances</c> applies every tag in the same call
    /// that creates the instance and its volumes, so there is no window in which a billing resource exists
    /// untagged. Read it together with the type remarks, which state the two things the sweep does not cover
    /// (other regions; terminated instances, deliberately).
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is present because <see cref="AwsEc2Pricing"/>
    /// carries real published list prices; it answers <see cref="CostEstimate.Unknown"/> for any instance type
    /// it does not know rather than approximating, and it says in its own <see cref="CostEstimate.Source"/>
    /// that the figure is compute-only.
    /// <para>
    /// <see cref="ProvisioningCapabilities.Resize"/>, <see cref="ProvisioningCapabilities.Snapshot"/>,
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> and
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> are all deliberately absent — see the type remarks.
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> deserves a word, since a launch here <em>does</em>
    /// request a public IPv4 address: the bit means the adapter can allocate and attach a static (Elastic)
    /// address as an operation on an existing resource, which it cannot. The address an instance gets here is
    /// ephemeral and changes across a stop/start cycle.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/>,
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> and
    /// <see cref="ProvisioningCapabilities.DetectDrift"/> are all present, and each names something
    /// <c>AwsEc2Provisioner.Maintenance.cs</c> actually plans.
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/> is backed by two operations: an instance-type change
    /// (a stop, a <c>ModifyInstanceAttribute</c> and a start, with every EBS volume still attached at the end)
    /// and a retag. <see cref="ProvisioningCapabilities.RecreateToUpdate"/> is backed by the image case, which
    /// EC2 can only reach by terminating this instance and launching another.
    /// <see cref="ProvisioningCapabilities.DetectDrift"/> compares a live instance against the handle Servyx
    /// recorded — including whether the machine still exists at all, which on EC2 is a state rather than a 404.
    /// </para>
    /// <para>
    /// <strong><see cref="ProvisioningCapabilities.Resize"/> stays absent even though a type change can now be
    /// executed.</strong> That is an understatement, and it is the safe direction of one:
    /// <see cref="IUpdateApplier"/> — implemented in <c>AwsEc2Provisioner.InstanceType.cs</c> — will carry out
    /// an approved instance-type change, so a caller establishes that ability by the type test the interface
    /// exists for, which is checkable, rather than by reading a flag. The flag describes a broader promise
    /// (resizing on request, including the disk and image changes this adapter refuses to issue at all) that
    /// this adapter still does not make. Claiming it would overstate what a caller can ask for; leaving it
    /// absent understates only what a caller can already discover — the same call the DigitalOcean adapter made
    /// when its resize landed.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost
        | ProvisioningCapabilities.UpdateInPlace
        | ProvisioningCapabilities.RecreateToUpdate
        | ProvisioningCapabilities.DetectDrift;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the instance spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Issues no HTTP request whatsoever, so it cannot create, change, or bill for
    /// anything — and cannot resolve the key pair, derive a signing key, or compute a signature.
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
    public ProvisioningPlan BuildPlan(AwsEc2InstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var instanceTags = ServyxEc2Tags.Validate(TagsFor(spec, ServyxEc2Tags.RoleInstance));
        var volumeTags = ServyxEc2Tags.Validate(TagsFor(spec, ServyxEc2Tags.RoleVolume));

        var stages = new List<ProvisioningStage>
        {
            new(
                "run-instance",
                Id,
                $"Launch one '{spec.Machine.SizeRef}' instance in region '{_region}' from image "
                + $"'{spec.Machine.ImageRef}', with {instanceTags.Count} Servyx tag(s) applied by the same call "
                + $"(and {volumeTags.Count} on every EBS volume the launch creates, so neither can exist untagged), "
                + (spec.KeyPairName is null
                    ? "no EC2 key pair named (SSH access will depend entirely on the image), "
                    : $"EC2 key pair '{spec.KeyPairName}', ")
                + (spec.SecurityGroupIds.Count == 0
                    ? "the VPC's default security group, "
                    : $"{spec.SecurityGroupIds.Count} caller-named security group(s), ")
                + (string.IsNullOrEmpty(spec.Machine.CloudInit)
                    ? "and no user-data (this provisioner never authors cloud-init; nothing is installed on the machine here)."
                    : $"and {spec.Machine.CloudInit.Length} character(s) of caller-supplied user-data, base64-encoded and forwarded verbatim.")),
        };

        if (spec.AssignPublicIp)
        {
            stages.Add(new ProvisioningStage(
                "assign-public-ipv4",
                Id,
                "Request an ephemeral public IPv4 address for the instance's primary network interface. BILLABLE "
                + "and separately metered: AWS charges $0.005/hour for every in-use public IPv4 address (about "
                + "$3.65/month) since 2024-02-01, which is NOT part of the instance-type price shown as this "
                + "plan's cost. The address is not static and will change across a stop/start."));
        }

        stages.Add(new ProvisioningStage(
            "await-public-address",
            Id,
            "Poll the instance until EC2 reports an IPv4 address for it. No change is made to the instance by "
            + "this stage."));

        stages.Add(new ProvisioningStage(
            "handoff-ssh-target",
            Id,
            $"Hand back an 'ssh://{_sshUsername}@<address>:{SshPort}' target descriptor for the new host. "
            + "Provisioning stops here: installing a game server onto this host is a separate stage run by the "
            + "SSH host-install provisioner, identically to any bare-metal SSH box."));

        if (spec.Machine.Ingress.Count > 0)
        {
            // Stated as a stage rather than silently skipped: this provisioner does not advertise
            // ProvisioningCapabilities.FirewallRules, and a caller who believed a port had been opened when
            // nothing had would expose a server it thought was firewalled - or, here, would wait for traffic
            // that a default-deny security group is silently dropping.
            stages.Add(new ProvisioningStage(
                "ingress-not-applied",
                Id,
                $"NOT APPLIED: {spec.Machine.Ingress.Count} inbound rule(s) were requested "
                + $"({string.Join(", ", spec.Machine.Ingress.Select(r => $"{r.Protocol}/{r.Port} from {r.SourceCidr ?? "any"}"))}), "
                + "but this provisioner does not implement FirewallRules and will neither create nor modify a "
                + "security group. A security group denies all inbound traffic by default, so these ports are "
                + "actively closed rather than merely un-opened. Apply them separately."));
        }

        var planHash = ComputePlanHash(spec, instanceTags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.InstanceName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: string.IsNullOrWhiteSpace(spec.Machine.SizeRef)
                ? CostEstimate.Unknown(PlanCostUnavailable + " " + AwsEc2Pricing.Source)
                : AwsEc2Pricing.For(spec.Machine.SizeRef),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the instance back from EC2 by id — the registry-backed shape, so the answer reflects the machine as
    /// EC2 currently describes it. An instance EC2 no longer knows (<c>InvalidInstanceID.NotFound</c>), or one
    /// whose tags no longer identify it as Servyx-managed, yields <see langword="null"/>.
    /// </para>
    /// <para>
    /// <strong>So does a terminated one, and that branch is the AWS-specific part.</strong> EC2 keeps a
    /// terminated instance visible to <c>DescribeInstances</c> for up to about an hour after it has stopped
    /// existing in every sense that matters, complete with its tags and its old addresses. Returning it would
    /// tell a caller a machine is provisioned when it is gone. Neither existing cloud adapter has this problem:
    /// a destroyed droplet 404s and a deleted ARM resource 404s. Here "gone" is a state, so it is checked as
    /// one — see <c>Ec2Instance.GoneStates</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The instance exists and is alive but reports no IPv4 address yet, so no SSH target can be described for
    /// it. This is a transient boot state, not a missing resource, and is deliberately distinguished from
    /// <see langword="null"/> — treating "still booting" as "gone" would let a caller conclude a billing
    /// instance had disappeared.
    /// </exception>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (string.IsNullOrWhiteSpace(handle.ProviderResourceId)
            || !handle.ProviderResourceId.StartsWith("i-", StringComparison.Ordinal))
        {
            return null;
        }

        var instance = await _api.DescribeInstanceAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (instance is null || instance.IsGone)
        {
            return null;
        }

        var identity = ServyxEc2Tags.FromTags(instance.Tags);
        if (identity is null)
        {
            return null;
        }

        return new ProvisionedResource(
            Handle: HandleFor(instance),
            ConnectorId: identity.ConnectorId,
            Target: BuildTargetDescriptor(RequireSshAddress(instance)),
            Facts: BuildFacts(instance));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive, and the reason this adapter may be trusted with billable resources at all.
    /// Asks EC2 for every instance and every EBS volume carrying <c>servyx.managed=true</c>, independent of any
    /// Servyx-local record, so a machine launched but never acknowledged can still be found.
    /// </para>
    /// <para>
    /// <strong>Pagination is followed to the end.</strong> EC2 returns an opaque <c>nextToken</c> in the
    /// response body rather than a ready-made next-page URL, so a sweep that ignores it silently reports
    /// "no orphans beyond page one" as "no orphans". That is exactly the failure the Azure report flagged as a
    /// real trap, and it is worse here because a large account genuinely does exceed a page.
    /// </para>
    /// <para>
    /// The tag filter is sent to the API <em>and</em> re-applied to every resource in the response — the same
    /// two-step the Docker, DigitalOcean and Azure sweeps perform for the same reason: the filter is the
    /// provider's promise, the second check is this process's own guarantee that nothing untagged is ever
    /// reported as Servyx-owned and subsequently destroyed. A sweep acting on a false positive terminates
    /// someone else's instance.
    /// </para>
    /// <para>
    /// <strong>Only <see cref="OrphanScope.ProviderWide"/> is served.</strong> The provider's own inventory is
    /// the search space, so a scope describing some other search space — an
    /// <see cref="OrphanScope.MarkerDirectory"/>, say — is declined exactly as a scope naming another
    /// provisioner is: no handles, and no API call. And <see cref="OrphanScope.Region"/>, when set, must name
    /// this provisioner's own region: EC2's endpoint is regional, so a sweep cannot reach another region, and
    /// answering a request for <c>eu-west-1</c> with <c>us-east-1</c>'s instances would hand a caller a delete
    /// list for the wrong continent.
    /// </para>
    /// <para>
    /// Handles come back instances-first, then volumes, each group ordered by id. That ordering is a teardown
    /// order: a volume attached to a live instance cannot be deleted until the instance is gone.
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
            .DescribeInstancesByTagAsync(ServyxEc2Tags.ManagedTag, ServyxEc2Tags.ManagedTagValue, ct)
            .ConfigureAwait(false);

        var volumes = await _api
            .DescribeVolumesByTagAsync(ServyxEc2Tags.ManagedTag, ServyxEc2Tags.ManagedTagValue, ct)
            .ConfigureAwait(false);

        var handles = new List<ResourceHandle>();

        handles.AddRange(instances
            .Where(i => ServyxEc2Tags.IsManaged(i.Tags) && !i.IsGone)
            .OrderBy(i => i.InstanceId, StringComparer.Ordinal)
            .Select(HandleFor));

        handles.AddRange(volumes
            .Where(v => ServyxEc2Tags.IsManaged(v.Tags))
            .OrderBy(v => v.VolumeId, StringComparer.Ordinal)
            .Select(HandleFor));

        return handles;
    }

    /// <summary>
    /// Returns the mutating operation that launches the instance described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is driven by
    /// <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering. Calling this
    /// method creates nothing on its own — and, critically for a provider that bills by the second, makes no
    /// billable API call and resolves no credential.
    /// </remarks>
    public IProvisioningOperation CreateOperation(AwsEc2InstanceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new InstanceLaunchOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above: builds the instance spec the same way
    /// <see cref="PlanAsync"/> does, via <see cref="BuildSpec"/>, so a plan preview and the operation that later
    /// realises it are always derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys a resource this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dispatches on the <c>servyx.role</c> tag, because <see cref="ReconcileAsync"/> reports two kinds of
    /// object and <see cref="ResourceHandle"/> carries no field saying which is which. An instance is
    /// terminated; a volume is deleted. A handle carrying neither role — or an id that is neither an
    /// <c>i-</c> nor a <c>vol-</c> — is refused rather than guessed at.
    /// </para>
    /// <para>
    /// Terminating an instance destroys its root volume with it <em>if</em> that volume's
    /// <c>DeleteOnTermination</c> flag is set, which is the AMI's decision and not this adapter's; see the type
    /// remarks. A volume that survives is exactly what the volume half of the sweep exists to find. Deleting a
    /// volume that is still attached is refused by EC2, which is why the sweep orders instances first.
    /// </para>
    /// </remarks>
    /// <param name="handle">The resource to destroy.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns><see langword="true"/> if the resource was destroyed; <see langword="false"/> if it was already gone.</returns>
    /// <exception cref="ArgumentException">The handle does not name an EC2 instance or an EBS volume.</exception>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var id = handle.ProviderResourceId;

        if (id.StartsWith("i-", StringComparison.Ordinal))
        {
            return await _api.TerminateInstanceAsync(id, ct).ConfigureAwait(false);
        }

        if (id.StartsWith("vol-", StringComparison.Ordinal))
        {
            return await _api.DeleteVolumeAsync(id, ct).ConfigureAwait(false);
        }

        throw new ArgumentException(
            $"'{id}' is neither an EC2 instance id ('i-...') nor an EBS volume id ('vol-...'), so the '{Id}' "
            + "provisioner cannot tell what it would be destroying and will not guess.",
            nameof(handle));
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into an instance spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the value of the instance's <c>Name</c> tag.</description></item>
    /// <item><description><c>image</c> — required, an AMI id, e.g. <c>ami-0abcdef1234567890</c>.</description></item>
    /// <item><description><c>size</c> — required, an EC2 instance type, e.g. <c>t3.medium</c>.</description></item>
    /// <item><description><c>keyPair</c> — the name of an EC2 key pair already registered in the account.</description></item>
    /// <item><description><c>subnetId</c> — the VPC subnet to launch into; omitted lets EC2 choose the default subnet.</description></item>
    /// <item><description><c>securityGroupId:&lt;n&gt;</c> — the id of an existing security group; <c>n</c> fixes the order.</description></item>
    /// <item><description><c>assignPublicIp</c> — <c>false</c> to suppress the (billable) public IPv4 address. Defaults to true.</description></item>
    /// <item><description><c>sshPublicKey</c> — the operator's declared public key. Part of the plan hash but not sent; <c>RunInstances</c> takes only a key-pair name (see <see cref="AwsEc2InstanceSpec"/>).</description></item>
    /// <item><description><c>cloudInit</c> — user-data, forwarded verbatim (base64-encoded on the wire). Nothing here authors one.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. Recorded and reported as NOT applied; see the type remarks.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string, matching every existing adapter, so no
    /// separator can collide with a value.
    /// <para>
    /// There is deliberately no <c>region</c> key. It is the one <see cref="MachineSpec"/> field this adapter
    /// cannot take from a request — see the type remarks — and accepting one that had to match the constructor's
    /// would be a parameter that can only ever be wrong. There is likewise no <c>credentialUrn</c>,
    /// <c>sshUsername</c> or <c>endpoint</c> key: all three are fixed at construction, because
    /// <see cref="RefreshAsync"/> must rebuild an identical descriptor from an instance that records none of
    /// them.
    /// </para>
    /// </remarks>
    /// <param name="request">The request to translate.</param>
    /// <exception cref="ArgumentException">A required parameter is missing, or a value is not expressible as an EC2 tag.</exception>
    public AwsEc2InstanceSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxEc2Tags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var securityGroups = new SortedDictionary<int, string>();
        var ingress = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("securityGroupId:", StringComparison.Ordinal))
            {
                securityGroups[ParseIndex(pair.Key, "securityGroupId:")] = pair.Value;
            }
            else if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
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

        return new AwsEc2InstanceSpec(name, machine, tags)
        {
            KeyPairName = parameters.TryGetValue("keyPair", out var keyPair) && !string.IsNullOrWhiteSpace(keyPair)
                ? keyPair
                : null,
            SubnetId = parameters.TryGetValue("subnetId", out var subnetId) && !string.IsNullOrWhiteSpace(subnetId)
                ? subnetId
                : null,
            SecurityGroupIds = securityGroups.Values.ToList(),
            AssignPublicIp = !parameters.TryGetValue("assignPublicIp", out var assign)
                || !string.Equals(assign, "false", StringComparison.OrdinalIgnoreCase),
            AdditionalTags = extraTags,
        };
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for an instance. This is the whole hand-off, and the whole
    /// shape claim: the value returned here names the <em>existing</em> SSH transport, carries an ordinary
    /// <c>ssh://user@host:port</c> endpoint, and contains not one AWS-specific field. Nothing downstream needs
    /// to know a cloud API — let alone a signing algorithm — was ever involved.
    /// </summary>
    /// <param name="address">The address the endpoint names.</param>
    internal TargetDescriptor BuildTargetDescriptor(string address)
    {
        // Caller-supplied options first, Servyx-owned keys last, so an option can never shadow one - the same
        // ordering rule ServyxTagKeys.Build applies to tags, and the same one every sibling adapter applies to
        // its own descriptor options.
        var options = new Dictionary<string, string>(_transportOptions, StringComparer.Ordinal)
        {
            ["rootPath"] = HostRootPath,
        };

        return new TargetDescriptor(
            TransportId: SshTransportId,
            Endpoint: string.Create(CultureInfo.InvariantCulture, $"ssh://{_sshUsername}@{address}:{SshPort}"),
            CredentialUrn: _sshCredentialUrn,
            DockerContext: null,
            Options: options);
    }

    /// <summary>The full Servyx tag dictionary for a spec: caller extras and the role first, canonical keys last.</summary>
    private static IReadOnlyDictionary<string, string> TagsFor(AwsEc2InstanceSpec spec, string role)
    {
        var additional = new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
        {
            [ServyxEc2Tags.RoleTag] = role,
            [ServyxEc2Tags.NameTag] = spec.InstanceName,
        };

        return spec.Tags.ToTags(additional);
    }

    private ResourceHandle HandleFor(Ec2Instance instance) =>
        new(Id, instance.InstanceId, _region, new Dictionary<string, string>(instance.Tags, StringComparer.Ordinal));

    private ResourceHandle HandleFor(Ec2Volume volume) =>
        new(Id, volume.VolumeId, _region, new Dictionary<string, string>(volume.Tags, StringComparer.Ordinal));

    private static ResourceFacts BuildFacts(Ec2Instance instance) =>
        new(
            PublicAddress: instance.PublicIpAddress,
            PrivateAddress: instance.PrivateIpAddress,
            Cost: AwsEc2Pricing.For(instance.InstanceType),
            CreatedAt: instance.LaunchTime ?? DateTimeOffset.UnixEpoch);

    /// <summary>The address a descriptor names: the public IPv4 if there is one, otherwise the private one.</summary>
    private static string RequireSshAddress(Ec2Instance instance) =>
        instance.PublicIpAddress
        ?? instance.PrivateIpAddress
        ?? throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Instance {instance.InstanceId} exists (state '{instance.State}') but EC2 reports no IPv4 address for it yet, so no SSH target can be described. This is a transient boot state, not a missing instance - the instance is billing and must not be treated as gone."));

    private string ComputePlanHash(AwsEc2InstanceSpec spec, IReadOnlyDictionary<string, string> instanceTags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.InstanceName).Append('\n');
        builder.Append(spec.Machine.ImageRef).Append('\n');
        builder.Append(spec.Machine.SizeRef).Append('\n');
        builder.Append(_region).Append('\n');
        builder.Append(spec.Machine.SshPublicKey).Append('\n');
        builder.Append(spec.Machine.CloudInit ?? string.Empty).Append('\n');
        builder.Append(spec.KeyPairName ?? string.Empty).Append('\n');
        builder.Append(spec.SubnetId ?? string.Empty).Append('\n');
        builder.Append(spec.AssignPublicIp ? "public-ip\n" : "no-public-ip\n");
        builder.Append(_sshUsername).Append('\n');
        builder.Append(HostRootPath).Append('\n');

        foreach (var group in spec.SecurityGroupIds)
        {
            builder.Append(CultureInfo.InvariantCulture, $"securityGroup {group}\n");
        }

        foreach (var rule in spec.Machine.Ingress)
        {
            builder.Append(CultureInfo.InvariantCulture, $"ingress {rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}\n");
        }

        foreach (var tag in instanceTags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        foreach (var option in _transportOptions.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"option {option.Key}={option.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static int ParseIndex(string key, string prefix)
    {
        if (!int.TryParse(key[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            throw new ArgumentException($"'{key}' does not carry a numeric index after '{prefix}'.", nameof(key));
        }

        return index;
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
    private sealed class InstanceLaunchOperation : IProvisioningOperation
    {
        private readonly AwsEc2Provisioner _owner;
        private readonly AwsEc2InstanceSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _instanceTags;
        private readonly IReadOnlyDictionary<string, string> _volumeTags;
        private string? _launchedInstanceId;

        internal InstanceLaunchOperation(AwsEc2Provisioner owner, AwsEc2InstanceSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in order
            // to commit them to the write-ahead ledger - so they must be the same values that later reach the
            // provider, not a set recomputed later.
            _instanceTags = ServyxEc2Tags.Validate(TagsFor(spec, ServyxEc2Tags.RoleInstance));
            _volumeTags = ServyxEc2Tags.Validate(TagsFor(spec, ServyxEc2Tags.RoleVolume));
        }

        public string ProvisionerId => Id;

        public string? Region => _owner._region;

        public IReadOnlyDictionary<string, string> Tags => _instanceTags;

        /// <summary>
        /// Launches the instance, waits for it to have an address, and hands back an SSH target for it.
        /// </summary>
        /// <remarks>
        /// Tags are applied by the same call that creates the instance and its volumes, so — unlike the
        /// marker-file shape, and unlike Azure's implicitly-created OS disk — there is no window in which a
        /// billing resource exists that a sweep could not find. Note what this method does <em>not</em> do: it
        /// runs no command on the machine, uploads nothing to it, and never opens an SSH connection at all. It
        /// does not know what game is going to be installed.
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var launched = await _owner._api
                .RunInstancesAsync(AwsEc2Requests.RunInstances(_spec, _instanceTags, _volumeTags), ct)
                .ConfigureAwait(false);

            _launchedInstanceId = launched.InstanceId;

            var instance = await WaitForAddressAsync(launched, ct).ConfigureAwait(false);

            return new ProvisionedResource(
                Handle: _owner.HandleFor(instance),
                ConnectorId: _spec.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(RequireSshAddress(instance)),
                Facts: BuildFacts(instance));
        }

        /// <summary>
        /// Terminates whatever instance this operation may have launched.
        /// </summary>
        /// <remarks>
        /// When the launch call never handed back an id, this does not assume nothing was created — it asks EC2
        /// by tag instead, mirroring the Docker and DigitalOcean operations' refusal to make the same
        /// assumption. For a per-second billed machine the difference is a machine that bills forever versus one
        /// that does not. Note what compensation deliberately does not do: it never deletes a volume. A volume
        /// left behind by a failed launch is attached to the instance being terminated, so EC2 would refuse the
        /// delete anyway, and a volume that survives termination is a sweep's business rather than a failed
        /// operation's.
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            if (_launchedInstanceId is { } instanceId)
            {
                await _owner._api.TerminateInstanceAsync(instanceId, ct).ConfigureAwait(false);
                return;
            }

            var instances = await _owner._api
                .DescribeInstancesByTagAsync(ServyxEc2Tags.ManagedTag, ServyxEc2Tags.ManagedTagValue, ct)
                .ConfigureAwait(false);

            foreach (var instance in instances)
            {
                if (!instance.IsGone
                    && ServyxEc2Tags.IsManaged(instance.Tags)
                    && instance.Tags.TryGetValue(ServyxEc2Tags.InstanceIdTag, out var servyxInstanceId)
                    && string.Equals(servyxInstanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
                {
                    await _owner._api.TerminateInstanceAsync(instance.InstanceId, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<Ec2Instance> WaitForAddressAsync(Ec2Instance launched, CancellationToken ct)
        {
            var latest = launched;

            for (var attempt = 0; attempt < _owner._addressPollAttempts; attempt++)
            {
                if (latest.PublicIpAddress is not null || latest.PrivateIpAddress is not null)
                {
                    return latest;
                }

                await Task.Delay(_owner._addressPollInterval, _owner._timeProvider, ct).ConfigureAwait(false);

                latest = await _owner._api.DescribeInstanceAsync(launched.InstanceId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Instance {launched.InstanceId} was launched but EC2 no longer reports it. Servyx cannot describe a target for it; reconcile by tag before assuming nothing is billing."));
            }

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Instance {launched.InstanceId} did not report an IPv4 address within {_owner._addressPollAttempts} poll(s). The instance exists and is billing; compensation will terminate it."));
        }
    }
}
