using System.Diagnostics;
using Docker.DotNet;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>
/// <see cref="ITransport"/> implementation reaching a workload through the Docker Engine API (local
/// Docker Desktop over its named pipe, or any daemon reachable via a Unix socket or TCP endpoint).
/// </summary>
/// <remarks>
/// This milestone is read-only: <see cref="Capabilities"/> deliberately omits
/// <see cref="TransportCapabilities.ExecuteCommand"/>, <see cref="TransportCapabilities.StreamOutput"/>,
/// <see cref="TransportCapabilities.StreamStdin"/>, <see cref="TransportCapabilities.FileWrite"/>, and
/// <see cref="TransportCapabilities.PortForward"/> — none of those are genuinely implemented yet.
/// <c>docker exec</c>-based command execution lands in M2.
/// </remarks>
public sealed class DockerTransport : ITransport
{
    private readonly IDockerClientFactory _clientFactory;
    private readonly IDockerEnvironment _environment;

    /// <summary>Creates a <see cref="DockerTransport"/>, optionally substituting its client factory and environment for testing.</summary>
    public DockerTransport(IDockerClientFactory? clientFactory = null, IDockerEnvironment? environment = null)
    {
        _clientFactory = clientFactory ?? new DockerClientFactory();
        _environment = environment ?? new SystemDockerEnvironment();
    }

    /// <inheritdoc />
    public string TransportId => "docker";

    /// <inheritdoc />
    public TransportCapabilities Capabilities =>
        TransportCapabilities.FileRead | TransportCapabilities.DirectoryList | TransportCapabilities.ContainerApi;

    /// <inheritdoc />
    public async Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var stopwatch = Stopwatch.StartNew();
        IDockerClient? client = null;
        try
        {
            var endpoint = DockerEndpointResolver.Resolve(target, _environment);
            client = _clientFactory.Create(endpoint);

            var version = await client.System.GetVersionAsync(ct).ConfigureAwait(false);
            stopwatch.Stop();

            var detail = $"Docker {version.Version} (API {version.APIVersion}) on {version.Os}/{version.Arch}, kernel {version.KernelVersion}";
            return new TargetHealth(true, stopwatch.Elapsed, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TargetHealth(false, null, $"Docker engine unreachable: {ex.Message}");
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// This deliberately creates a fresh <see cref="IDockerClient"/> scoped to the resolved
    /// <paramref name="target"/> rather than reusing a shared instance: a <see cref="TargetDescriptor"/>
    /// can point at any endpoint (a remote host, a different Docker context), so the client's lifetime
    /// must match the session's, and <see cref="DockerExecutionTarget"/> disposes it when the session
    /// ends (<c>ownsClient: true</c>). This is unlike <see cref="DockerMetricsSource"/> and
    /// <see cref="DockerLogStream"/>, which are registered against the single DI-provided
    /// <see cref="IDockerClient"/> for the one local daemon this milestone manages — those services have
    /// no notion of "target" to scope a client to, since <c>IMetricsSource</c>/<c>ILogStream</c>
    /// address a server by id, not a <see cref="TargetDescriptor"/>.
    /// </remarks>
    public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();

        var endpoint = DockerEndpointResolver.Resolve(target, _environment);
        var client = _clientFactory.Create(endpoint);
        var containerRef = ResolveContainerRef(target);
        var containerRootPath = ResolveContainerRootPath(target);

        IExecutionTarget executionTarget = new DockerExecutionTarget(client, containerRef, containerRootPath, ownsClient: true);
        return Task.FromResult(executionTarget);
    }

    /// <summary>
    /// Determines which container a <see cref="TargetDescriptor"/> refers to. By convention this reads
    /// <see cref="TargetDescriptor.Options"/>' <c>"containerId"</c> key first, then <c>"containerName"</c>,
    /// then the generic <c>"container"</c> key.
    /// </summary>
    internal static string ResolveContainerRef(TargetDescriptor target)
    {
        if (target.Options.TryGetValue("containerId", out var id) && !string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        if (target.Options.TryGetValue("containerName", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (target.Options.TryGetValue("container", out var generic) && !string.IsNullOrWhiteSpace(generic))
        {
            return generic;
        }

        throw new ArgumentException(
            "TargetDescriptor.Options must specify 'containerId', 'containerName', or 'container' for the Docker transport to connect.",
            nameof(target));
    }

    /// <summary>
    /// The absolute in-container path that <see cref="TargetPath"/> values passed to the resulting
    /// <see cref="IExecutionTarget"/> are relative to, read from <see cref="TargetDescriptor.Options"/>'
    /// <c>"rootPath"</c> key. Defaults to <c>/</c> when absent.
    /// </summary>
    internal static string ResolveContainerRootPath(TargetDescriptor target) =>
        target.Options.TryGetValue("rootPath", out var rootPath) && !string.IsNullOrWhiteSpace(rootPath)
            ? rootPath
            : "/";
}
