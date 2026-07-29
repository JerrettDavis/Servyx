using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// An <see cref="IProvisioner"/> that creates game-server workloads as Docker containers on a single
/// Docker Engine, and hands the result back as a <see cref="TargetDescriptor"/> the existing
/// <see cref="DockerTransport"/> consumes directly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only planning.</strong> <see cref="PlanAsync"/> issues no Docker call at all — mutating or
/// otherwise. A plan is pure computation over the request, which is the strongest form of the "planning
/// changes nothing" guarantee: there is no call to audit.
/// </para>
/// <para>
/// <strong>Mutation lives outside this type's <see cref="IProvisioner"/> surface.</strong> Creating a
/// container is reachable only through <see cref="CreateOperation"/>, which returns an
/// <see cref="IProvisioningOperation"/> for <c>Servyx.Application</c>'s plan executor to drive. Nothing on
/// the <see cref="IProvisioner"/> interface mutates anything, exactly as its remarks require.
/// </para>
/// <para>
/// <strong>Not registered by <c>AddServyxDocker()</c>.</strong> See
/// <see cref="DockerProvisioningServiceCollectionExtensions.AddServyxDockerProvisioning"/>: this type is
/// opt-in so the default read-only composition root has no path to a mutating Docker call.
/// </para>
/// <para>
/// <strong>No cost estimation, deliberately.</strong> <see cref="Capabilities"/> omits
/// <see cref="ProvisioningCapabilities.EstimatesCost"/>. A container on a machine Servyx did not rent has
/// no provider-billed price, and <see cref="CostEstimate.Unknown"/> is the honest answer — a fabricated
/// zero would be indistinguishable from a real zero-cost figure to every caller downstream.
/// </para>
/// <para>
/// <strong>Maintenance is preview-only.</strong> This type also implements <see cref="IMaintainer"/>, whose
/// two members read the live container and produce a description — an <see cref="UpdatePlan"/> or a
/// <see cref="DriftResult"/> — and nothing else. Neither issues a mutating engine call, and there is no
/// executor anywhere in this solution that applies an <see cref="UpdatePlan"/>. Recreating a container is
/// still reachable only by driving the existing create/destroy paths deliberately.
/// </para>
/// </remarks>
public sealed class DockerContainerProvisioner : IProvisioner, IMaintainer
{
    /// <summary>The stable <see cref="IProvisioner.ProvisionerId"/> of this provisioner.</summary>
    public const string Id = "docker-container";

    /// <summary>
    /// The <see cref="ITransport.TransportId"/> of the transport that consumes the descriptors this
    /// provisioner produces. Kept as a constant here (rather than instantiating a
    /// <see cref="DockerTransport"/> merely to read its property) and asserted equal to
    /// <see cref="DockerTransport.TransportId"/> by the handoff test, so drift is caught by a test rather
    /// than by a runtime "no transport for id" failure.
    /// </summary>
    internal const string DockerTransportId = "docker";

    private const string CostSource =
        "Local Docker containers are not billed by a provider; this provisioner does not advertise EstimatesCost.";

    private readonly IDockerClient _client;
    private readonly string? _endpoint;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a provisioner operating against <paramref name="client"/>.
    /// </summary>
    /// <param name="client">The Docker Engine client. Substituted in tests; no live daemon is required.</param>
    /// <param name="endpoint">
    /// The Docker endpoint string to stamp onto every <see cref="TargetDescriptor"/> this provisioner
    /// produces, e.g. <c>"npipe://./pipe/dockerDesktopLinuxEngine"</c>. When <see langword="null"/> the
    /// descriptor carries an empty endpoint, which <see cref="DockerEndpointResolver"/> then resolves the
    /// same way it does for any hand-configured target (<c>DOCKER_HOST</c>, then an OS default).
    /// </param>
    /// <param name="timeProvider">Clock used for plan expiry. Defaults to <see cref="TimeProvider.System"/>.</param>
    public DockerContainerProvisioner(IDockerClient client, string? endpoint = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _endpoint = endpoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string ProvisionerId => Id;

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ProvisioningCapabilities.EstimatesCost"/> is deliberately absent — see the type remarks.
    /// <see cref="ProvisioningCapabilities.TagQuery"/> is present and load-bearing: it is what
    /// <see cref="ReconcileAsync"/> depends on to find containers Servyx created but lost track of.
    /// <para>
    /// <see cref="ProvisioningCapabilities.RecreateToUpdate"/> is present and
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/> is deliberately absent. That pairing is a
    /// factual statement about the Docker Engine, not a roadmap: a container's image, published port
    /// bindings, and labels are fixed by <c>docker create</c> and there is no engine call that edits any of
    /// them on a live container. Every update this adapter can plan is therefore a stop-remove-create, with
    /// the downtime and the new container id that implies, and claiming the in-place bit would tell a
    /// caller it could avoid both.
    /// </para>
    /// </remarks>
    public ProvisioningCapabilities Capabilities =>
        ProvisioningCapabilities.Create
        | ProvisioningCapabilities.Destroy
        | ProvisioningCapabilities.FirewallRules
        | ProvisioningCapabilities.TagQuery
        | ProvisioningCapabilities.RecreateToUpdate
        | ProvisioningCapabilities.DetectDrift;

    /// <summary>
    /// Docker's local engine is not region-scoped, so every handle and plan this provisioner produces
    /// carries a null region rather than inventing one.
    /// </summary>
    public static string? Region => null;

    /// <inheritdoc />
    /// <remarks>
    /// Pure computation: builds the container spec from <paramref name="request"/>'s parameters and
    /// describes the stages needed to realise it. Issues no Docker Engine call whatsoever, so it cannot
    /// mutate anything.
    /// </remarks>
    public Task<ProvisioningPlan> PlanAsync(ProvisioningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var spec = BuildSpec(request);
        return Task.FromResult(BuildPlan(spec));
    }

    /// <summary>
    /// Builds the plan for an already-materialised <paramref name="spec"/>, for callers that constructed
    /// the spec themselves rather than via a <see cref="ProvisioningRequest"/>.
    /// </summary>
    public ProvisioningPlan BuildPlan(DockerContainerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var labels = LabelsFor(spec);
        var published = spec.Ports.Where(p => p.HostPort is not null).ToList();

        var stages = new List<ProvisioningStage>
        {
            new(
                "create-container",
                Id,
                $"Create container '{spec.ContainerName}' from image '{spec.Image}' with {labels.Count} Servyx labels, {spec.Volumes.Count} mount(s) and {spec.Environment.Count} environment variable(s)."),
            new(
                "publish-ports",
                Id,
                published.Count == 0
                    ? "Expose no ports to the host; all declared ports stay container-internal."
                    : $"Publish {string.Join(", ", published.Select(p => $"{p.HostPort}->{p.ContainerPort}/{p.Protocol}"))} to the host."),
            new(
                "start-container",
                Id,
                $"Start container '{spec.ContainerName}' and observe its assigned address."),
        };

        var planHash = ComputePlanHash(spec, labels);

        return new ProvisioningPlan(
            PlanId: $"{Id}:{spec.ContainerName}:{planHash[..12]}",
            PlanHash: planHash,
            Stages: stages,
            EstimatedCost: CostEstimate.Unknown(CostSource),
            ExpiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <inheritdoc />
    /// <remarks>Inspects the container by id. A container the engine no longer knows about yields <see langword="null"/>.</remarks>
    public async Task<ProvisionedResource?> RefreshAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return null;
        }

        var labels = ToOrdinalDictionary(inspect.Config?.Labels);
        var containerId = string.IsNullOrEmpty(inspect.ID) ? handle.ProviderResourceId : inspect.ID;
        var containerName = inspect.Name?.TrimStart('/');
        var rootPath = labels.TryGetValue(ServyxResourceTags.RootPathLabel, out var recordedRoot) && !string.IsNullOrWhiteSpace(recordedRoot)
            ? recordedRoot
            : "/";
        var connectorId = labels.TryGetValue(ServyxResourceTags.ConnectorIdLabel, out var recordedConnector) && !string.IsNullOrWhiteSpace(recordedConnector)
            ? recordedConnector
            : string.Empty;

        return new ProvisionedResource(
            Handle: new ResourceHandle(Id, containerId, Region, labels),
            ConnectorId: connectorId,
            Target: BuildTargetDescriptor(containerId, containerName, rootPath),
            Facts: BuildFacts(inspect));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The orphan-sweep primitive. Asks the engine for every container labelled
    /// <c>servyx.managed=true</c>, independent of any Servyx-local record, so a resource created but never
    /// acknowledged can still be found.
    /// </para>
    /// <para>
    /// The label filter is sent to the engine <em>and</em> re-applied to the response. That is not
    /// redundancy for its own sake: the filter is the daemon's promise, while the second check is this
    /// process's own guarantee that nothing unlabelled is ever reported as Servyx-owned and subsequently
    /// destroyed by a sweep. A sweep acting on a false positive deletes someone else's container.
    /// </para>
    /// <para>
    /// The engine's own inventory <em>is</em> the search space, so the only scope shape this provisioner can
    /// serve is <see cref="OrphanScope.ProviderWide"/>. A scope describing some other search space — a
    /// marker directory, say — is declined the same way a scope naming another provisioner is: no handles,
    /// no daemon call. Quietly widening a narrower request into "list every managed container" would hand a
    /// caller more containers than it asked to sweep, and a sweep's output is a delete list.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<ResourceHandle>> ReconcileAsync(OrphanScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope is not OrphanScope.ProviderWide || !string.Equals(scope.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return [];
        }

        var parameters = new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
            {
                ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal) { [ServyxResourceTags.ManagedFilter] = true },
            },
        };

        var containers = await _client.Containers.ListContainersAsync(parameters, ct).ConfigureAwait(false);

        var handles = new List<ResourceHandle>();
        foreach (var container in containers ?? [])
        {
            var labels = ToOrdinalDictionary(container.Labels);
            if (!ServyxResourceTags.IsManaged(labels))
            {
                continue;
            }

            handles.Add(new ResourceHandle(Id, container.ID, Region, labels));
        }

        return handles;
    }

    /// <summary>
    /// Returns the mutating operation that creates the container described by <paramref name="spec"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <c>ApplyAsync</c> on <see cref="IProvisioner"/>: the returned operation is
    /// driven by <c>Servyx.Application</c>'s plan executor, which owns the write-ahead ledger ordering.
    /// Calling this method creates nothing on its own.
    /// </remarks>
    public IProvisioningOperation CreateOperation(DockerContainerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ContainerCreateOperation(this, spec);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Thin translation onto the typed overload above: builds the container spec the same way
    /// <see cref="PlanAsync"/> does, via <see cref="BuildSpec"/>, so a plan preview and the operation that
    /// later realises it are always derived from the request the same way.
    /// </remarks>
    public IProvisioningOperation CreateOperation(ProvisioningRequest request) => CreateOperation(BuildSpec(request));

    /// <summary>
    /// Permanently removes a container this provisioner created, making
    /// <see cref="ProvisioningCapabilities.Destroy"/> a real capability rather than an advertised one.
    /// </summary>
    /// <returns><see langword="true"/> if the container was removed; <see langword="false"/> if it was already gone.</returns>
    public async Task<bool> DestroyAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return await RemoveContainerAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Reads, then computes. No mutating engine call is issued, ever.</strong> The only Docker call
    /// on this path is a single <c>InspectContainerAsync</c>; everything after it is pure comparison between
    /// the inspect response and the spec built from <paramref name="desired"/>. The container is not
    /// stopped, not removed, not relabelled, and not pulled for.
    /// </para>
    /// <para>
    /// <strong>Every difference forces a recreate, because the engine offers no alternative.</strong> Image,
    /// published port bindings, and labels are all fixed at <c>docker create</c> time, so a change to any of
    /// them is planned as stop → remove → create → start rather than as an edit. That is why
    /// <see cref="Capabilities"/> holds <see cref="ProvisioningCapabilities.RecreateToUpdate"/> and not
    /// <see cref="ProvisioningCapabilities.UpdateInPlace"/>.
    /// </para>
    /// <para>
    /// <strong>How the <see cref="DataImpact"/> is decided.</strong> It is derived from the live container's
    /// actual mounts, never defaulted. <see cref="DataImpact.Preserved"/> is asserted only when nothing
    /// needs to change at all, or when every mount the live container currently has is carried into the
    /// replacement at the same host path with the same access. A recreate of a container holding its state
    /// in its writable layer — i.e. with no mounts — is <see cref="DataImpact.AtRisk"/>, because removing
    /// the container removes that layer (see the remarks on
    /// <see cref="ProvisioningCapabilities.Destroy"/>), and so is a recreate that drops or remaps a mount:
    /// the bytes stay on the host but the workload comes back up without them.
    /// <see cref="DataImpact.Destroyed"/> is never asserted here, and that is a property of the adapter
    /// rather than an omission — the removal stage never sets <c>RemoveVolumes</c>, so no plan this method
    /// can produce deletes a volume.
    /// </para>
    /// </remarks>
    public async Task<UpdatePlan?> PlanUpdateAsync(
        ResourceHandle handle,
        ProvisioningRequest desired,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(desired);

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return null;
        }

        return BuildUpdatePlan(inspect, BuildSpec(desired));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Compares the live container's image and labels against what <paramref name="handle"/> records. The
    /// image expectation is read from the <see cref="ServyxResourceTags.ImageLabel"/> entry this provisioner
    /// stamps at create time; a handle predating that label reports its image as unverifiable rather than as
    /// matching, because a check that cannot prove a match must not claim one.
    /// </para>
    /// <para>
    /// A handle belonging to a different provisioner is answered without touching the engine, mirroring
    /// <see cref="ReconcileAsync"/>'s refusal to act on another provisioner's scope — but reported as a
    /// divergence rather than as a match, since "this is not my resource" is not evidence that it is intact.
    /// </para>
    /// </remarks>
    public async Task<DriftResult> DetectDriftAsync(ResourceHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (!string.Equals(handle.ProvisionerId, Id, StringComparison.Ordinal))
        {
            return new DriftResult(handle, [new DriftDivergence("provisioner", Id, handle.ProvisionerId)]);
        }

        var inspect = await InspectOrNullAsync(handle.ProviderResourceId, ct).ConfigureAwait(false);
        if (inspect is null)
        {
            return new DriftResult(handle, [new DriftDivergence("existence", "present", null)]);
        }

        var live = ToOrdinalDictionary(inspect.Config?.Labels);
        var recorded = handle.Tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var divergences = new List<DriftDivergence>();

        var liveImage = NullIfBlank(inspect.Config?.Image);
        var recordedImage = recorded.TryGetValue(ServyxResourceTags.ImageLabel, out var fromTags)
            ? NullIfBlank(fromTags)
            : null;

        if (!string.Equals(recordedImage, liveImage, StringComparison.Ordinal))
        {
            divergences.Add(new DriftDivergence("image", recordedImage, liveImage));
        }

        // The image is reported once, under its own aspect, rather than a second time as a label.
        foreach (var expected in recorded.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (string.Equals(expected.Key, ServyxResourceTags.ImageLabel, StringComparison.Ordinal))
            {
                continue;
            }

            var found = live.TryGetValue(expected.Key, out var value) ? value : null;
            if (!string.Equals(expected.Value, found, StringComparison.Ordinal))
            {
                divergences.Add(new DriftDivergence($"label {expected.Key}", expected.Value, found));
            }
        }

        return new DriftResult(handle, divergences);
    }

    /// <summary>
    /// Translates a <see cref="ProvisioningRequest"/>'s free-form parameters into a container spec.
    /// </summary>
    /// <remarks>
    /// Recognised keys, chosen to mirror a game definition's docker deployment profile
    /// (see <c>definitions/palworld-docker.yaml</c>):
    /// <list type="bullet">
    /// <item><description><c>image</c> — required, the image reference.</description></item>
    /// <item><description><c>containerName</c> — required.</description></item>
    /// <item><description><c>instanceId</c>, <c>jobId</c>, <c>connectorId</c> — required; become the mandatory Servyx labels.</description></item>
    /// <item><description><c>rootPath</c> — the profile's <c>dataDir</c>. Defaults to <c>/</c>.</description></item>
    /// <item><description><c>restartPolicy</c> — e.g. <c>unless-stopped</c>.</description></item>
    /// <item><description><c>port:&lt;containerPort&gt;/&lt;protocol&gt;</c> — value is the host port, or empty to expose without publishing (<c>published: false</c>).</description></item>
    /// <item><description><c>volume:&lt;containerPath&gt;</c> — value is <c>&lt;hostPath&gt;|rw</c> or <c>&lt;hostPath&gt;|ro</c>, mirroring <c>filesystem[].access</c>.</description></item>
    /// <item><description><c>env:&lt;NAME&gt;</c> — an environment variable baked in at create time.</description></item>
    /// <item><description><c>label:&lt;key&gt;</c> — an extra label; can never shadow a mandatory Servyx label.</description></item>
    /// </list>
    /// A key-per-item shape is used rather than one delimited string so no separator can ever collide with
    /// a Windows host path or a value containing a colon.
    /// <para>
    /// There is deliberately no per-request <c>endpoint</c> key. The endpoint is fixed at construction time
    /// because it is the same value the Docker client itself was built from — letting a single request
    /// override the stamped endpoint would reintroduce exactly the divergence
    /// <see cref="DockerProvisioningServiceCollectionExtensions.AddServyxDockerProvisioning"/> exists to make
    /// impossible: a container created on one daemon and recorded as living on another.
    /// </para>
    /// </remarks>
    public static DockerContainerSpec BuildSpec(ProvisioningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = request.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var tags = ServyxResourceTags.For(
            Required(parameters, "instanceId"),
            Required(parameters, "jobId"),
            request.ConnectorId ?? Required(parameters, "connectorId"));

        var ports = new List<DockerPortBinding>();
        var volumes = new List<DockerVolumeMount>();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var extraLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in parameters)
        {
            if (pair.Key.StartsWith("port:", StringComparison.Ordinal))
            {
                ports.Add(ParsePort(pair.Key["port:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("volume:", StringComparison.Ordinal))
            {
                volumes.Add(ParseVolume(pair.Key["volume:".Length..], pair.Value));
            }
            else if (pair.Key.StartsWith("env:", StringComparison.Ordinal))
            {
                environment[pair.Key["env:".Length..]] = pair.Value;
            }
            else if (pair.Key.StartsWith("label:", StringComparison.Ordinal))
            {
                extraLabels[pair.Key["label:".Length..]] = pair.Value;
            }
        }

        return new DockerContainerSpec(Required(parameters, "image"), Required(parameters, "containerName"), tags)
        {
            Ports = ports,
            Volumes = volumes,
            Environment = environment,
            AdditionalLabels = extraLabels,
            RootPath = parameters.TryGetValue("rootPath", out var rootPath) && !string.IsNullOrWhiteSpace(rootPath) ? rootPath : "/",
            RestartPolicy = parameters.TryGetValue("restartPolicy", out var restartPolicy) && !string.IsNullOrWhiteSpace(restartPolicy) ? restartPolicy : null,
        };
    }

    /// <summary>
    /// Builds the <see cref="TargetDescriptor"/> for a container. This is the whole handoff: the value
    /// returned here is what <see cref="DockerTransport.ProbeAsync"/> and
    /// <see cref="DockerTransport.ConnectAsync"/> consume, with no adapter in between. The option keys are
    /// exactly the ones <see cref="DockerTransport.ResolveContainerRef"/> and
    /// <see cref="DockerTransport.ResolveContainerRootPath"/> already read.
    /// </summary>
    internal TargetDescriptor BuildTargetDescriptor(string containerId, string? containerName, string rootPath)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["containerId"] = containerId,
            ["rootPath"] = rootPath,
        };

        if (!string.IsNullOrWhiteSpace(containerName))
        {
            options["containerName"] = containerName;
        }

        return new TargetDescriptor(
            TransportId: DockerTransportId,
            Endpoint: _endpoint ?? string.Empty,
            CredentialUrn: null,
            DockerContext: null,
            Options: options);
    }

    /// <summary>
    /// The single code path in this assembly capable of producing Docker create-parameters.
    /// </summary>
    /// <remarks>
    /// It is private, static, and takes the whole <see cref="DockerContainerSpec"/> — whose
    /// <see cref="DockerContainerSpec.Tags"/> is a required, non-defaultable
    /// <see cref="ServyxResourceTags"/>. Labels are always sourced from <see cref="LabelsFor"/>, which
    /// always ends with the mandatory Servyx entries. There is therefore no expressible call to this
    /// method — and no other route to <c>CreateContainerAsync</c> — that produces an unlabelled container.
    /// </remarks>
    private static CreateContainerParameters BuildCreateParameters(DockerContainerSpec spec)
    {
        var exposedPorts = new Dictionary<string, EmptyStruct>(StringComparer.Ordinal);
        var portBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal);

        foreach (var port in spec.Ports)
        {
            var key = FormatPortKey(port.ContainerPort, port.Protocol);
            exposedPorts[key] = default;

            if (port.HostPort is { } hostPort)
            {
                portBindings[key] = [new PortBinding { HostPort = hostPort.ToString(CultureInfo.InvariantCulture) }];
            }
        }

        var hostConfig = new HostConfig
        {
            Binds = spec.Volumes
                .Select(v => $"{v.HostPath}:{v.ContainerPath}:{(v.ReadWrite ? "rw" : "ro")}")
                .ToList(),
            PortBindings = portBindings,
        };

        if (ParseRestartPolicy(spec.RestartPolicy) is { } restartPolicy)
        {
            hostConfig.RestartPolicy = new RestartPolicy { Name = restartPolicy };
        }

        return new CreateContainerParameters
        {
            Image = spec.Image,
            Name = spec.ContainerName,
            Labels = new Dictionary<string, string>(LabelsFor(spec), StringComparer.Ordinal),
            Env = spec.Environment
                .Select(e => $"{e.Key}={e.Value}")
                .ToList(),
            ExposedPorts = exposedPorts,
            HostConfig = hostConfig,
        };
    }

    /// <summary>
    /// The complete label set for a spec: caller extras first, then the mandatory Servyx labels, then the
    /// recorded root path so <see cref="RefreshAsync"/> can rebuild an identical descriptor from the live
    /// container alone, and the image the container was created from so
    /// <see cref="DetectDriftAsync"/> has a recorded expectation to compare against.
    /// </summary>
    /// <remarks>
    /// Both descriptive labels are passed as <em>extras</em>, so they are written before the canonical
    /// identity keys and cannot shadow one — see <see cref="ServyxTagKeys.Build"/>. Because they go through
    /// the same dictionary a caller's <c>label:</c> parameters land in, a caller supplying its own
    /// <c>servyx.image</c> is overwritten here rather than being allowed to record an image the container
    /// was not created from.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> LabelsFor(DockerContainerSpec spec)
    {
        var extras = new Dictionary<string, string>(spec.AdditionalLabels, StringComparer.Ordinal)
        {
            [ServyxResourceTags.RootPathLabel] = spec.RootPath,
            [ServyxResourceTags.ImageLabel] = spec.Image,
        };

        return spec.Tags.ToLabels(extras);
    }

    /// <summary>
    /// Inspects a container, translating "the engine has never heard of it" into <see langword="null"/>.
    /// Shared by <see cref="RefreshAsync"/>, <see cref="PlanUpdateAsync"/>, and
    /// <see cref="DetectDriftAsync"/> so all three agree on what a missing container looks like.
    /// </summary>
    private async Task<ContainerInspectResponse?> InspectOrNullAsync(string containerId, CancellationToken ct)
    {
        try
        {
            return await _client.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// The whole of update planning: pure comparison between an already-fetched inspect response and the
    /// desired spec. It touches only <see cref="_timeProvider"/> (for the plan's expiry) and never
    /// <see cref="_client"/>, so every Docker call on the update path is the single inspect its caller
    /// already made.
    /// </summary>
    private UpdatePlan BuildUpdatePlan(ContainerInspectResponse inspect, DockerContainerSpec spec)
    {
        var containerName = inspect.Name?.TrimStart('/') ?? spec.ContainerName;
        var liveImage = NullIfBlank(inspect.Config?.Image);
        var liveMounts = ReadLiveMounts(inspect);
        var desiredLabels = LabelsFor(spec);

        var changes = new List<PlannedChange>();

        if (!string.Equals(liveImage, spec.Image, StringComparison.Ordinal))
        {
            // A container's image is fixed by `docker create`; there is no engine call that swaps it.
            changes.Add(new PlannedChange("image", liveImage, spec.Image, RequiresRecreate: true));
        }

        var livePorts = DescribePorts(ReadLivePorts(inspect));
        var desiredPorts = DescribePorts(DesiredPorts(spec));
        if (!string.Equals(livePorts, desiredPorts, StringComparison.Ordinal))
        {
            changes.Add(new PlannedChange("ports", livePorts, desiredPorts, RequiresRecreate: true));
        }

        var liveLabels = ToOrdinalDictionary(inspect.Config?.Labels);
        foreach (var desired in desiredLabels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            // servyx.image is a restatement of the image change already reported above; reporting it twice
            // would make one difference look like two in a preview.
            if (string.Equals(desired.Key, ServyxResourceTags.ImageLabel, StringComparison.Ordinal))
            {
                continue;
            }

            var current = liveLabels.TryGetValue(desired.Key, out var value) ? value : null;
            if (!string.Equals(current, desired.Value, StringComparison.Ordinal))
            {
                changes.Add(new PlannedChange($"label {desired.Key}", current, desired.Value, RequiresRecreate: true));
            }
        }

        var strategy = changes.Count == 0 ? UpdateStrategy.NoChangeRequired : UpdateStrategy.Recreate;
        var carriedOver = MountsCarriedOver(liveMounts, spec.Volumes);
        var dataImpact = AssertDataImpact(strategy, liveMounts, carriedOver);
        var stages = strategy == UpdateStrategy.NoChangeRequired
            ? (IReadOnlyList<ProvisioningStage>)[]
            : BuildRecreateStages(containerName, liveImage, liveMounts, carriedOver, spec, desiredLabels, dataImpact);

        var planHash = ComputeUpdatePlanHash(inspect.ID ?? containerName, liveImage, livePorts, liveLabels, spec, desiredLabels, strategy, dataImpact);

        return new UpdatePlan(
            planId: $"{Id}:update:{containerName}:{planHash[..12]}",
            planHash: planHash,
            provisionerId: Id,
            strategy: strategy,
            dataImpact: dataImpact,
            changes: changes,
            stages: stages,
            expiresAt: _timeProvider.GetUtcNow().AddMinutes(15));
    }

    /// <summary>
    /// The deliberate data-impact assertion, made from the live container's real mounts rather than from a
    /// default. Every branch is reachable and every branch is a claim this adapter can defend.
    /// </summary>
    private static DataImpact AssertDataImpact(
        UpdateStrategy strategy,
        IReadOnlyList<LiveMount> liveMounts,
        bool carriedOver)
    {
        if (strategy == UpdateStrategy.NoChangeRequired)
        {
            // Nothing would run, so nothing can happen to the data. This is the one place Preserved is safe
            // without inspecting mounts, because the container itself is not being touched.
            return DataImpact.Preserved;
        }

        if (liveMounts.Count == 0)
        {
            // Everything this container has written lives in its writable layer, and removing the container
            // removes that layer. There is nothing carried over because there is nothing to carry.
            return DataImpact.AtRisk;
        }

        // Preserved requires evidence: every mount the live container has must reappear in the replacement
        // at the same host path with the same access. A dropped or remapped mount leaves the bytes on the
        // host but brings the workload up on fresh state, which is not preservation.
        return carriedOver ? DataImpact.Preserved : DataImpact.AtRisk;
    }

    private static IReadOnlyList<ProvisioningStage> BuildRecreateStages(
        string containerName,
        string? liveImage,
        IReadOnlyList<LiveMount> liveMounts,
        bool carriedOver,
        DockerContainerSpec spec,
        IReadOnlyDictionary<string, string> desiredLabels,
        DataImpact dataImpact)
    {
        var imageClause = string.Equals(liveImage, spec.Image, StringComparison.Ordinal)
            ? $"image '{spec.Image}' (unchanged)"
            : $"image '{spec.Image}', replacing image '{liveImage ?? "(unknown)"}'";

        var published = spec.Ports.Where(p => p.HostPort is not null).ToList();

        var mountClause = spec.Volumes.Count == 0
            ? "The replacement declares no mounts, so nothing is reattached"
            : $"Reattach {spec.Volumes.Count} mount(s): {string.Join(", ", spec.Volumes.Select(v => $"{v.HostPath}=>{v.ContainerPath} ({(v.ReadWrite ? "rw" : "ro")})"))}";

        var carryClause = liveMounts.Count == 0
            ? "the existing container has no mounts, so its state lives in the writable layer being discarded"
            : carriedOver
                ? $"every one of the existing container's {liveMounts.Count} mount(s) is carried over unchanged"
                : $"NOT every one of the existing container's {liveMounts.Count} mount(s) is carried over: {string.Join(", ", liveMounts.Where(m => !IsCarriedOver(m, spec.Volumes)).Select(m => m.Describe()))}";

        return
        [
            new(
                "stop-container",
                Id,
                $"Stop container '{containerName}'. The workload is interrupted from here until the replacement starts; a container's image, ports and labels cannot be edited in place, so a replacement is the only route."),
            new(
                "remove-container",
                Id,
                $"Remove container '{containerName}' and its writable layer. Volumes are never removed with it — the removal call this provisioner issues sets RemoveVolumes to false — so its {liveMounts.Count} mount(s) survive the step."),
            new(
                "create-container",
                Id,
                $"Create replacement container '{containerName}' from {imageClause}, with {desiredLabels.Count} Servyx labels and {spec.Environment.Count} environment variable(s)."),
            new(
                "reattach-volumes",
                Id,
                $"{mountClause}. Data impact of this plan is {dataImpact}: {carryClause}."),
            new(
                "publish-ports",
                Id,
                published.Count == 0
                    ? "Expose no ports to the host; all declared ports stay container-internal."
                    : $"Publish {string.Join(", ", published.Select(p => $"{p.HostPort}->{p.ContainerPort}/{p.Protocol}"))} to the host."),
            new(
                "start-container",
                Id,
                $"Start the replacement container '{containerName}' and observe its assigned address. Its container id will differ from the one being replaced."),
        ];
    }

    /// <summary>A mount as the live container actually reports it.</summary>
    private readonly record struct LiveMount(string ContainerPath, string HostPath, bool ReadWrite)
    {
        internal string Describe() => $"{HostPath}=>{ContainerPath} ({(ReadWrite ? "rw" : "ro")})";
    }

    /// <summary>
    /// Reads the live container's mounts, preferring the engine's own <c>Mounts</c> view and falling back to
    /// parsing <c>HostConfig.Binds</c>. Both are read because they are populated by different engine
    /// versions and code paths, and a mount this method fails to see would be a mount the data-impact
    /// analysis silently ignores.
    /// </summary>
    private static IReadOnlyList<LiveMount> ReadLiveMounts(ContainerInspectResponse inspect)
    {
        var mounts = new List<LiveMount>();

        foreach (var mount in inspect.Mounts ?? [])
        {
            if (mount is null || string.IsNullOrWhiteSpace(mount.Destination))
            {
                continue;
            }

            mounts.Add(new LiveMount(mount.Destination, mount.Source ?? mount.Name ?? string.Empty, mount.RW));
        }

        if (mounts.Count > 0)
        {
            return mounts;
        }

        foreach (var bind in inspect.HostConfig?.Binds ?? [])
        {
            if (ParseBind(bind) is { } parsed)
            {
                mounts.Add(parsed);
            }
        }

        return mounts;
    }

    /// <summary>
    /// Parses a <c>host:container[:mode]</c> bind string. Splits from the right so a Windows host path's
    /// drive-letter colon is never mistaken for the separator.
    /// </summary>
    private static LiveMount? ParseBind(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return null;
        }

        var parts = bind.Split(':');
        var readWrite = true;
        var end = parts.Length;

        if (end >= 3 && (string.Equals(parts[^1], "ro", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[^1], "rw", StringComparison.OrdinalIgnoreCase)))
        {
            readWrite = string.Equals(parts[^1], "rw", StringComparison.OrdinalIgnoreCase);
            end--;
        }

        if (end < 2)
        {
            return null;
        }

        var containerPath = parts[end - 1];
        var hostPath = string.Join(':', parts[..(end - 1)]);

        return string.IsNullOrWhiteSpace(containerPath) ? null : new LiveMount(containerPath, hostPath, readWrite);
    }

    /// <summary>
    /// Whether every mount the live container has reappears in <paramref name="desired"/>. The container
    /// path is compared exactly (it is a POSIX path inside the image); the host path is compared
    /// case-insensitively because the host may be Windows, where two spellings name one directory.
    /// </summary>
    private static bool MountsCarriedOver(IReadOnlyList<LiveMount> live, IReadOnlyList<DockerVolumeMount> desired) =>
        live.Count > 0 && live.All(m => IsCarriedOver(m, desired));

    private static bool IsCarriedOver(LiveMount live, IReadOnlyList<DockerVolumeMount> desired) =>
        desired.Any(d =>
            string.Equals(d.ContainerPath, live.ContainerPath, StringComparison.Ordinal)
            && string.Equals(d.HostPath, live.HostPath, StringComparison.OrdinalIgnoreCase)
            && d.ReadWrite == live.ReadWrite);

    /// <summary>
    /// The live container's exposed and published ports, normalised into the same shape
    /// <see cref="DesiredPorts"/> produces so the two can be compared as sets rather than as API objects.
    /// </summary>
    private static IEnumerable<string> ReadLivePorts(ContainerInspectResponse inspect)
    {
        var bindings = inspect.HostConfig?.PortBindings;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var key in inspect.Config?.ExposedPorts?.Keys ?? [])
        {
            keys.Add(key);
        }

        foreach (var key in bindings?.Keys ?? [])
        {
            keys.Add(key);
        }

        foreach (var key in keys)
        {
            var hostPort = "-";
            if (bindings is not null && bindings.TryGetValue(key, out var bound) && bound is { Count: > 0 })
            {
                var first = bound[0]?.HostPort;
                hostPort = string.IsNullOrWhiteSpace(first) ? "-" : first;
            }

            yield return $"{key.ToLowerInvariant()}->{hostPort}";
        }
    }

    private static IEnumerable<string> DesiredPorts(DockerContainerSpec spec) =>
        spec.Ports.Select(p =>
            $"{FormatPortKey(p.ContainerPort, p.Protocol)}->{p.HostPort?.ToString(CultureInfo.InvariantCulture) ?? "-"}");

    private static string DescribePorts(IEnumerable<string> ports)
    {
        var ordered = ports.OrderBy(p => p, StringComparer.Ordinal).ToList();
        return ordered.Count == 0 ? "(none)" : string.Join(", ", ordered);
    }

    private static string ComputeUpdatePlanHash(
        string containerId,
        string? liveImage,
        string livePorts,
        IReadOnlyDictionary<string, string> liveLabels,
        DockerContainerSpec spec,
        IReadOnlyDictionary<string, string> desiredLabels,
        UpdateStrategy strategy,
        DataImpact dataImpact)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append(":update\n");
        builder.Append(containerId).Append('\n');
        builder.Append(liveImage ?? string.Empty).Append('\n');
        builder.Append(livePorts).Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"{strategy}/{dataImpact}\n");

        foreach (var label in liveLabels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"live-label {label.Key}={label.Value}\n");
        }

        builder.Append(ComputePlanHash(spec, desiredLabels)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<bool> RemoveContainerAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await _client.Containers
                .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static ResourceFacts BuildFacts(ContainerInspectResponse inspect)
    {
        var network = inspect.NetworkSettings;
        var privateAddress = network?.IPAddress;
        if (string.IsNullOrWhiteSpace(privateAddress))
        {
            privateAddress = network?.Networks?
                .Select(n => n.Value?.IPAddress)
                .FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip));
        }

        var createdAt = inspect.Created == default
            ? DateTimeOffset.UnixEpoch
            : new DateTimeOffset(inspect.Created.ToUniversalTime());

        return new ResourceFacts(
            PublicAddress: null,
            PrivateAddress: string.IsNullOrWhiteSpace(privateAddress) ? null : privateAddress,
            Cost: CostEstimate.Unknown(CostSource),
            CreatedAt: createdAt);
    }

    private static string ComputePlanHash(DockerContainerSpec spec, IReadOnlyDictionary<string, string> labels)
    {
        var builder = new StringBuilder();
        builder.Append(Id).Append('\n');
        builder.Append(spec.Image).Append('\n');
        builder.Append(spec.ContainerName).Append('\n');
        builder.Append(spec.RootPath).Append('\n');
        builder.Append(spec.RestartPolicy ?? string.Empty).Append('\n');

        foreach (var port in spec.Ports.OrderBy(p => p.ContainerPort).ThenBy(p => p.Protocol, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"port {port.ContainerPort}/{port.Protocol}->{port.HostPort?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        }

        foreach (var volume in spec.Volumes.OrderBy(v => v.ContainerPath, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"volume {volume.HostPath}=>{volume.ContainerPath} {(volume.ReadWrite ? "rw" : "ro")}\n");
        }

        foreach (var entry in spec.Environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"env {entry.Key}={entry.Value}\n");
        }

        foreach (var label in labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"label {label.Key}={label.Value}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string FormatPortKey(int containerPort, string protocol) =>
        string.Create(CultureInfo.InvariantCulture, $"{containerPort}/{protocol.ToLowerInvariant()}");

    private static RestartPolicyKind? ParseRestartPolicy(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => null,
        "no" => RestartPolicyKind.No,
        "always" => RestartPolicyKind.Always,
        "on-failure" or "onfailure" => RestartPolicyKind.OnFailure,
        "unless-stopped" or "unlessstopped" => RestartPolicyKind.UnlessStopped,
        _ => throw new ArgumentException($"Unrecognised Docker restart policy '{name}'.", nameof(name)),
    };

    private static DockerPortBinding ParsePort(string portKey, string hostPortValue)
    {
        var slash = portKey.IndexOf('/', StringComparison.Ordinal);
        var portText = slash < 0 ? portKey : portKey[..slash];
        var protocol = slash < 0 ? "tcp" : portKey[(slash + 1)..];

        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var containerPort))
        {
            throw new ArgumentException($"'{portKey}' is not a valid 'port:<number>[/<protocol>]' provisioning parameter key.", nameof(portKey));
        }

        int? hostPort = null;
        if (!string.IsNullOrWhiteSpace(hostPortValue))
        {
            if (!int.TryParse(hostPortValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedHostPort))
            {
                throw new ArgumentException($"'{hostPortValue}' is not a valid host port for '{portKey}'.", nameof(hostPortValue));
            }

            hostPort = parsedHostPort;
        }

        return new DockerPortBinding(containerPort, protocol.ToLowerInvariant(), hostPort);
    }

    private static DockerVolumeMount ParseVolume(string containerPath, string value)
    {
        var separator = value.LastIndexOf('|');
        if (separator < 0)
        {
            throw new ArgumentException($"Volume parameter for '{containerPath}' must be shaped '<hostPath>|rw' or '<hostPath>|ro'.", nameof(value));
        }

        var hostPath = value[..separator];
        var access = value[(separator + 1)..];

        return access.ToLowerInvariant() switch
        {
            "rw" => new DockerVolumeMount(hostPath, containerPath, ReadWrite: true),
            "ro" => new DockerVolumeMount(hostPath, containerPath, ReadWrite: false),
            _ => throw new ArgumentException($"Volume access for '{containerPath}' must be 'rw' or 'ro', not '{access}'.", nameof(value)),
        };
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Provisioning parameter '{key}' is required by the '{Id}' provisioner.", nameof(parameters));
        }

        return value;
    }

    private static Dictionary<string, string> ToOrdinalDictionary(IDictionary<string, string>? source) =>
        source is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(source, StringComparer.Ordinal);

    /// <summary>
    /// The mutating half of provisioning, kept off <see cref="IProvisioner"/> entirely and nested inside
    /// the provisioner so it — and only it — can reach
    /// <see cref="BuildCreateParameters"/>.
    /// </summary>
    private sealed class ContainerCreateOperation : IProvisioningOperation
    {
        private readonly DockerContainerProvisioner _owner;
        private readonly DockerContainerSpec _spec;
        private string? _createdContainerId;

        internal ContainerCreateOperation(DockerContainerProvisioner owner, DockerContainerSpec spec)
        {
            _owner = owner;
            _spec = spec;
        }

        public string ProvisionerId => Id;

        public string? Region => DockerContainerProvisioner.Region;

        public IReadOnlyDictionary<string, string> Tags => LabelsFor(_spec);

        public async Task<ProvisionedResource> CreateAsync(CancellationToken ct = default)
        {
            var created = await _owner._client.Containers
                .CreateContainerAsync(BuildCreateParameters(_spec), ct)
                .ConfigureAwait(false);

            _createdContainerId = created?.ID
                ?? throw new InvalidOperationException("The Docker Engine returned no container id from CreateContainerAsync.");

            await _owner._client.Containers
                .StartContainerAsync(_createdContainerId, new ContainerStartParameters(), ct)
                .ConfigureAwait(false);

            var inspect = await _owner._client.Containers
                .InspectContainerAsync(_createdContainerId, ct)
                .ConfigureAwait(false);

            var labels = inspect?.Config?.Labels is { Count: > 0 }
                ? ToOrdinalDictionary(inspect.Config.Labels)
                : new Dictionary<string, string>(Tags, StringComparer.Ordinal);

            return new ProvisionedResource(
                Handle: new ResourceHandle(Id, _createdContainerId, Region, labels),
                ConnectorId: _spec.Tags.ConnectorId,
                Target: _owner.BuildTargetDescriptor(_createdContainerId, _spec.ContainerName, _spec.RootPath),
                Facts: inspect is null
                    ? new ResourceFacts(null, null, CostEstimate.Unknown(CostSource), _owner._timeProvider.GetUtcNow())
                    : BuildFacts(inspect));
        }

        public async Task CompensateAsync(CancellationToken ct = default)
        {
            if (_createdContainerId is not null)
            {
                await _owner.RemoveContainerAsync(_createdContainerId, ct).ConfigureAwait(false);
                return;
            }

            // The create call never handed back an id, so the container may or may not exist. Ask the
            // engine by instance-id label rather than assuming nothing was created — assuming would leave
            // an orphan running with no local trace, which is precisely what the ledger exists to prevent.
            foreach (var orphan in await FindByInstanceIdAsync(ct).ConfigureAwait(false))
            {
                await _owner.RemoveContainerAsync(orphan, ct).ConfigureAwait(false);
            }
        }

        private async Task<IReadOnlyList<string>> FindByInstanceIdAsync(CancellationToken ct)
        {
            var parameters = new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [ServyxResourceTags.ManagedFilter] = true,
                        [$"{ServyxResourceTags.InstanceIdLabel}={_spec.Tags.InstanceId}"] = true,
                    },
                },
            };

            var containers = await _owner._client.Containers.ListContainersAsync(parameters, ct).ConfigureAwait(false);

            return (containers ?? [])
                .Where(c => ToOrdinalDictionary(c.Labels).TryGetValue(ServyxResourceTags.InstanceIdLabel, out var instanceId)
                    && string.Equals(instanceId, _spec.Tags.InstanceId, StringComparison.Ordinal))
                .Select(c => c.ID)
                .ToList();
        }
    }
}
