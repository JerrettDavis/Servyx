using Servyx.Application.Servers;
using Servyx.Domain.Configuration;
using Servyx.Domain.Discovery;

namespace Servyx.Composition;

/// <summary>
/// The <see cref="IMirrorTargetRunStateSource"/> the settings pipeline uses: a thin read over
/// <see cref="IServerDiscovery"/>, which already reports every candidate container's
/// <see cref="DiscoveredServer.State"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discovery, NOT <c>IServerQueryService</c> — and that is a correctness requirement, not a layering
/// preference.</strong> This is the same rule, and the same deadlock, that
/// <see cref="ServyxSurfaceResolutionContextSource"/> and <see cref="ServyxServerPlanCatalogSource"/> already
/// document at length: the query service optionally consumes the settings pipeline, this type is consumed by
/// it, and all three are singletons — so reaching up would have <c>PlanExecutor.PreviewAsync</c> awaiting a
/// task whose completion depends on itself. Silent, permanent, and invisible to every catch block, because a
/// deadlocked task never throws. <see cref="ServerQueryContainerStateProbe"/> is the adapter that DOES route
/// through the query service, and is why it cannot be reused here despite answering a nearly identical
/// question for the lifecycle stop ladder.
/// </para>
/// <para>
/// <strong>Every failure is "unknown", never an exception and never a guess.</strong> A daemon that is down,
/// a container that has been removed, a definition this host cannot derive adoption criteria for — all of
/// them return <see langword="null"/>, which <c>PlanExecutor</c> treats as a reason to block the mirrored
/// action with a hint. Preview runs on every settings page load; throwing out of it would take the page down
/// over an optional half of an optional feature.
/// </para>
/// <para>
/// <strong>Read-only, and it authorises nothing.</strong> The write grant, the transport write guard and
/// every capability check are untouched and still run. The worst a wrong answer here can do is cost an
/// operator a mirrored action that would have worked; it can never let one through that should not have.
/// </para>
/// </remarks>
public sealed class DiscoveryMirrorTargetRunStateSource : IMirrorTargetRunStateSource
{
    /// <summary>
    /// Docker's own words for a container that is up. <c>restarting</c> is deliberately excluded: a container
    /// mid-restart may accept an exec now and be gone a second later, and a mirrored write finalized by a
    /// rename issued through exec is exactly the operation that must not be attempted against a moving target.
    /// </summary>
    private static readonly IReadOnlySet<string> RunningStates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running" };

    private readonly IServerDiscovery _discovery;
    private readonly AdoptionCriteria? _criteria;

    /// <summary>Creates the source.</summary>
    /// <param name="discovery">Lists candidate containers and their reported run state.</param>
    /// <param name="criteria">
    /// The single loaded definition's adoption criteria, or null when none is derivable — in which case no
    /// container is discoverable at all and every answer is honestly "unknown".
    /// </param>
    public DiscoveryMirrorTargetRunStateSource(IServerDiscovery discovery, AdoptionCriteria? criteria)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        _discovery = discovery;
        _criteria = criteria;
    }

    /// <inheritdoc />
    public async Task<MirrorTargetRunState?> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        if (_criteria is null)
        {
            return null;
        }

        IReadOnlyList<DiscoveredServer> candidates;
        try
        {
            candidates = await _discovery
                .DiscoverAsync(_criteria.ImageRepository, _criteria.RequiredMountContainerPath, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        // Id first, then name — the same two-step ServyxSurfaceResolutionContextSource uses, so a server the
        // settings pipeline can open sessions for is one this can also answer for.
        var container =
            candidates.FirstOrDefault(s => string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => string.Equals(s.Name, serverId, StringComparison.OrdinalIgnoreCase));

        // No matching container is "unknown", not "stopped". They lead to different operator advice — go
        // start the server, versus go find out why Servyx cannot see it — and asserting the wrong one sends
        // an operator to press a button that will not help.
        return container is null
            ? null
            : new MirrorTargetRunState(RunningStates.Contains(container.State), container.State);
    }
}
