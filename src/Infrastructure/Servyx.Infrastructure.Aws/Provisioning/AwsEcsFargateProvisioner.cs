using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates AWS Fargate deployments on Amazon ECS — the second implementation
/// of "shape M" (a managed container service) in this codebase, and the second adapter that hands back a resource
/// <em>no transport can reach</em>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read <c>docs/provisioning.md</c> §11 and <c>AzureContainerInstanceProvisioner</c> before changing
/// anything here.</strong> This adapter exists because of the domain change §11.10 records: <c>ProvisionedResource</c>
/// carries a <see cref="ResourceReachability"/> rather than a <see cref="TargetDescriptor"/>, so an adapter whose
/// provider terminates in something nothing can address is able to say so in its return type instead of
/// fabricating a transport id or throwing from <c>CreateAsync</c>. Everything in that section applies here with a
/// different provider's names substituted, and the differences that are <em>not</em> mere renaming are what the
/// rest of these remarks are about.
/// </para>
/// <para>
/// <strong>A Servyx server maps to an ECS service, not to a task, and the reason is host failure.</strong> ECS
/// offers two ways to run a Fargate workload. <c>RunTask</c> launches one standalone task: when it stops — a host
/// retirement, a platform-version rollout, an out-of-memory kill, a crash — it is simply gone, and nothing brings
/// it back. <c>CreateService</c> registers a desired count with the ECS scheduler, which replaces the task
/// whenever it stops. A game server that must survive a host failure is not a one-shot task, and AWS retires
/// Fargate infrastructure underneath running tasks as ordinary maintenance, so a standalone task is not a
/// long-lived server that happens to lack a supervisor — it is a server with a scheduled death.
/// </para>
/// <para>
/// The mapping also settles identity. A task ARN changes every time the scheduler replaces the task, so a
/// <see cref="ResourceHandle.ProviderResourceId"/> naming one would go stale with nothing having gone wrong and
/// no way for Servyx to notice. A service ARN does not move. <see cref="AwsFargateServiceSpec.DesiredCount"/> is
/// fixed at one: a Servyx "server" is one server, and a desired count above one would be several servers sharing
/// one handle, one EFS volume and one set of save files.
/// </para>
/// <para>
/// <strong>What this adapter therefore is, stated plainly so nobody has to infer it.</strong> It registers a task
/// definition, creates a service, confirms a task actually reached <c>RUNNING</c>, sweeps by tag, and destroys.
/// It hands back a resource Servyx's control plane <em>cannot connect to</em>: no <c>IExecutionTarget</c>, no file
/// read, no command execution, no health probe over a transport. As with ACI, the workload is reachable only
/// through a game-specific control channel — RCON on a published port — which satisfies
/// <c>ControlCapability.ControlChannelWrite</c> and therefore the <c>Operate</c> tier and nothing above it. The
/// <c>Provision</c> tier requires <c>WriteComposeFile</c> and a Fargate task has no compose file, so that ceiling
/// is permanent and is not an implementation gap.
/// </para>
/// <para>
/// <strong>Why ECS Exec was not attempted, since it is the obvious next question.</strong> ECS does expose
/// <c>ExecuteCommand</c>, and it returns a session — but the session is an AWS Systems Manager one, described by
/// a <c>streamUrl</c>, a <c>tokenValue</c> and a <c>sessionId</c>. That stream is a WebSocket carrying SSM's own
/// binary message framing, which in practice is spoken by the <c>session-manager-plugin</c> binary rather than by
/// an HTTP client; it reports no exit code anywhere; it multiplexes one pseudo-terminal, so stdout and stderr are
/// indistinguishable; and it requires the SSM agent to be present in the operator's image and
/// <c>enableExecuteCommand</c> to have been set on the service. Even granting every one of those preconditions,
/// an <c>IExecutionTarget</c> over it would have to fabricate <c>CommandResult.ExitCode</c> on every call — which
/// is §11.2's finding exactly, reached independently against a different provider's exec surface. Mounting EFS
/// fixes the storage half of the problem and moves the exec half not at all; the two are independent axes.
/// </para>
/// <para>
/// <strong>Persistent storage is mandatory and is enforced by the type, not by validation.</strong>
/// <see cref="AwsFargateServiceSpec"/> takes an <see cref="EfsVolumeMount"/> as a required constructor argument,
/// so a spec describing a Fargate service with no durable volume cannot be built. The argument for that is
/// stronger than ACI's: an ACI container group loses its writable layer when Azure happens to restart it, while
/// an ECS service replaces its task — and destroys its ephemeral storage — as a matter of routine operation. See
/// <see cref="EfsVolumeMount"/> for what Fargate actually offers and why EFS is the only answer.
/// </para>
/// <para>
/// <strong>The EFS file system is the ACI storage-account problem again, with one improvement and one new
/// hazard.</strong> The improvement: EFS needs no credential. Authorisation is network reachability plus IAM, so
/// unlike <c>AzureFileShareMount</c> there is no <see cref="SecretUrn"/> on the mount, nothing resolved from
/// <see cref="ISecretStore"/> at create time, and no key in any request body — the whole class of leak ACI's mount
/// has to argue about does not arise. The new hazard: EFS requires a mount target in the task's availability zone
/// and an inbound NFS rule on the file system's security group, and Servyx creates, sees and validates none of
/// those. When they are missing every ECS call still succeeds and the task fails afterwards, which is why this
/// adapter's create path confirms by reading a task's own status and reports
/// <c>DescribeTasks</c>'s <c>stoppedReason</c> in the failure.
/// </para>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no HTTP request at all and resolves no
/// secret — there is no secret in this shape to resolve, and the AWS key pair itself is touched only by
/// <see cref="AwsRequestSigner"/>, per request, at the moment of signing. A plan is pure computation over the
/// request, including its cost figure, which is why <see cref="AwsFargatePricing"/> is a static snapshot.
/// </para>
/// <para>
/// <strong>Addressing is worse here than on ACI, and it is not smoothed over.</strong> An ACI container group has
/// a public IP that may change when the group restarts, and a <c>dnsNameLabel</c> that survives that. A Fargate
/// task's address is a property of its elastic network interface, so it changes every time the service replaces
/// the task — which is the service's entire job — and <c>DescribeTasks</c> does not report a public address at
/// all: obtaining one means calling <c>ec2:DescribeNetworkInterfaces</c>, a different AWS service this adapter
/// does not call. <see cref="ResourceFacts.PublicAddress"/> is therefore always <see langword="null"/> and
/// <see cref="ResourceFacts.PrivateAddress"/> carries the current task's private IPv4, with a plan stage saying
/// out loud that no stable address exists and that obtaining one means a load balancer or Cloud Map, neither of
/// which Servyx creates.
/// </para>
/// <para>
/// <strong>What <see cref="ReconcileAsync"/> finds, and what it structurally cannot.</strong> The sweep lists
/// every Fargate service in the configured cluster and keeps the ones carrying <c>servyx.managed=true</c>, having
/// asked for their tags explicitly. Because <c>CreateService</c> applies tags in the call that creates the
/// service, there is no window in which a billing service exists that this sweep could not find — within its
/// cluster. Four things are outside it, and no tagging discipline in this adapter can bring them in:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Services in any other cluster.</strong> ECS has no cross-cluster service listing;
/// <c>ListServices</c> takes a cluster and there is no tag filter on it. This adapter is configured with exactly
/// one cluster, so a service Servyx created through a differently-configured provisioner is invisible to this
/// one. That is the same narrowing every AWS adapter here has for region, one level finer. The Resource Groups
/// Tagging API <em>would</em> find them region-wide by tag; this adapter deliberately does not call it, so the
/// limit stands.
/// </description></item>
/// <item><description>
/// <strong>Task definition revisions.</strong> Every provision registers one, they are never deleted by anyone —
/// <c>DeregisterTaskDefinition</c> only marks a revision <c>INACTIVE</c> — and this adapter does not enumerate
/// them. This is real clutter and it is honestly the mildest orphan class in this codebase: a revision is
/// <strong>free</strong>, reserves nothing, runs nothing, and holds no data. It is named in the service's
/// <see cref="ServyxEcsTags.TaskDefinitionFamilyTag"/> so a sweep that finds the service can find the family.
/// </description></item>
/// <item><description>
/// <strong>The cluster.</strong> Never created by Servyx, therefore never tagged, therefore invisible. Also free
/// when it is running nothing, which puts it alongside the Azure VM adapter's resource group rather than
/// alongside ACI's storage account.
/// </description></item>
/// <item><description>
/// <strong>The EFS file system and its access point.</strong> This is §11.4's finding, unchanged and unchangeable
/// from inside the adapter. The file system is separately billable, holds the customer's save data, and
/// <em>must</em> outlive the service by design. Servyx does not create it, so Servyx does not tag it, so a sweep
/// — which enumerates resources <em>by</em> tag — cannot see it. The partial mitigation is real but bounded: the
/// service carries <see cref="ServyxEcsTags.FileSystemTag"/> and <see cref="ServyxEcsTags.AccessPointTag"/>, so
/// while the service exists a sweep can name the file system it depends on. Once the service is destroyed that
/// pointer is destroyed with it, and the file system carries on billing with nothing in AWS or in Servyx
/// attributing the charge.
/// </description></item>
/// </list>
/// <para>
/// <strong>Submission is never reported as success, at either end.</strong> <c>CreateService</c> answers
/// <c>200 OK</c> with the service already <c>ACTIVE</c> and its running count at zero, so the create path
/// confirms by reading a task's own <c>lastStatus</c> and fails — so the executor compensates — if none reaches
/// <c>RUNNING</c>. <c>DeleteService</c> answers <c>200 OK</c> with the service <c>DRAINING</c>, so
/// <see cref="DestroyAsync"/> polls until ECS reports <c>INACTIVE</c> and raises rather than returning
/// <see langword="true"/> if it never does. Both are the same rule applied to the two ends of a resource's life.
/// </para>
/// <para>
/// <strong>Nothing is created that this adapter cannot destroy.</strong> No cluster, no EFS file system, no mount
/// target, no security group, no IAM role, no log group, no load balancer. Every one of those is a precondition
/// the operator supplies, named in the plan as REQUIRES rather than created — so unlike the Azure VM adapter,
/// which upserts a resource group and then refuses to delete it, this adapter leaves behind exactly one class of
/// thing it made (task definition revisions) and that class is free.
/// </para>
/// </remarks>
public sealed class AwsEcsFargateProvisioner : IProvisioner
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "aws-ecs-fargate";

    /// <summary>
    /// Why no transport can reach a Fargate task. Stamped onto every
    /// <see cref="ResourceReachability.NoTransport"/> this adapter produces.
    /// </summary>
    /// <remarks>
    /// Written for the operator who is looking at a resource Servyx created and will not connect to, and whose
    /// first conclusion will otherwise be that something is broken. Nothing is broken; the provider has no daemon
    /// to connect to. The text names the three transports that exist, says why each is inapplicable, disposes of
    /// ECS Exec explicitly because it is the obvious counter-suggestion, and names the one thing that
    /// <em>does</em> work — because a reason that leaves the reader with no next step is barely better than a
    /// null.
    /// </remarks>
    public const string UnreachableReason =
        "An AWS Fargate task exposes no Docker Engine endpoint (there is no host and no daemon to reach), runs no "
        + "sshd, and is not the Servyx host, so none of Servyx's transports ('docker', 'ssh', 'local') can address "
        + "it. ECS Exec cannot close the gap either: it hands back an AWS Systems Manager session - a streamUrl, a "
        + "token and a session id - whose WebSocket carries SSM's own binary framing rather than a command "
        + "protocol, reports no exit code anywhere, multiplexes a single pseudo-terminal so stdout and stderr are "
        + "indistinguishable, and requires the SSM agent to be baked into the operator's image before it works at "
        + "all. Reach the workload through its game control channel (RCON on a published port) instead. That "
        + "satisfies ControlChannelWrite and therefore the Operate tier; the Provision tier needs a compose file, "
        + "which a Fargate task does not have, so it is permanently out of reach for this shape. Note also that "
        + "the address to point a control channel at is not stable: it belongs to the task, and the service "
        + "replaces the task as a matter of routine.";

    /// <summary>The ECS task lifecycle state this adapter accepts as confirmation that a workload started.</summary>
    internal const string RunningDesiredStatus = "RUNNING";

    /// <summary>The ECS task lifecycle state a failed start ends in, listed only on the failure path.</summary>
    internal const string StoppedDesiredStatus = "STOPPED";

    private readonly EcsJsonApiClient _api;
    private readonly string _region;
    private readonly string _cluster;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _pollAttempts;

    /// <summary>
    /// Creates a provisioner acting on one AWS region and one ECS cluster as the identity whose key pair is
    /// stored at <paramref name="identity"/>'s URNs.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for every API call. Its base address is not used — the ECS endpoint is derived from
    /// <paramref name="region"/>, or supplied explicitly via <paramref name="endpoint"/>. Tests substitute its
    /// <see cref="HttpMessageHandler"/>, so no network access is required or attempted.
    /// </param>
    /// <param name="secretStore">Where the AWS key pair is resolved from, freshly, on every request.</param>
    /// <param name="identity">
    /// The URNs of the access key id and secret access key — the same <see cref="AwsSigningIdentity"/> type the
    /// EC2 and Lightsail adapters use, since all three authenticate as the same AWS account with the same
    /// algorithm.
    /// </param>
    /// <param name="region">The AWS region this provisioner acts on, e.g. <c>us-east-1</c>.</param>
    /// <param name="cluster">
    /// The ECS cluster every service is created in and every sweep covers. <strong>Must already exist</strong>;
    /// this adapter creates no cluster. Adapter state rather than a request parameter for the same structural
    /// reason the region is: <c>ListServices</c> takes a cluster and there is no cross-cluster listing, so what a
    /// sweep can cover is fixed at construction and a caller can see it before running one.
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry, request signing, and readiness polls.</param>
    /// <param name="pollInterval">How long to wait between task-readiness and deletion polls. Defaults to five seconds.</param>
    /// <param name="pollAttempts">How many polls to make before giving up. Defaults to 60.</param>
    /// <param name="endpoint">Override for the regional ECS endpoint. Defaults to <c>https://ecs.{region}.amazonaws.com/</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="region"/> or <paramref name="cluster"/> is blank.</exception>
    public AwsEcsFargateProvisioner(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        string cluster,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        int pollAttempts = 60,
        Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentOutOfRangeException.ThrowIfLessThan(pollAttempts, 1);

        _region = region;
        _cluster = cluster;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _pollAttempts = pollAttempts;

        _api = new EcsJsonApiClient(
            httpClient,
            new AwsRequestSigner(secretStore, identity, region, EcsProtocol.ServiceName, _timeProvider),
            region,
            endpoint);
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <summary>The AWS region this provisioner acts on. Fixed at construction; see the type remarks.</summary>
    public string Region => _region;

    /// <summary>The ECS cluster this provisioner creates into and sweeps. Fixed at construction.</summary>
    public string Cluster => _cluster;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>The same four bits as the ACI adapter, and the absences carry the same weight.</strong>
    /// <see cref="ProvisioningCapabilities.Create"/> and <see cref="ProvisioningCapabilities.Destroy"/> are
    /// implemented outright, the second of them confirmed rather than submitted.
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: a service keeps a Fargate task
    /// running and therefore billing for every second it exists, so an orphan that cannot be found by tag bills
    /// indefinitely. It is the registry-backed form — the service's tags are written by the same call that creates
    /// it — but read it with the type remarks, which name the four things the sweep cannot reach.
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is present because Fargate's two meters can be priced
    /// exactly from published rates; the figure says in its own <see cref="CostEstimate.Source"/> that it is
    /// compute-only and not all-in.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.StaticAddress"/> is absent, and more emphatically than it is for ACI.
    /// There, an address may change when the group restarts. Here the address belongs to the task, and the
    /// service's whole purpose is to replace the task — so the address changes as a matter of ordinary operation,
    /// not as an exception. Claiming the bit would mislead an operator about the one thing they would build a
    /// connection string on.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.FirewallRules"/> is absent for a different reason than ACI's, and the
    /// difference is worth reading. ACI attaches no network security group to a public-IP container group and
    /// offers no source-address filter at all, so there is nothing to implement. A Fargate task in <c>awsvpc</c>
    /// mode genuinely does sit behind a security group with real source-address rules — the mechanism exists and
    /// works. What is absent is Servyx's hand on it: this adapter references a pre-existing security group and
    /// makes no <c>ec2</c> call to create or modify one. So a requested source CIDR is described in the plan as
    /// NOT APPLIED, and the plan says which of the two situations it is, because "there is no filter" and "there
    /// is a filter and it is not ours" lead an operator to different next steps.
    /// </para>
    /// <para>
    /// <see cref="ProvisioningCapabilities.Resize"/> and <see cref="ProvisioningCapabilities.Snapshot"/> are
    /// absent because neither is implemented. <see cref="ProvisioningCapabilities.UpdateInPlace"/>,
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> and
    /// <see cref="ProvisioningCapabilities.DetectDrift"/> are absent because this type does not implement
    /// <see cref="IMaintainer"/> at all — which is worth saying out loud, because ECS's <c>UpdateService</c> is
    /// exactly the shape an in-place update would use and its absence here is a decision rather than an oversight:
    /// the two update bits say update <em>planning</em> exists, and it does not here.
    /// </para>
    /// <para>
    /// <strong>None of these bits says anything about reachability, and none of them should be read as doing
    /// so.</strong> That question is answered by the <see cref="ResourceReachability.NoTransport"/> this adapter
    /// returns from every create and refresh.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.EstimatesCost;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the Fargate spec from <paramref name="request"/>'s parameters and describes the
    /// stages needed to realise it. Issues no HTTP request whatsoever, computes no signature, and resolves no
    /// secret. It therefore cannot create, change, or bill for anything.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(BuildPlan(BuildSpec(request)));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed the spec
    /// themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    /// <remarks>
    /// The stage list is where this shape's real character becomes visible to whoever approves the plan, and four
    /// of the stages exist purely to say something uncomfortable out loud: that the EFS file system is billed
    /// separately and is not Servyx's to clean up, that the task definition revision this creates will never be
    /// deleted by anything, that no stable address exists, and that what comes back at the end is a resource
    /// nothing in Servyx can connect to. Compressing any of them into "create the service" would make this look
    /// like the Docker adapter on screen while hiding exactly the differences a reviewer needs.
    /// </remarks>
    public ProvisioningPlan BuildPlan(AwsFargateServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var tags = ServyxEcsTags.Validate(TagsFor(spec));

        // Formatted invariantly up front so the stage descriptions below are plain string concatenation. An
        // interpolated string built by concatenation is no longer an interpolation handler expression, so it
        // cannot be handed to string.Create(IFormatProvider, ...) - the culture has to be applied here.
        var cpuText = spec.CpuUnits.ToString(CultureInfo.InvariantCulture);
        var memoryText = spec.MemoryMib.ToString(CultureInfo.InvariantCulture);
        var vcpuText = spec.Vcpu.ToString(CultureInfo.InvariantCulture);
        var memoryGbText = spec.MemoryGb.ToString(CultureInfo.InvariantCulture);
        var tagCountText = tags.Count.ToString(CultureInfo.InvariantCulture);
        var portCountText = spec.Ports.Count.ToString(CultureInfo.InvariantCulture);
        var subnetText = string.Join(", ", spec.SubnetIds);

        var stages = new List<ProvisioningStage>
        {
            new(
                "require-ecs-cluster",
                Id,
                $"REQUIRES (does not create): ECS cluster '{spec.ClusterName}' in region '{_region}'. This "
                + "adapter creates no cluster, so it can leave none behind; if the cluster is absent the create "
                + "below fails with ClusterNotFoundException before anything billable exists. An empty ECS "
                + "cluster is itself free."),
            new(
                "require-efs-file-system",
                Id,
                $"REQUIRES (does not create): EFS file system '{spec.Mount.FileSystemId}'"
                + (spec.Mount.AccessPointId is null
                    ? $", root directory '{spec.Mount.RootDirectory}'"
                    : $" via access point '{spec.Mount.AccessPointId}'")
                + $", mounted at '{spec.Mount.ContainerPath}' with transit encryption "
                + $"{EfsVolumeMount.TransitEncryption}. No credential is involved: EFS is authorised by network "
                + "reachability and IAM, so nothing is read from the secret store for this mount. ALSO REQUIRED "
                + "AND NOT CHECKED BY SERVYX: an EFS mount target in every availability zone the task may be "
                + $"placed in ({subnetText}), and a security group rule allowing inbound NFS (TCP 2049) from the "
                + "task. If either is missing every API call below still succeeds and the task then fails to "
                + "start. THE FILE SYSTEM IS BILLED SEPARATELY, has a lifetime independent of the service, holds "
                + "the save data, and is NEVER created, modified or destroyed by Servyx - including when the "
                + "service is destroyed."),
            new(
                "register-task-definition",
                Id,
                $"Register a new revision of task definition family '{spec.Family}' ({cpuText} CPU units = "
                + $"{vcpuText} vCPU, {memoryText} MiB = {memoryGbText} GB) running image '{spec.Image}' in network "
                + $"mode '{AwsFargateServiceSpec.NetworkMode}', tagged with {tagCountText} Servyx tag(s), with the "
                + "EFS volume above mounted into it. FREE and reserves nothing. NOTE: a task definition revision "
                + "is never deleted - DeregisterTaskDefinition only marks one INACTIVE - so every provision of "
                + "this server adds a revision to the family permanently. Servyx does not sweep them, because "
                + "they cost nothing and hold nothing."),
            new(
                "create-service",
                Id,
                $"Create ECS service '{spec.ServiceName}' in cluster '{spec.ClusterName}' at desired count "
                + $"{AwsFargateServiceSpec.DesiredCount.ToString(CultureInfo.InvariantCulture)}, launch type "
                + $"{AwsFargateServiceSpec.LaunchType}, platform version {AwsFargateServiceSpec.PlatformVersion}, "
                + $"on subnet(s) {subnetText} with "
                + (spec.SecurityGroupIds.Count == 0
                    ? "the VPC's default security group"
                    : "security group(s) " + string.Join(", ", spec.SecurityGroupIds))
                + $", assignPublicIp {(spec.AssignPublicIp ? "ENABLED" : "DISABLED")}, publishing "
                + $"{portCountText} port(s) ({DescribePorts(spec)}), tagged with {tagCountText} Servyx tag(s) "
                + "applied by the same call and propagated to every task. BILLABLE per second on vCPU and memory "
                + "for as long as a task runs - and the service exists to keep one running, replacing it "
                + "whenever it stops, so the meter runs indefinitely by design."),
            new(
                "await-running-task",
                Id,
                "Poll the service's tasks until one reports lastStatus RUNNING. No change is made to any resource "
                + "by this stage. It exists because CreateService answers 200 OK with the service already ACTIVE "
                + "and its running count at zero: an unpullable image, a missing EFS mount target or a security "
                + "group with no NFS rule all produce a service that ECS considers healthy and a task that never "
                + "starts. If no task reaches RUNNING, ECS's own stoppedReason is read and reported, and the "
                + "create is failed so it can be compensated."),
            new(
                "no-stable-address",
                Id,
                "NOT PROVIDED: a stable address. A Fargate task's address belongs to its elastic network "
                + "interface, so it changes every time the service replaces the task - which is the service's "
                + "purpose. DescribeTasks reports the task's private IPv4 only; the public IPv4, if assigned, "
                + "requires an ec2:DescribeNetworkInterfaces call this adapter does not make, and would be just "
                + "as ephemeral. Servyx will report the current task's private address as a fact and no public "
                + "address at all. Obtaining a stable endpoint means a load balancer or Cloud Map service "
                + "discovery, neither of which Servyx creates, and both of which bill separately."),
            new(
                "handoff-unreachable",
                Id,
                "Hand back the service WITH NO TRANSPORT TARGET. " + UnreachableReason),
        };

        var restricted = spec.Ports.Where(p => !string.IsNullOrWhiteSpace(p.SourceCidr)).ToList();
        if (restricted.Count > 0)
        {
            // Stated as a stage rather than silently dropped, exactly as the ACI and EC2 adapters state their
            // unapplied ingress. The direction of the error matters: the port IS declared and the restriction is
            // NOT applied by Servyx, so a caller who assumed otherwise is relying on a rule nobody wrote.
            stages.Add(new ProvisioningStage(
                "ingress-source-not-applied",
                Id,
                $"NOT APPLIED: {restricted.Count.ToString(CultureInfo.InvariantCulture)} port(s) named a source "
                + $"CIDR ({DescribeRestrictions(restricted)}). Unlike Azure Container Instances, where no "
                + "source-address filter exists at all, a Fargate task in awsvpc mode DOES sit behind a real "
                + "security group with real source rules - but this adapter does not implement FirewallRules and "
                + "makes no ec2 call, so it neither creates nor modifies one. The port is declared in the task "
                + "definition; whether it is reachable, and from where, is entirely decided by the pre-existing "
                + "security group named above. Apply the requested rules to that group outside Servyx."));
        }

        if (spec.LogGroup is null)
        {
            stages.Add(new ProvisioningStage(
                "no-log-configuration",
                Id,
                "NOT CONFIGURED: container logging. No logGroup was named, so the task definition carries no "
                + "logConfiguration and the container's stdout and stderr are DISCARDED. There is no host to read "
                + "them from afterwards and no 'docker logs' to run - a Fargate task's only diagnostic channel is "
                + "the one configured here. Name an existing CloudWatch Logs group (Servyx does not create one) "
                + "unless the workload is genuinely expected to be silent."));
        }

        var planHash = ComputePlanHash(spec, tags);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.ServiceName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: AwsFargatePricing.For(spec.CpuUnits, spec.MemoryMib),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Reads the service back from ECS by ARN — the registry-backed shape, so the answer reflects the service as
    /// ECS currently describes it. A handle that is not a service ARN, an ARN naming a cluster this provisioner
    /// was not configured with, a service ECS reports as <c>MISSING</c>, a service ECS reports as
    /// <c>INACTIVE</c> (which is a deleted service that ECS is still willing to describe), and a service whose
    /// tags no longer identify it as Servyx-managed all yield <see langword="null"/>.
    /// </para>
    /// <para>
    /// <strong>Up to four round trips, where the ACI adapter needs one.</strong> That is not inefficiency, it is
    /// the shape of the provider: a Fargate deployment is a service, a task definition revision and a task, and
    /// each is a separate object with a separate read. The service gives status and tags; the task list plus the
    /// task description give the current address; the task definition gives the CPU/memory reservation the cost
    /// figure is computed from. An ACI container group carries all four facts in one document.
    /// </para>
    /// <para>
    /// <strong>A service with no running task is not an error here.</strong> The service exists, it is billing
    /// nothing at that instant, and ECS is trying to start a replacement — that is a fact to report, not a
    /// missing resource. <see cref="ResourceFacts.PrivateAddress"/> is simply <see langword="null"/>, exactly as
    /// the ACI adapter treats a group with no address yet.
    /// </para>
    /// </remarks>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!TryReadServiceArn(handle.ProviderResourceId, out var cluster, out _)
            || !BelongsToThisCluster(cluster))
        {
            return null;
        }

        var service = await _api.DescribeServiceAsync(_cluster, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (service is null || service.IsInactive)
        {
            return null;
        }

        var identity = ServyxEcsTags.FromTags(service.Tags);
        if (identity is null)
        {
            return null;
        }

        var task = await CurrentTaskAsync(service, ct).ConfigureAwait(false);
        var cost = await PriceAsync(service, ct).ConfigureAwait(false);

        return new ProvisionedResource(
            Handle: HandleFor(service),
            ConnectorId: identity.ConnectorId,
            Reachability: new ResourceReachability.NoTransport(UnreachableReason),
            Facts: new ResourceFacts(
                PublicAddress: null,
                PrivateAddress: task?.PrivateIpv4Address,
                Cost: cost,
                CreatedAt: service.CreatedAt ?? DateTimeOffset.UnixEpoch));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive. Lists every Fargate service in the configured cluster, reads them back in
    /// batches with <c>include: ["TAGS"]</c>, and keeps the ones carrying <c>servyx.managed=true</c> —
    /// independent of any Servyx-local record. There is no server-side tag filter to send, so unlike the EC2
    /// adapter the whole narrowing is this process's own work, and the tag check is applied to every service in
    /// the response for the reason every adapter here gives: a sweep's output is a delete list, and acting on a
    /// false positive destroys someone else's workload.
    /// </para>
    /// <para>
    /// <strong>Returns only services.</strong> A task definition revision is not returned, and could not usefully
    /// be: it is free, it cannot be deleted, and a <see cref="ResourceHandle"/> naming one would put an
    /// undeletable object on a delete list. See the type remarks for the full list of what this sweep cannot
    /// reach, of which the EFS file system is the one that matters.
    /// </para>
    /// <para>
    /// <strong>An <c>INACTIVE</c> service is excluded and a <c>DRAINING</c> one is not.</strong> ECS keeps a
    /// deleted service describable for a while; reporting one as an orphan would put a resource that no longer
    /// exists on a delete list. A draining service, by contrast, still exists and may still have a task running,
    /// so it belongs in the answer.
    /// </para>
    /// <para>
    /// <strong>Only <see cref="OrphanScope.ProviderWide"/> is served</strong>, and a scope naming another
    /// provisioner, another region, or another search-space shape is declined with no handles and no API call.
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

        var arns = await _api.ListFargateServiceArnsAsync(_cluster, ct).ConfigureAwait(false);
        if (arns.Count == 0)
        {
            return [];
        }

        var services = await _api.DescribeServicesAsync(_cluster, arns, ct).ConfigureAwait(false);

        return services
            .Where(s => !s.IsInactive && ServyxEcsTags.IsManaged(s.Tags))
            .OrderBy(s => s.ServiceArn, StringComparer.Ordinal)
            .Select(HandleFor)
            .ToList();
    }

    /// <summary>
    /// Returns the mutating operation that creates the Fargate deployment described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Calling this creates nothing on its own, makes no API call, and computes no signature.
    /// </remarks>
    public IProvisioningOperation CreateOperation(AwsFargateServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new FargateServiceCreateOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above, via the same <see cref="BuildSpec"/>
    /// <see cref="PlanAsync"/> uses, so a plan preview and the operation that later realises it are always
    /// derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently destroys a service this provisioner created, and confirms it before saying so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>DeleteService</c> — with <c>force: true</c>, which is required rather than permissive; see
    /// <see cref="EcsJsonApiClient.DeleteServiceAsync"/> for exactly what that flag authorises and why the
    /// two-call alternative buys nothing. Then a poll, because <c>DeleteService</c> answers <c>200 OK</c> with
    /// the service <c>DRAINING</c> and its task still running: returning <see langword="true"/> on that response
    /// would report a submission as a completion, which is the one thing this adapter must never do at either end
    /// of a resource's life.
    /// </para>
    /// <para>
    /// <strong>The mounted EFS file system, its access point and its mount targets are never touched.</strong>
    /// Not as a safety margin — as the point. They hold the customer's save data, they were not created by
    /// Servyx, and destroying a workload must never destroy the data it wrote (see the remarks on
    /// <see cref="ProvisioningCapabilities.Destroy"/>). The cost of that refusal is stated plainly on this type:
    /// the file system carries on billing afterwards and no sweep will attribute it.
    /// </para>
    /// <para>
    /// <strong>The task definition revision is also never touched</strong>, for the opposite reason: it is free,
    /// deregistering it would not delete it, and a task still draining from it would be left referencing an
    /// <c>INACTIVE</c> revision for no gain.
    /// </para>
    /// <para>
    /// <strong>A handle this adapter could not have created deletes nothing.</strong> An id that is not an ECS
    /// service ARN, or one naming a different cluster, is refused without an API call.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> if ECS confirmed the service reached <c>INACTIVE</c>; <see langword="false"/> if it
    /// was already gone, or if the handle does not name a service in this provisioner's cluster.
    /// </returns>
    /// <exception cref="AwsApiException">
    /// The delete was accepted but ECS did not report the service as <c>INACTIVE</c> within the configured number
    /// of polls. The service still exists and may still be running a billing task.
    /// </exception>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!TryReadServiceArn(handle.ProviderResourceId, out var cluster, out _)
            || !BelongsToThisCluster(cluster))
        {
            return false;
        }

        var deleted = await _api.DeleteServiceAsync(_cluster, handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (deleted is null)
        {
            return false;
        }

        if (deleted.IsInactive)
        {
            return true;
        }

        for (var attempt = 0; attempt < _pollAttempts; attempt++)
        {
            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);

            var service = await _api.DescribeServiceAsync(_cluster, handle.ProviderResourceId, ct).ConfigureAwait(false);
            if (service is null || service.IsInactive)
            {
                return true;
            }
        }

        throw new AwsApiException(
            HttpStatusCode.Accepted,
            null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"ECS accepted the DeleteService call for '{handle.ProviderResourceId}' but did not report the service as {EcsService.InactiveStatus} within {_pollAttempts} poll(s). The service still exists and may still be running a Fargate task, which bills per second; it is still Servyx-tagged, so a reconcile of cluster '{_cluster}' will find it again."));
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into a Fargate spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys:
    /// <list type="bullet">
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx tags.</description></item>
    /// <item><description><c>name</c> — required, the ECS service's name and the default task definition family.</description></item>
    /// <item><description><c>image</c> — required, an OCI image reference. Not an AMI id: a Fargate task runs a container, not a machine image.</description></item>
    /// <item><description><c>fileSystemId</c>, <c>mountPath</c> — <strong>both required</strong>. There is no way to ask this adapter for a Fargate service with no durable volume, because a service destroys its task's storage every time it replaces it.</description></item>
    /// <item><description><c>subnetId:&lt;n&gt;</c> — <strong>at least one required</strong>, ordered by index. Fargate supports only <c>awsvpc</c>, which has no default subnet.</description></item>
    /// <item><description><c>securityGroupId:&lt;n&gt;</c> — optional, ordered by index. Referenced only; never created or modified.</description></item>
    /// <item><description><c>efsRootDirectory</c>, <c>efsAccessPointId</c>, <c>mountReadOnly</c> — override the mount's defaults.</description></item>
    /// <item><description><c>cpu</c>, <c>memory</c> — the two per-second billing meters, in ECS CPU units and MiB. Validated against <see cref="AwsFargateSizing"/>'s matrix here, so an impossible pair fails at plan time rather than at RegisterTaskDefinition.</description></item>
    /// <item><description><c>containerName</c>, <c>family</c>, <c>executionRoleArn</c>, <c>taskRoleArn</c>, <c>logGroup</c>, <c>assignPublicIp</c> — override the defaults.</description></item>
    /// <item><description><c>ingress:&lt;port&gt;/&lt;protocol&gt;</c> — value is the source CIDR, or empty for any. The port is declared; a CIDR is reported as NOT applied.</description></item>
    /// <item><description><c>env:&lt;name&gt;</c> — a plain environment variable. Never a credential; see <see cref="AwsFargateServiceSpec.Environment"/>.</description></item>
    /// <item><description><c>tag:&lt;key&gt;</c> — an extra Servyx tag; can never shadow a mandatory one.</description></item>
    /// </list>
    /// <para>
    /// There is deliberately no <c>region</c> or <c>cluster</c> key: both are fixed at construction, the region
    /// for the same reason the EC2 and Lightsail adapters fix theirs (it names the endpoint and the credential
    /// scope) and the cluster because it is what a sweep can cover, which a caller must be able to see before
    /// running one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">A required parameter is missing, or a value is not usable.</exception>
    public AwsFargateServiceSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxEcsTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var extraTags = new Dictionary<string, string>(StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var ports = new List<FirewallRule>();

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("ingress:", StringComparison.Ordinal))
            {
                ports.Add(ParseIngress(pair.Key["ingress:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("env:", StringComparison.Ordinal))
            {
                environment[pair.Key["env:".Length..]] = pair.Value;
            }
            else if (pair.Key.StartsWith("tag:", StringComparison.Ordinal))
            {
                extraTags[pair.Key["tag:".Length..]] = pair.Value;
            }
        }

        var mount = new EfsVolumeMount(
            Required(parameters, "fileSystemId"),
            Required(parameters, "mountPath"),
            Optional(parameters, "efsRootDirectory") ?? "/",
            Optional(parameters, "efsAccessPointId"),
            ParseBool(parameters, "mountReadOnly"));

        var subnets = Indexed(parameters, "subnetId");
        if (subnets.Count == 0)
        {
            throw new ArgumentException(
                $"At least one 'subnetId:<n>' provisioning parameter is required by the '{Id}' provisioner. "
                + "Fargate supports only the 'awsvpc' network mode, which places an elastic network interface "
                + "for every task and has no default subnet.",
                nameof(request));
        }

        var spec = new AwsFargateServiceSpec(
            Required(parameters, "name"),
            _cluster,
            Required(parameters, "image"),
            mount,
            subnets,
            tags);

        var sizing = AwsFargateSizing.Require(
            ParseInt(parameters, "cpu") ?? spec.CpuUnits,
            ParseInt(parameters, "memory") ?? spec.MemoryMib,
            nameof(request));

        return spec with
        {
            ContainerName = Optional(parameters, "containerName") ?? spec.ContainerName,
            Family = Optional(parameters, "family") ?? spec.Family,
            CpuUnits = sizing.CpuUnits,
            MemoryMib = sizing.MemoryMib,
            SecurityGroupIds = Indexed(parameters, "securityGroupId"),
            AssignPublicIp = Optional(parameters, "assignPublicIp") is { } assign
                ? bool.TryParse(assign, out var enabled) && enabled
                : spec.AssignPublicIp,
            ExecutionRoleArn = Optional(parameters, "executionRoleArn"),
            TaskRoleArn = Optional(parameters, "taskRoleArn"),
            LogGroup = Optional(parameters, "logGroup"),
            Ports = ports
                .OrderBy(p => p.Port)
                .ThenBy(p => p.Protocol, StringComparer.Ordinal)
                .ToList(),
            Environment = environment,
            AdditionalTags = extraTags,
        };
    }

    /// <summary>
    /// Reads the cluster and service name out of an ECS service ARN.
    /// </summary>
    /// <remarks>
    /// A modern service ARN is <c>arn:aws:ecs:{region}:{account}:service/{cluster}/{service}</c>. The pre-2021
    /// short format omitted the cluster segment; an ARN in that shape is accepted with a <see langword="null"/>
    /// cluster, which <see cref="BelongsToThisCluster"/> then treats as "this provisioner's cluster" — the only
    /// cluster it could have come from, since a provisioner only ever writes to one.
    /// </remarks>
    /// <param name="arn">The candidate ARN.</param>
    /// <param name="cluster">The cluster the ARN names, or <see langword="null"/> for a short-format ARN.</param>
    /// <param name="serviceName">The service name the ARN names.</param>
    /// <returns><see langword="true"/> if <paramref name="arn"/> is an ECS service ARN.</returns>
    internal static bool TryReadServiceArn(
        string? arn,
        out string? cluster,
        [NotNullWhen(true)] out string? serviceName)
    {
        cluster = null;
        serviceName = null;

        if (string.IsNullOrWhiteSpace(arn))
        {
            return false;
        }

        var parts = arn.Split(':');
        if (parts.Length < 6
            || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[2], "ecs", StringComparison.Ordinal))
        {
            return false;
        }

        var resource = string.Join(':', parts[5..]).Split('/');
        if (resource.Length < 2 || !string.Equals(resource[0], "service", StringComparison.Ordinal))
        {
            return false;
        }

        if (resource.Length == 2)
        {
            serviceName = resource[1];
            return serviceName.Length > 0;
        }

        cluster = resource[1];
        serviceName = resource[2];
        return serviceName.Length > 0;
    }

    /// <summary>Whether an ARN's cluster segment names the cluster this provisioner acts on.</summary>
    private bool BelongsToThisCluster(string? cluster) =>
        cluster is null || string.Equals(cluster, _cluster, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full Servyx tag dictionary for a spec: caller extras and this adapter's bookkeeping keys first,
    /// canonical keys last, so an extra can never shadow <c>servyx.managed</c> or an identity key.
    /// </summary>
    /// <remarks>
    /// The role recorded here is the service's; <see cref="AwsEcsRequests"/> substitutes the task-definition role
    /// into the other body. The cluster, family and EFS names are recorded for the reason given on
    /// <see cref="ServyxEcsTags"/>: while the service exists, a sweep that finds it can name everything it
    /// depends on. This does not make any of those things sweepable and is not claimed to.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> TagsFor(AwsFargateServiceSpec spec)
    {
        var extras = new Dictionary<string, string>(spec.AdditionalTags, StringComparer.Ordinal)
        {
            [ServyxEcsTags.RoleTag] = ServyxEcsTags.RoleService,
            [ServyxEcsTags.ClusterTag] = spec.ClusterName,
            [ServyxEcsTags.TaskDefinitionFamilyTag] = spec.Family,
            [ServyxEcsTags.FileSystemTag] = spec.Mount.FileSystemId,
        };

        if (spec.Mount.AccessPointId is { Length: > 0 } accessPoint)
        {
            extras[ServyxEcsTags.AccessPointTag] = accessPoint;
        }

        return spec.Tags.ToTags(extras);
    }

    private ResourceHandle HandleFor(EcsService service) =>
        new(Id, service.ServiceArn, _region, new Dictionary<string, string>(service.Tags, StringComparer.Ordinal));

    /// <summary>The service's currently running task, or <see langword="null"/> if it has none right now.</summary>
    private async Task<EcsTask?> CurrentTaskAsync(EcsService service, CancellationToken ct)
    {
        if (service.ServiceName is not { Length: > 0 } name)
        {
            return null;
        }

        var arns = await _api.ListTaskArnsAsync(_cluster, name, RunningDesiredStatus, ct).ConfigureAwait(false);
        if (arns.Count == 0)
        {
            return null;
        }

        var tasks = await _api.DescribeTasksAsync(_cluster, arns, ct).ConfigureAwait(false);
        return tasks.FirstOrDefault(t => t.IsRunning);
    }

    /// <summary>
    /// Prices a live service from the CPU/memory reservation of the revision it currently launches.
    /// </summary>
    /// <remarks>
    /// A separate read, because a service does not carry its own reservation — the task definition does. A
    /// revision ECS no longer has is answered as <see cref="CostEstimate.Unknown"/> rather than as free.
    /// </remarks>
    private async Task<CostEstimate> PriceAsync(EcsService service, CancellationToken ct)
    {
        if (service.TaskDefinition is not { Length: > 0 } taskDefinition)
        {
            return CostEstimate.Unknown(
                "ECS reported no task definition for the service, so its CPU and memory reservation - the two "
                + "meters Fargate bills - are unknown. " + AwsFargatePricing.Source);
        }

        var definition = await _api.DescribeTaskDefinitionAsync(taskDefinition, ct).ConfigureAwait(false);

        return definition is null
            ? CostEstimate.Unknown(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Task definition '{taskDefinition}' could not be read back, so the service's CPU and memory reservation are unknown. ")
                + AwsFargatePricing.Source)
            : AwsFargatePricing.For(definition.Cpu, definition.Memory);
    }

    private static string DescribePorts(AwsFargateServiceSpec spec) =>
        spec.Ports.Count == 0
            ? "none"
            : string.Join(", ", spec.Ports.Select(p => Describe(p, includeSource: false)));

    private static string DescribeRestrictions(IEnumerable<FirewallRule> rules) =>
        string.Join(", ", rules.Select(r => Describe(r, includeSource: true)));

    private static string Describe(FirewallRule rule, bool includeSource) =>
        includeSource
            ? string.Create(CultureInfo.InvariantCulture, $"{rule.Protocol}/{rule.Port} from {rule.SourceCidr ?? "any"}")
            : string.Create(CultureInfo.InvariantCulture, $"{rule.Protocol}/{rule.Port}");

    /// <summary>
    /// The plan hash. Covers everything that reaches either request body, so a plan and the deployment it
    /// describes cannot drift apart silently.
    /// </summary>
    private string ComputePlanHash(AwsFargateServiceSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(_region).Append('\n');
        builder.Append(spec.ClusterName).Append('\n');
        builder.Append(spec.ServiceName).Append('\n');
        builder.Append(spec.Family).Append('\n');
        builder.Append(spec.ContainerName).Append('\n');
        builder.Append(spec.Image).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{spec.CpuUnits}\n");
        builder.Append(CultureInfo.InvariantCulture, $"{spec.MemoryMib}\n");
        builder.Append(spec.Mount.FileSystemId).Append('\n');
        builder.Append(spec.Mount.RootDirectory).Append('\n');
        builder.Append(spec.Mount.AccessPointId ?? string.Empty).Append('\n');
        builder.Append(spec.Mount.ContainerPath).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{spec.Mount.ReadOnly}\n");
        builder.Append(spec.ExecutionRoleArn ?? string.Empty).Append('\n');
        builder.Append(spec.TaskRoleArn ?? string.Empty).Append('\n');
        builder.Append(spec.LogGroup ?? string.Empty).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{spec.AssignPublicIp}\n");

        foreach (var subnet in spec.SubnetIds)
        {
            builder.Append("subnet ").Append(subnet).Append('\n');
        }

        foreach (var group in spec.SecurityGroupIds)
        {
            builder.Append("sg ").Append(group).Append('\n');
        }

        foreach (var port in spec.Ports)
        {
            builder.Append(CultureInfo.InvariantCulture, $"port {port.Protocol}/{port.Port} from {port.SourceCidr ?? "any"}\n");
        }

        foreach (var variable in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {variable.Key}={variable.Value}\n");
        }

        foreach (var tag in tags.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"tag {tag.Key}={tag.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Collects <c>prefix:&lt;n&gt;</c> parameters into a list ordered by their numeric index.</summary>
    /// <remarks>
    /// Ordered numerically rather than by ordinal string comparison, so <c>subnetId:10</c> follows
    /// <c>subnetId:9</c> instead of preceding it. The order matters: it reaches the request body and the plan
    /// hash, and two identical deployments whose subnet lists sorted differently would hash differently.
    /// </remarks>
    private static IReadOnlyList<string> Indexed(IReadOnlyDictionary<string, string> parameters, string prefix)
    {
        var full = prefix + ":";

        return parameters
            .Where(p => p.Key.StartsWith(full, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(p.Value))
            .OrderBy(p => int.TryParse(p.Key[full.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? index
                : int.MaxValue)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Value)
            .ToList();
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

    private static string? Optional(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static bool ParseBool(IReadOnlyDictionary<string, string> parameters, string key) =>
        Optional(parameters, key) is { } raw && bool.TryParse(raw, out var value) && value;

    private static int? ParseInt(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (Optional(parameters, key) is not { } raw)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException(
                $"Provisioning parameter '{key}' must be a whole number, but was '{raw}'.",
                nameof(parameters));
        }

        return value;
    }

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside the
    /// provisioner so it — and only it — can reach the API client the provisioner is configured with.
    /// </summary>
    private sealed class FargateServiceCreateOperation : IProvisioningOperation
    {
        private readonly AwsEcsFargateProvisioner _owner;
        private readonly AwsFargateServiceSpec _spec;
        private readonly IReadOnlyDictionary<string, string> _tags;

        internal FargateServiceCreateOperation(AwsEcsFargateProvisioner owner, AwsFargateServiceSpec spec)
        {
            _owner = owner;
            _spec = spec;

            // Materialised once, at construction, because the executor reads Tags *before* CreateAsync in order
            // to commit them to the write-ahead ledger - so they must be the same values that later reach the
            // provider. Validated here too, so a tag ECS would reject fails before the ledger row is written.
            // This is also why servyx.aws-ecs-task-definition-family records the family and not the revision
            // ARN: the ARN does not exist until RegisterTaskDefinition has already run.
            _tags = ServyxEcsTags.Validate(TagsFor(spec));
        }

        public string ProvisionerId => Id;

        public string? Region => _owner._region;

        public IReadOnlyDictionary<string, string> Tags => _tags;

        /// <summary>
        /// Registers the task definition, creates the service, confirms a task is actually running, and hands the
        /// service back as a resource no transport can reach.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Two writes, in the order that makes a failure cheapest.</strong> The task definition is
        /// registered first because it is free and because it is what validates most of the deployment: an
        /// invalid CPU/memory pair, a malformed EFS volume or an unusable execution role is refused there, before
        /// any service exists and therefore before anything can bill. Only then is the billable
        /// <c>CreateService</c> issued, naming the exact revision ARN rather than the family — a bare family name
        /// resolves to whatever is latest when ECS reads it, which would let a service launch a revision this
        /// plan never described.
        /// </para>
        /// <para>
        /// <strong>The third step is the one that matters, and it is not a write.</strong> ECS answers
        /// <c>CreateService</c> with a service that is already <c>ACTIVE</c> and running nothing. This method does
        /// not return until a task reports <c>lastStatus: RUNNING</c> of its own accord, and raises otherwise —
        /// carrying ECS's own <c>stoppedReason</c>, which is where an EFS mount failure or an unpullable image
        /// actually shows up.
        /// </para>
        /// <para>
        /// <strong>What this method does not do</strong>: it creates no cluster, no EFS file system, no mount
        /// target, no security group, no IAM role and no log group; it runs no command in the container and opens
        /// no connection to it. It resolves no secret of its own — this shape has none — beyond the AWS key pair
        /// <see cref="AwsRequestSigner"/> resolves per request to sign with. It does not know what game is inside
        /// the image.
        /// </para>
        /// </remarks>
        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;

            var definition = await api
                .RegisterTaskDefinitionAsync(AwsEcsRequests.RegisterTaskDefinition(_spec, _tags, _owner._region), ct)
                .ConfigureAwait(false);

            var service = await api
                .CreateServiceAsync(AwsEcsRequests.CreateService(_spec, definition.TaskDefinitionArn, _tags), ct)
                .ConfigureAwait(false);

            var task = await WaitForRunningTaskAsync(ct).ConfigureAwait(false);

            return new ProvisionedResource(
                Handle: new ResourceHandle(
                    Id,
                    service.ServiceArn,
                    _owner._region,
                    service.Tags.Count > 0
                        ? new Dictionary<string, string>(service.Tags, StringComparer.Ordinal)
                        : new Dictionary<string, string>(_tags, StringComparer.Ordinal)),
                ConnectorId: _spec.Tags.ConnectorId,
                // The whole point of this adapter. There is no transport id that would be true here, so none is
                // named - and the reason travels with the resource so whoever sees it knows nothing broke.
                Reachability: new ResourceReachability.NoTransport(UnreachableReason),
                Facts: new ResourceFacts(
                    // Never guessed. See the provisioner's remarks on addressing: DescribeTasks reports no public
                    // address, and the private one below belongs to a task the service will replace.
                    PublicAddress: null,
                    PrivateAddress: task.PrivateIpv4Address,
                    Cost: AwsFargatePricing.For(_spec.CpuUnits, _spec.MemoryMib),
                    CreatedAt: service.CreatedAt ?? DateTimeOffset.UnixEpoch));
        }

        /// <summary>
        /// Attempts to undo a failed <see cref="CreateAsync"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Asks before it deletes.</strong> The service is read back first and destroyed only if it
        /// exists <em>and</em> carries this operation's own <c>servyx.instance-id</c>. <c>CreateService</c> is not
        /// an upsert — a duplicate name is refused outright — but a name collision with a pre-existing service
        /// means the create failed <em>because</em> someone else's service is already there, and blindly deleting
        /// the name in the spec would then destroy infrastructure Servyx never made.
        /// </para>
        /// <para>
        /// <strong>Best-effort, and deliberately not polled.</strong> Compensation issues the delete and returns;
        /// it does not wait for <c>INACTIVE</c>. A service left <c>DRAINING</c> still carries every Servyx tag,
        /// so a reconcile finds it — which is a better place to spend the wait than inside a compensation path
        /// that is itself already handling a failure.
        /// </para>
        /// <para>
        /// <strong>Never touches the EFS file system, its access point, or its mount targets</strong>, under any
        /// circumstance, including when the create failed after the volume was attached. They hold the customer's
        /// data and Servyx did not create them. The task definition revision, if one was registered, is also left
        /// alone: it is free, and deregistering is not deleting.
        /// </para>
        /// </remarks>
        public async Task CompensateAsync(CancellationToken ct = default)
        {
            var api = _owner._api;

            var existing = await api
                .DescribeServiceAsync(_owner._cluster, _spec.ServiceName, ct)
                .ConfigureAwait(false);

            if (existing is null || existing.IsInactive)
            {
                return;
            }

            if (!ServyxEcsTags.IsManaged(existing.Tags)
                || !existing.Tags.TryGetValue(ServyxTagKeys.InstanceId, out var instanceId)
                || !string.Equals(instanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
            {
                return;
            }

            await api.DeleteServiceAsync(_owner._cluster, _spec.ServiceName, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns the service's first task that reports <c>RUNNING</c>, polling until one does.
        /// </summary>
        /// <remarks>
        /// The confirmation step, and the reason this adapter can claim to create a workload rather than to
        /// submit one. On exhaustion it makes two extra reads it does not make on the happy path — listing the
        /// service's <c>STOPPED</c> tasks and describing them — purely to put ECS's own <c>stoppedReason</c> into
        /// the failure message. That reason is where "no EFS mount target in this availability zone" and
        /// "CannotPullContainerError" actually appear, and an exception that omitted it would send an operator to
        /// the console to find out what a call Servyx already made had been told.
        /// </remarks>
        private async Task<EcsTask> WaitForRunningTaskAsync(CancellationToken ct)
        {
            var api = _owner._api;

            for (var attempt = 0; attempt < _owner._pollAttempts; attempt++)
            {
                var arns = await api
                    .ListTaskArnsAsync(_owner._cluster, _spec.ServiceName, RunningDesiredStatus, ct)
                    .ConfigureAwait(false);

                if (arns.Count > 0)
                {
                    var tasks = await api.DescribeTasksAsync(_owner._cluster, arns, ct).ConfigureAwait(false);
                    var running = tasks.FirstOrDefault(t => t.IsRunning);

                    if (running is not null)
                    {
                        return running;
                    }
                }

                await Task.Delay(_owner._pollInterval, _owner._timeProvider, ct).ConfigureAwait(false);
            }

            // Resolved into a local before the message is built: an interpolated string converted to a
            // DefaultInterpolatedStringHandler is a ref struct expression, and C# will not allow an await inside
            // one.
            var reason = await MostRecentStopReasonAsync(ct).ConfigureAwait(false);

            throw new AwsApiException(
                HttpStatusCode.Accepted,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ECS service '{_spec.ServiceName}' in cluster '{_owner._cluster}' was created but no task reached {EcsTask.RunningStatus} within {_owner._pollAttempts} poll(s). The service exists, is Servyx-tagged, and will keep trying to start a task - which bills per second whenever one gets far enough; compensation will delete it. ECS's own account of the most recent failure: {reason}"));
        }

        /// <summary>ECS's stated reason for the most recently stopped task, or a plain statement that there is none.</summary>
        private async Task<string> MostRecentStopReasonAsync(CancellationToken ct)
        {
            var api = _owner._api;

            var arns = await api
                .ListTaskArnsAsync(_owner._cluster, _spec.ServiceName, StoppedDesiredStatus, ct)
                .ConfigureAwait(false);

            if (arns.Count == 0)
            {
                return "no stopped task was found for the service, so it is still trying rather than failing "
                    + "outright - most often a slow image pull.";
            }

            var tasks = await api.DescribeTasksAsync(_owner._cluster, arns, ct).ConfigureAwait(false);
            var stopped = tasks.FirstOrDefault(t => t.IsStopped) ?? tasks.FirstOrDefault();

            return stopped?.StoppedText ?? "ECS described no stopped task despite listing one.";
        }
    }
}
