using Servyx.Application.Lifecycle;
using Servyx.Application.Servers;
using Servyx.Domain.Lifecycle;

namespace Servyx.Web.Services;

/// <summary>
/// Thin <see cref="IContainerStateProbe"/> adapter over <see cref="IServerQueryService"/> — the same
/// transport-agnostic read path the rest of the dashboard uses.
/// </summary>
/// <remarks>
/// <see cref="ServerLifecycleService"/>'s stop ladder polls this between escalation stages to learn
/// whether the container has exited yet. Building the adapter over <see cref="IServerQueryService"/>
/// rather than a Docker-specific client (e.g. <c>IDockerClient</c>) keeps it working unchanged whichever
/// transport <c>Program.cs</c> composed — local Docker or ssh+docker — exactly as
/// <see cref="IServerQueryService"/>'s own contract promises for every other read in this codebase.
/// </remarks>
public sealed class ServerQueryContainerStateProbe : IContainerStateProbe
{
    private readonly IServerQueryService _query;

    /// <summary>Creates a probe over <paramref name="query"/>.</summary>
    public ServerQueryContainerStateProbe(IServerQueryService query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A server that is no longer found (removed, or the daemon unreachable) is reported as
    /// <see langword="true"/> — already exited — so a stop ladder waiting between stages does not spin
    /// forever polling a container that has vanished mid-wait.
    /// </remarks>
    public async Task<ContainerStateSnapshot> GetStateAsync(string containerRef, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerRef);

        var detail = await _query.GetServerDetailAsync(containerRef, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return new ContainerStateSnapshot(Exited: true, State: "not-found");
        }

        var exited = detail.Summary.State is not (ServerState.Running or ServerState.Starting or ServerState.Stopping);
        return new ContainerStateSnapshot(Exited: exited, State: detail.Summary.State.ToString());
    }
}
