using System.Globalization;

using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Aws.Provisioning;

/// <summary>
/// The persistent Amazon EFS volume a Fargate task mounts — mandatory, because a Fargate task's own storage is
/// ephemeral by construction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Mandatory by construction, for a harder reason than Azure Container Instances'.</strong>
/// <see cref="AwsFargateServiceSpec"/> takes this type as a required constructor argument, so a spec describing
/// an unmounted Fargate service cannot be built — the same shape as <c>AzureFileShareMount</c>. The reason is
/// worse here. An ACI container group loses its writable layer when ACI happens to restart it; a Fargate
/// <em>service</em> replaces its task as a matter of ordinary operation — on host retirement, on a platform
/// version rollout, whenever the container exits — and the task's 20 GiB of ephemeral storage is destroyed with
/// it every single time. An unmounted Fargate game server does not risk losing its saves; it is guaranteed to,
/// on a schedule AWS chooses.
/// </para>
/// <para>
/// <strong>What Fargate actually offers, and why this is EFS.</strong> A Fargate task has exactly three storage
/// options. Ephemeral task storage (20 GiB by default, configurable to 200 GiB) dies with the task and is
/// therefore not storage for this purpose at all. FSx for Windows File Server is Windows-only and this adapter
/// provisions Linux tasks. Amazon EFS is the one durable option for a Linux Fargate task, attached as a
/// <c>volumes[].efsVolumeConfiguration</c> on the task definition. There is no fourth answer and no EBS volume to
/// reach for: EBS attaches to an instance, and a Fargate task has no instance.
/// </para>
/// <para>
/// <strong>There is no credential here, and that is a genuine improvement over the ACI mount.</strong>
/// <c>AzureFileShareMount</c> must carry a <see cref="SecretUrn"/> because ACI supports no managed identity for
/// SMB and requires the storage account key as a literal in the ARM body. EFS is authorised by network reachability
/// and IAM — a mount target in the task's subnet, a security group that permits NFS on 2049 from the task's
/// security group, and optionally an access point with <c>iam: ENABLED</c> evaluated against the task role.
/// Nothing on this type is secret, nothing is resolved from <see cref="ISecretStore"/> at mount time, and no
/// credential reaches the <c>RegisterTaskDefinition</c> body. The whole class of leak that
/// <c>AzureFileShareMount</c>'s remarks argue about does not arise.
/// </para>
/// <para>
/// <strong>What replaces it is worse in a different way, and is stated rather than traded away.</strong> Those
/// same network preconditions — a mount target in every availability zone the task may land in, and an inbound
/// NFS rule — are things Servyx does not create, cannot see, and cannot validate before the fact. When they are
/// missing, every ECS API call still succeeds: the task definition registers, the service is created, ECS reports
/// <c>ACTIVE</c>, and the task then fails at <c>PROVISIONING</c> with the reason buried in
/// <c>DescribeTasks</c>'s <c>stoppedReason</c>. That is exactly why this adapter's create path confirms by
/// reading a task's own status and surfaces <see cref="EcsTask.StoppedReason"/> in the failure; a credential
/// mistake is loud and this is not.
/// </para>
/// <para>
/// <strong>The file system is not Servyx's to create or destroy.</strong> It must already exist. This adapter
/// issues no <c>elasticfilesystem</c> call of any kind: it does not create the file system, does not create mount
/// targets, does not create the access point, and — most importantly — never deletes any of them. Deleting a save
/// directory is not a provisioning verb (see <see cref="ProvisioningCapabilities.Destroy"/>), and here the same
/// rule has the same colder edge it has for ACI: the file system must outlive the service, because the whole
/// point of mounting it is that the workload is disposable and the data is not.
/// </para>
/// </remarks>
public sealed record EfsVolumeMount
{
    /// <summary>The name the volume is given inside the task definition.</summary>
    public const string VolumeName = "servyx-data";

    /// <summary>The prefix every EFS file system id carries.</summary>
    public const string FileSystemIdPrefix = "fs-";

    /// <summary>The prefix every EFS access point id carries.</summary>
    public const string AccessPointIdPrefix = "fsap-";

    /// <summary>
    /// The transit-encryption setting this adapter writes, always. There is deliberately no knob for it.
    /// </summary>
    /// <remarks>
    /// EFS mounts are NFSv4 over the task's VPC network, and unencrypted is a real option AWS offers. It is not
    /// offered here: this volume holds a customer's save data, encryption in transit costs nothing, and EFS
    /// requires it anyway whenever an access point or IAM authorisation is used. A configuration knob whose only
    /// use is to make a deployment less safe is not a feature.
    /// </remarks>
    public const string TransitEncryption = "ENABLED";

    /// <summary>Creates a mount description.</summary>
    /// <param name="fileSystemId">The EFS file system to mount, e.g. <c>fs-0123456789abcdef0</c>. Must already exist.</param>
    /// <param name="containerPath">The absolute path the volume is mounted at inside the container.</param>
    /// <param name="rootDirectory">
    /// The directory within the file system to expose as the volume root. Must be <c>/</c> when
    /// <paramref name="accessPointId"/> is supplied — AWS refuses any other combination, because an access point
    /// already imposes a root of its own.
    /// </param>
    /// <param name="accessPointId">
    /// An EFS access point to mount through, or <see langword="null"/> for a direct mount. Worth using: an access
    /// point pins the POSIX uid/gid and the root directory, which is how a container running as a non-root user
    /// gets a writable data directory without anyone chmod-ing a shared file system.
    /// </param>
    /// <param name="readOnly">Whether the volume is mounted read-only. Almost never true for a game server's data directory.</param>
    /// <exception cref="ArgumentException">
    /// An id does not carry its AWS prefix, <paramref name="containerPath"/> is not an absolute POSIX path, or a
    /// root directory other than <c>/</c> was combined with an access point.
    /// </exception>
    public EfsVolumeMount(
        string fileSystemId,
        string containerPath,
        string rootDirectory = "/",
        string? accessPointId = null,
        bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileSystemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        if (!fileSystemId.StartsWith(FileSystemIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{fileSystemId}' is not an EFS file system id. One looks like '{FileSystemIdPrefix}0123456789abcdef0'. "
                + "Servyx never creates the file system, so this must name one that already exists - checked here "
                + "rather than at RegisterTaskDefinition, because by then a caller has already approved a plan.",
                nameof(fileSystemId));
        }

        if (!containerPath.StartsWith('/'))
        {
            throw new ArgumentException(
                $"'{containerPath}' is not an absolute path. A container mount path must begin with '/'.",
                nameof(containerPath));
        }

        if (accessPointId is not null)
        {
            if (!accessPointId.StartsWith(AccessPointIdPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"'{accessPointId}' is not an EFS access point id. One looks like "
                    + $"'{AccessPointIdPrefix}0123456789abcdef0'.",
                    nameof(accessPointId));
            }

            if (!string.Equals(rootDirectory, "/", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"An EFS access point already imposes its own root directory, so rootDirectory must be '/' "
                    + $"when one is used - it was '{rootDirectory}'. AWS refuses the combination outright; "
                    + "catching it here means the plan a caller approves is one that can actually be applied.",
                    nameof(rootDirectory));
            }
        }

        FileSystemId = fileSystemId;
        ContainerPath = containerPath;
        RootDirectory = rootDirectory;
        AccessPointId = accessPointId;
        ReadOnly = readOnly;
    }

    /// <summary>The EFS file system mounted. Never created or destroyed by this adapter.</summary>
    public string FileSystemId { get; }

    /// <summary>The absolute path the volume is mounted at inside the container.</summary>
    public string ContainerPath { get; }

    /// <summary>The directory within the file system exposed as the volume root.</summary>
    public string RootDirectory { get; }

    /// <summary>The EFS access point mounted through, if any.</summary>
    public string? AccessPointId { get; }

    /// <summary>Whether the volume is mounted read-only.</summary>
    public bool ReadOnly { get; }
}

/// <summary>
/// The discrete CPU/memory combinations AWS Fargate will actually run a task at.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Fargate has a size catalogue where ACI has two continuous dials, and getting it wrong is a plan-time
/// failure that would otherwise surface at create time.</strong> Azure Container Instances bills vCPU and memory
/// as independent continuous quantities, so <c>AzureContainerGroupSpec</c> can accept any positive pair. Fargate
/// accepts a fixed matrix: seven task CPU values, each with its own allowed memory range and step. A pair outside
/// it is refused by <c>RegisterTaskDefinition</c> with an <c>InvalidParameterException</c>.
/// </para>
/// <para>
/// That refusal is exactly the failure this type exists to move earlier. <c>AwsEcsFargateProvisioner.PlanAsync</c>
/// issues no HTTP request, so nothing validates a plan against AWS before someone approves it — the same argument
/// <c>ServyxEc2Tags.Validate</c> makes for tags, applied to the one other field that can make a whole deployment
/// unbuildable. A caller who asks for 1 vCPU and 1 GB gets an <see cref="ArgumentException"/> naming the legal
/// memory range for 1 vCPU, rather than a plan that looks fine and dies on the first write.
/// </para>
/// <para>
/// <strong>This is a snapshot of a published matrix and will go stale, though far more slowly than a price.</strong>
/// AWS has added rows to it twice (the 8 and 16 vCPU rows arrived in 2022) and has never removed one. A future row
/// this table does not know would be rejected here as invalid — the failure direction is refusing something legal,
/// not permitting something illegal, which is the right way round for a check whose job is to stop a bad create.
/// </para>
/// </remarks>
public static class AwsFargateSizing
{
    /// <summary>The task CPU reservation this adapter defaults to, in ECS CPU units. 1024 units is one vCPU.</summary>
    public const int DefaultCpuUnits = 1024;

    /// <summary>The task memory reservation this adapter defaults to, in MiB.</summary>
    public const int DefaultMemoryMib = 2048;

    /// <summary>ECS CPU units per vCPU. The unit Fargate's per-second vCPU meter is derived from.</summary>
    public const int CpuUnitsPerVcpu = 1024;

    /// <summary>MiB per GB, as Fargate's memory meter counts them.</summary>
    public const int MibPerGb = 1024;

    /// <summary>
    /// The default ephemeral storage every Fargate task gets, in GiB. Included in the task price; destroyed with
    /// the task. Named here so the pricing file can say what it is not charging for.
    /// </summary>
    public const int DefaultEphemeralStorageGib = 20;

    private static readonly (int Cpu, int MinMemory, int MaxMemory, int Step)[] Matrix =
    [
        (256, 512, 2048, 0),
        (512, 1024, 4096, 1024),
        (1024, 2048, 8192, 1024),
        (2048, 4096, 16384, 1024),
        (4096, 8192, 30720, 1024),
        (8192, 16384, 61440, 4096),
        (16384, 32768, 122880, 8192),
    ];

    // The 256-unit row is the one irregular one: AWS lists exactly three memory values for it rather than a
    // range with a step, so it is enumerated instead of computed.
    private static readonly int[] QuarterVcpuMemory = [512, 1024, 2048];

    /// <summary>Whether Fargate will run a task with this CPU and memory reservation.</summary>
    public static bool IsValid(int cpuUnits, int memoryMib)
    {
        foreach (var row in Matrix)
        {
            if (row.Cpu != cpuUnits)
            {
                continue;
            }

            if (row.Step == 0)
            {
                return Array.IndexOf(QuarterVcpuMemory, memoryMib) >= 0;
            }

            return memoryMib >= row.MinMemory
                && memoryMib <= row.MaxMemory
                && (memoryMib - row.MinMemory) % row.Step == 0;
        }

        return false;
    }

    /// <summary>
    /// Returns <paramref name="cpuUnits"/>/<paramref name="memoryMib"/> unchanged, or throws describing the
    /// legal memory values for the requested CPU.
    /// </summary>
    /// <param name="cpuUnits">The task CPU reservation, in ECS CPU units.</param>
    /// <param name="memoryMib">The task memory reservation, in MiB.</param>
    /// <param name="paramName">The parameter name to attribute the failure to.</param>
    /// <exception cref="ArgumentException">Fargate would refuse the combination.</exception>
    public static (int CpuUnits, int MemoryMib) Require(int cpuUnits, int memoryMib, string paramName)
    {
        if (IsValid(cpuUnits, memoryMib))
        {
            return (cpuUnits, memoryMib);
        }

        throw new ArgumentException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"AWS Fargate will not run a task with cpu={cpuUnits} units and memory={memoryMib} MiB. {DescribeAllowed(cpuUnits)} RegisterTaskDefinition would refuse this pair, so it is refused here instead - a plan is approved before any request is sent, and a plan that cannot be applied is worse than no plan."),
            paramName);
    }

    /// <summary>A human-readable statement of what memory values are legal for <paramref name="cpuUnits"/>.</summary>
    public static string DescribeAllowed(int cpuUnits)
    {
        foreach (var row in Matrix)
        {
            if (row.Cpu != cpuUnits)
            {
                continue;
            }

            return row.Step == 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"For cpu={cpuUnits} the only legal memory values are {string.Join(", ", QuarterVcpuMemory)} MiB.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"For cpu={cpuUnits} memory must be between {row.MinMemory} and {row.MaxMemory} MiB in steps of {row.Step} MiB.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"cpu={cpuUnits} is not a Fargate task CPU value at all; the legal values are {string.Join(", ", Matrix.Select(r => r.Cpu))} units (1024 units = 1 vCPU).");
    }
}

/// <summary>
/// Everything needed to create one AWS Fargate deployment: a task definition revision and the ECS service that
/// keeps one task of it running.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this does not wrap <see cref="MachineSpec"/>.</strong> For the same reasons
/// <c>AzureContainerGroupSpec</c> does not: <see cref="MachineSpec"/> describes a machine — a bootable image
/// reference, a named size, an SSH public key to authorise, cloud-init to run on first boot — and a Fargate task
/// has none of those concepts. Its image is an OCI reference; its size is a CPU/memory pair drawn from
/// <see cref="AwsFargateSizing"/>'s matrix rather than a named catalogue entry; it has no sshd to authorise a key
/// against and no init to run a script. Carrying four fields of which three are structurally inapplicable would
/// misrepresent shape M as a variant of shape I, which is the finding <c>docs/provisioning.md</c> §11 records.
/// </para>
/// <para>
/// <strong>Three things are mandatory in the constructor, and each is mandatory for a different reason.</strong>
/// <see cref="Mount"/> because a Fargate service without persistent storage is a guaranteed data-loss machine —
/// see <see cref="EfsVolumeMount"/>. <see cref="SubnetIds"/> because <c>awsvpc</c> is the only network mode
/// Fargate supports and it has no default: a task with no subnet cannot be placed at all, so "forgot the subnets"
/// must not be a plan a caller can approve. <see cref="ClusterName"/> because there is nowhere else for a service
/// to live, and this adapter creates no cluster.
/// </para>
/// <para>
/// <strong>What it shares with the other specs in this assembly.</strong> The Servyx identity is a
/// <see cref="ServyxEcsTags"/> that cannot be constructed incompletely, extra tags are additive and can never
/// shadow a canonical key, and nothing on this type is a credential value — here trivially so, because this shape
/// has no credential at all.
/// </para>
/// </remarks>
public sealed record AwsFargateServiceSpec
{
    /// <summary>The number of tasks the service keeps running. Fixed at one; see the type remarks on the provisioner.</summary>
    public const int DesiredCount = 1;

    /// <summary>The only network mode Fargate supports, and therefore the only one this adapter writes.</summary>
    public const string NetworkMode = "awsvpc";

    /// <summary>The launch type this adapter creates services with.</summary>
    public const string LaunchType = "FARGATE";

    /// <summary>The Fargate platform version this adapter requests.</summary>
    /// <remarks>
    /// <c>LATEST</c> rather than a pin. A pinned platform version goes end-of-life and then silently stops being
    /// available, and Servyx has no update path that could move a service off a retired one — so a pin here would
    /// be a deployment that works today and cannot be recreated in two years. The cost of <c>LATEST</c> is that
    /// AWS may roll the task's platform underneath the service, which it does by replacing the task; that is the
    /// same replacement the service exists to survive.
    /// </remarks>
    public const string PlatformVersion = "LATEST";

    /// <summary>Creates a spec.</summary>
    /// <param name="serviceName">The ECS service's name. Also the default task definition family.</param>
    /// <param name="clusterName">
    /// The ECS cluster the service is created in. <strong>Must already exist</strong> — this adapter creates no
    /// cluster, for the reasons given on <c>AwsEcsFargateProvisioner</c>.
    /// </param>
    /// <param name="image">The OCI image reference the container runs, e.g. <c>docker.io/library/nginx:1.27</c>.</param>
    /// <param name="mount">The mandatory persistent EFS volume.</param>
    /// <param name="subnetIds">The subnets the task's elastic network interface may be placed in. At least one is required.</param>
    /// <param name="tags">The mandatory Servyx identity, which cannot be constructed incompletely.</param>
    /// <exception cref="ArgumentException">A required string is blank, or no subnet was named.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="mount"/>, <paramref name="subnetIds"/> or <paramref name="tags"/> is null.</exception>
    public AwsFargateServiceSpec(
        string serviceName,
        string clusterName,
        string image,
        EfsVolumeMount mount,
        IReadOnlyList<string> subnetIds,
        ServyxEcsTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(subnetIds);
        ArgumentNullException.ThrowIfNull(tags);

        if (subnetIds.Count == 0 || subnetIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "At least one subnet id is required. Fargate supports only the 'awsvpc' network mode, which "
                + "places an elastic network interface for the task and has no default subnet - a task with no "
                + "subnet cannot be placed at all. Name subnets in availability zones the EFS file system has "
                + "mount targets in, or the task will start and then fail to mount its data volume.",
                nameof(subnetIds));
        }

        ServiceName = serviceName;
        ClusterName = clusterName;
        Image = image;
        Mount = mount;
        SubnetIds = [.. subnetIds];
        Tags = tags;
        ContainerName = serviceName;
        Family = serviceName;
    }

    /// <summary>The ECS service's name at the provider.</summary>
    public string ServiceName { get; }

    /// <summary>The ECS cluster the service lives in. Never created or destroyed by this adapter.</summary>
    public string ClusterName { get; }

    /// <summary>The OCI image reference the container runs.</summary>
    public string Image { get; }

    /// <summary>The mandatory persistent EFS volume. There is no way to build a spec without one.</summary>
    public EfsVolumeMount Mount { get; }

    /// <summary>The subnets the task's network interface may be placed in. Never empty.</summary>
    public IReadOnlyList<string> SubnetIds { get; }

    /// <summary>The mandatory Servyx identity stamped onto both the task definition and the service.</summary>
    public ServyxEcsTags Tags { get; }

    /// <summary>The single container's name within the task. Defaults to the service's own name.</summary>
    public string ContainerName { get; init; }

    /// <summary>The task definition family. Defaults to the service's own name; every provision adds a revision to it.</summary>
    public string Family { get; init; }

    /// <summary>The task CPU reservation, in ECS CPU units. One of the two meters Fargate bills per second.</summary>
    public int CpuUnits { get; init; } = AwsFargateSizing.DefaultCpuUnits;

    /// <summary>The task memory reservation, in MiB. The other of the two meters Fargate bills per second.</summary>
    public int MemoryMib { get; init; } = AwsFargateSizing.DefaultMemoryMib;

    /// <summary>
    /// The security groups the task's network interface joins, or empty for the VPC's default security group.
    /// </summary>
    /// <remarks>
    /// Referenced, never created or modified. This is the difference between this adapter and ACI's on ingress:
    /// a Fargate task genuinely does sit behind a security group with real source-address rules, so the mechanism
    /// exists — it simply is not Servyx's to write. A requested <see cref="FirewallRule.SourceCidr"/> is therefore
    /// reported in the plan as NOT APPLIED for a different reason than ACI's: there, no filter exists; here, one
    /// exists and this adapter does not touch it. Note also that the group named here must permit outbound NFS to
    /// the EFS mount targets, or the task will fail to start.
    /// </remarks>
    public IReadOnlyList<string> SecurityGroupIds { get; init; } = [];

    /// <summary>
    /// Whether the task's network interface gets a public IPv4 address.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>, because a game server nothing can connect to is not a game server —
    /// and because a task in a private subnet with no NAT gateway cannot even pull its own image. Note what this
    /// does <em>not</em> buy: the address belongs to the task, changes every time the service replaces it, and is
    /// not reported by <c>DescribeTasks</c> at all. See <c>AwsEcsFargateProvisioner</c>'s remarks on addressing.
    /// </remarks>
    public bool AssignPublicIp { get; init; } = true;

    /// <summary>
    /// The IAM role ECS assumes to pull the image and write logs, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Required in practice for an image in ECR or any log configuration, and referenced rather than created:
    /// this adapter makes no IAM call. A task definition without one still registers, which is why this is
    /// nullable rather than mandatory — a public image with no logging genuinely needs no execution role.
    /// </remarks>
    public string? ExecutionRoleArn { get; init; }

    /// <summary>
    /// The IAM role the container itself assumes, or <see langword="null"/> for none.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ExecutionRoleArn"/>, which is the agent's identity rather than the workload's.
    /// Needed when the EFS access point is configured with <c>iam: ENABLED</c>, and otherwise optional.
    /// </remarks>
    public string? TaskRoleArn { get; init; }

    /// <summary>
    /// The CloudWatch Logs group the container's output is written to, or <see langword="null"/> for no log
    /// configuration at all.
    /// </summary>
    /// <remarks>
    /// Worth setting, and the plan says so when it is not: a Fargate task with no log configuration discards its
    /// container's stdout and stderr entirely. There is no host to read them from afterwards and no
    /// <c>docker logs</c> to run — the one diagnostic channel a task has is the one configured here. Referenced,
    /// never created: this adapter makes no <c>logs</c> API call, and a group that does not exist makes the task
    /// fail to start.
    /// </remarks>
    public string? LogGroup { get; init; }

    /// <summary>
    /// The ports published on the task's network interface.
    /// </summary>
    /// <remarks>
    /// A <see cref="FirewallRule"/> is reused as the domain's existing shape for "port, protocol, source", and —
    /// as on ACI — only two thirds of it can be honoured. In <c>awsvpc</c> mode the host port always equals the
    /// container port, so a port mapping is really just a declaration; what governs reachability is the security
    /// group, which this adapter does not write. See <see cref="SecurityGroupIds"/>.
    /// </remarks>
    public IReadOnlyList<FirewallRule> Ports { get; init; } = [];

    /// <summary>
    /// Environment variables handed to the container.
    /// </summary>
    /// <remarks>
    /// Plain values only, and deliberately so. ECS has a <c>secrets</c> member that resolves values from Secrets
    /// Manager or SSM Parameter Store, which is genuinely the right mechanism for a credential — and is not this
    /// dictionary. A value written here would have had to be a literal in a caller's configuration to get here,
    /// which is the thing Servyx's URN rule exists to prevent.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Extra Servyx tags to stamp alongside the canonical ones. Can never shadow a canonical key.</summary>
    public IReadOnlyDictionary<string, string> AdditionalTags { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The task CPU reservation expressed in vCPU, which is the unit Fargate publishes a price per.</summary>
    public decimal Vcpu => CpuUnits / (decimal)AwsFargateSizing.CpuUnitsPerVcpu;

    /// <summary>The task memory reservation expressed in GB, which is the unit Fargate publishes a price per.</summary>
    public decimal MemoryGb => MemoryMib / (decimal)AwsFargateSizing.MibPerGb;
}
