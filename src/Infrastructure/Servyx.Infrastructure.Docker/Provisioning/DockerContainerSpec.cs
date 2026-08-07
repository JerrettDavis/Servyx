namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>A container port to expose, and optionally publish to a host port.</summary>
/// <param name="ContainerPort">The port as exposed inside the container.</param>
/// <param name="Protocol">The transport protocol, <c>"tcp"</c> or <c>"udp"</c>, matching a game definition's <c>capabilities.network[].protocol</c>.</param>
/// <param name="HostPort">
/// The host port to publish on, or <see langword="null"/> to expose the port without publishing it —
/// mirroring a game definition's <c>published: false</c> (e.g. Palworld's RCON and REST ports).
/// </param>
public sealed record DockerPortBinding(int ContainerPort, string Protocol, int? HostPort);

/// <summary>A bind mount to attach to the container.</summary>
/// <param name="HostPath">The path on the host to mount.</param>
/// <param name="ContainerPath">The path inside the container to mount it at, matching a game definition's <c>capabilities.filesystem[].path</c>.</param>
/// <param name="ReadWrite">Whether the mount is writable from inside the container (a definition's <c>access: rw</c>).</param>
public sealed record DockerVolumeMount(string HostPath, string ContainerPath, bool ReadWrite);

/// <summary>
/// The full description of a container <c>DockerContainerProvisioner</c> may create.
/// </summary>
/// <remarks>
/// <see cref="Tags"/> is a required positional argument of type <see cref="ServyxResourceTags"/>, which
/// itself cannot be constructed without an instance id, a job id, and a connector id. That is the
/// structural guarantee that a spec — and therefore any container built from one — always carries the
/// mandatory Servyx labels. It is validated non-null here as well so a deliberate <c>null!</c> fails fast
/// rather than producing an unlabelled container.
/// </remarks>
/// <param name="Image">The image reference to create the container from, e.g. <c>"thijsvanloef/palworld-server-docker:latest"</c>.</param>
/// <param name="ContainerName">The container name to request from the Docker Engine.</param>
/// <param name="Tags">The mandatory Servyx labels. Cannot be omitted or defaulted.</param>
public sealed record DockerContainerSpec(string Image, string ContainerName, ServyxResourceTags Tags)
{
    /// <summary>The image reference to create the container from.</summary>
    public string Image { get; } = Validate(Image, nameof(Image));

    /// <summary>The container name to request from the Docker Engine.</summary>
    public string ContainerName { get; } = Validate(ContainerName, nameof(ContainerName));

    /// <summary>The mandatory Servyx labels applied to the created container.</summary>
    public ServyxResourceTags Tags { get; } = Tags ?? throw new ArgumentNullException(nameof(Tags));

    /// <summary>Ports to expose, and optionally publish.</summary>
    public IReadOnlyList<DockerPortBinding> Ports { get; init; } = [];

    /// <summary>Bind mounts to attach.</summary>
    public IReadOnlyList<DockerVolumeMount> Volumes { get; init; } = [];

    /// <summary>Environment variables to bake into the container at create time.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Extra labels to attach alongside the mandatory Servyx ones. Applied first, so they can never
    /// override a mandatory label.
    /// </summary>
    public IReadOnlyDictionary<string, string> AdditionalLabels { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The in-container path that <c>TargetPath</c> values are resolved relative to once the transport
    /// connects (a definition's <c>dataDir</c>, e.g. <c>/palworld</c>). Defaults to <c>/</c>.
    /// </summary>
    public string RootPath { get; init; } = "/";

    /// <summary>The Docker restart policy name to apply (e.g. <c>"unless-stopped"</c>), or <see langword="null"/> for the daemon default.</summary>
    public string? RestartPolicy { get; init; }

    /// <summary>
    /// Files to materialize into the container's storage after it is created but before it is started for
    /// the first time — the runtime destination of a game definition's <c>deployments[].files[]</c>.
    /// </summary>
    /// <remarks>
    /// Empty for every ordinary container, and deliberately so: an image that takes its configuration from
    /// environment variables needs none of this. It is non-empty only for the narrow class of image that
    /// invents a credential for itself on first start unless it finds one already on disk — see the remarks
    /// on <see cref="SeededFile"/>. Declaring any file here makes the provisioner require an
    /// <c>ITransport</c>, because the bytes are written through the transport's guarded write path and
    /// never through this adapter's own Docker client.
    /// </remarks>
    public IReadOnlyList<SeededFile> SeededFiles { get; init; } = [];

    /// <summary>
    /// How long the daemon waits for the workload to exit on its own before force-killing it, baked into the
    /// container at create time — the Engine-API equivalent of Compose's <c>stop_grace_period</c>, and the
    /// destination of a game definition's <c>deployments[].stopGracePeriodSeconds</c>.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> leaves the daemon's own default in force, which is <em>ten seconds</em> — long
    /// enough for almost nothing. A game that saves its world on shutdown routinely needs minutes, and a
    /// container created without this value set will be SIGKILLed part-way through that save by any plain
    /// <c>docker stop</c>, host reboot, or daemon shutdown, no matter how generous the definition's own
    /// <c>lifecycle.stop</c> ladder is: the ladder governs what Servyx waits for, this governs what the
    /// daemon waits for, and only the second one applies when Servyx is not the caller.
    /// </remarks>
    public TimeSpan? StopGracePeriod { get; init; }

    private static string Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }
}
