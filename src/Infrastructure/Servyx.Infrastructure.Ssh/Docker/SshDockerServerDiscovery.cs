using Servyx.Domain.Discovery;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>
/// <see cref="IServerDiscovery"/> implementation for the ssh+docker transport: discovers candidate
/// containers by running <c>docker</c> CLI commands over an existing SSH exec channel and parsing their
/// JSON output, rather than talking to a Docker Engine API endpoint directly.
/// </summary>
/// <remarks>
/// <para>
/// Takes an already-connected <see cref="IExecutionTarget"/> (an SSH session), mirroring how
/// <c>DockerServerDiscovery</c> takes a persistent <c>IDockerClient</c>: both are held for the lifetime of
/// this instance and reused across calls, rather than reconnecting per call. The transport-plus-descriptor
/// shape (<c>ITransport</c> + <c>TargetDescriptor</c>) was considered, but it would force this class to
/// call <see cref="ITransport.ConnectAsync"/> (and dispose the resulting session) on every
/// <see cref="DiscoverAsync"/> call, when in practice a caller already holds one connected session per
/// managed server and passes it to every read surface — the same session <see cref="SshDockerTransport"/>
/// hands back unwrapped from its own <c>ConnectAsync</c>.
/// </para>
/// <para>
/// Adoption criteria are mirrored exactly from <c>DockerServerDiscovery.Matches</c>: a container's image
/// repository (ignoring tag/digest) must equal <c>imageRepository</c>, AND it must have a mount whose
/// container-side destination equals <c>requiredMountContainerPath</c>. Unlike the Docker.DotNet-backed
/// implementation — whose <c>ContainerListResponse</c> already carries full mount objects (source and
/// destination) from a single list call — <c>docker container ls</c>'s <c>Mounts</c> format field is only
/// a comma-separated list of mount sources/volume names, with no destination. So this class first
/// cheaply filters the list by image repository, then inspects each remaining candidate (which is the
/// only way to learn a mount's destination over the CLI) to apply the mount criterion and build the full
/// <see cref="DiscoveredServer"/> result.
/// </para>
/// </remarks>
public sealed class SshDockerServerDiscovery : IServerDiscovery
{
    private readonly IExecutionTarget _target;

    /// <summary>Creates a discovery service operating against an already-connected SSH session.</summary>
    public SshDockerServerDiscovery(IExecutionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// <c>docker container ls</c> or <c>docker container inspect</c> exited non-zero. Never swallowed:
    /// a failed list/inspect call surfaces loudly rather than silently reporting no candidates, so a
    /// broken SSH/docker path on a remote host cannot masquerade as "nothing to adopt".
    /// </exception>
    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        string imageRepository,
        string requiredMountContainerPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredMountContainerPath);

        var listResult = await _target.ExecuteAsync(DockerCli.Ps(), ct).ConfigureAwait(false);
        EnsureSucceeded(listResult, "docker container ls");

        var entries = DockerInspectJson.ParseContainerList(listResult.StandardOutput);

        var results = new List<DiscoveredServer>();
        foreach (var entry in entries)
        {
            if (entry.Id.Length == 0 || !DockerInspectJson.ImageRepositoryMatches(entry.Image, imageRepository))
            {
                continue;
            }

            var inspectResult = await _target.ExecuteAsync(DockerCli.Inspect(entry.Id), ct).ConfigureAwait(false);
            EnsureSucceeded(inspectResult, $"docker container inspect {entry.Id}");

            var server = DockerInspectJson.ParseInspect(inspectResult.StandardOutput);

            var hasRequiredMount = server.Mounts.Any(
                m => string.Equals(m.Destination, requiredMountContainerPath, StringComparison.Ordinal));
            if (!hasRequiredMount)
            {
                continue;
            }

            results.Add(server);
        }

        return results;
    }

    private static void EnsureSucceeded(CommandResult result, string description)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"'{description}' failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }
}
