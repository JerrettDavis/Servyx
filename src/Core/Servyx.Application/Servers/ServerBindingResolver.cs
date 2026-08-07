using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;

namespace Servyx.Application.Servers;

/// <summary>Whether a single discovered server's fresh match against the loaded criteria set was decisive.</summary>
public enum ServerMatchState
{
    /// <summary>Exactly one definition's criteria matched, or one won unambiguously on specificity.</summary>
    Bound,

    /// <summary>Two or more definitions matched with equal specificity — see <see cref="ServerBindingResolver"/>'s remarks.</summary>
    Ambiguous,
}

/// <summary>A single discovered server's fresh match result against a <see cref="DefinitionAdoptionCriteria"/> set.</summary>
/// <param name="Server">The discovered server.</param>
/// <param name="State">Whether resolution was decisive.</param>
/// <param name="Definition">The single winning definition, when <see cref="State"/> is <see cref="ServerMatchState.Bound"/>; otherwise <see langword="null"/>.</param>
/// <param name="Candidates">Every definition that matched with equal, top specificity — one entry when <see cref="ServerMatchState.Bound"/>, two or more when <see cref="ServerMatchState.Ambiguous"/>.</param>
public sealed record ServerMatchResult(
    DiscoveredServer Server,
    ServerMatchState State,
    GameDefinitionRef? Definition,
    IReadOnlyList<GameDefinitionRef> Candidates);

/// <summary>
/// Resolves which of several loaded game definitions' <see cref="AdoptionCriteria"/> governs each
/// discovered server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discovery fan-out, not a union filter.</strong> <see cref="IServerDiscovery.DiscoverAsync"/>
/// takes exactly one image-repository/required-mount pair and is not being changed by this feature (see the
/// interface's own remarks) — a single call cannot express "any of these N criteria". This resolver instead
/// calls it once per distinct (image repository, required mount) pair across the whole criteria set
/// (criteria sharing an identical pair are deduplicated into one discovery call, since they would return
/// identical results), then reconciles the per-call results against every criteria set that produced a
/// match for a given server. This keeps <see cref="IServerDiscovery"/>'s contract untouched and its
/// existing per-call filtering doing all the heavy lifting; the fan-out cost is one discovery call per
/// distinct detect rule, which is small (bounded by the number of loaded definitions' docker profiles, not
/// by the number of containers on the host).
/// </para>
/// <para>
/// <strong>Most-specific-wins, then explicit ambiguity.</strong> A server can satisfy more than one
/// criteria set — most commonly because two definitions declare an identical (image repository, required
/// mount) detect rule, but also because a container simply has more than one of several definitions'
/// required mounts present. Ties are broken by the longest matching
/// <see cref="AdoptionCriteria.RequiredMountContainerPath"/> — image repository is not a usable tie-break
/// signal because every candidate for a given server was already discovered under its own criteria's exact
/// image repository (the docker/SSH discovery implementations require exact equality after stripping
/// tag/digest), so all candidates for one server always share an identical image repository. If more than
/// one distinct definition remains tied
/// after the mount tie-break, resolution deliberately stops rather than picking arbitrarily — the caller
/// gets <see cref="ServerMatchState.Ambiguous"/> naming every tied candidate, never a silently mislabelled
/// server.
/// </para>
/// </remarks>
public static class ServerBindingResolver
{
    /// <summary>
    /// Fans discovery out across every distinct detect rule in <paramref name="criteriaSet"/> and resolves
    /// each discovered server to at most one governing definition. A discovery call that throws for one
    /// detect rule is logged and treated as "no matches for that rule" — it does not prevent other rules'
    /// results from being resolved, since one malformed or unreachable-image definition must not blind
    /// adoption to every other game.
    /// </summary>
    public static async Task<IReadOnlyList<ServerMatchResult>> ResolveAsync(
        IServerDiscovery discovery,
        IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(criteriaSet);
        ArgumentNullException.ThrowIfNull(logger);

        var matchesByServerId = new Dictionary<string, (DiscoveredServer Server, List<DefinitionAdoptionCriteria> Matches)>(
            StringComparer.Ordinal);

        var dedupedRules = criteriaSet.GroupBy(c => (c.Criteria.ImageRepository, c.Criteria.RequiredMountContainerPath));

        foreach (var rule in dedupedRules)
        {
            var (imageRepo, mount) = rule.Key;
            IReadOnlyList<DiscoveredServer> discovered;
            try
            {
                discovered = await discovery.DiscoverAsync(imageRepo, mount, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to discover servers for image repository '{ImageRepository}' / mount '{Mount}'; "
                    + "the definition(s) sharing that detect rule contribute no matches this round.",
                    imageRepo,
                    mount);
                continue;
            }

            var ruleCriteria = rule.ToList();
            foreach (var server in discovered)
            {
                if (!matchesByServerId.TryGetValue(server.ServerId, out var entry))
                {
                    entry = (server, []);
                    matchesByServerId[server.ServerId] = entry;
                }

                entry.Matches.AddRange(ruleCriteria);
            }
        }

        var results = new List<ServerMatchResult>(matchesByServerId.Count);
        foreach (var (server, matches) in matchesByServerId.Values)
        {
            results.Add(Resolve(server, matches));
        }

        return results;
    }

    private static ServerMatchResult Resolve(DiscoveredServer server, List<DefinitionAdoptionCriteria> matches)
    {
        // Specificity is decided by required-mount length alone, not image repository: every entry in
        // `matches` reached this server via a discovery call keyed on that criteria's own
        // ImageRepository (see ResolveAsync above), and DockerServerDiscovery/SshDockerServerDiscovery's
        // ImageRepositoryMatches requires exact equality after stripping tag/digest — so all candidates
        // for one server necessarily share an identical ImageRepository already. An image-length tie-break
        // would be a no-op on every real input; it previously existed here and was removed as dead code.
        var maxMountLength = matches.Max(m => m.Criteria.RequiredMountContainerPath.Length);
        var topByMount = matches.Where(m => m.Criteria.RequiredMountContainerPath.Length == maxMountLength).ToList();

        var distinctDefinitions = topByMount
            .Select(m => m.DefinitionRef)
            .Distinct()
            .ToList();

        return distinctDefinitions.Count == 1
            ? new ServerMatchResult(server, ServerMatchState.Bound, distinctDefinitions[0], distinctDefinitions)
            : new ServerMatchResult(server, ServerMatchState.Ambiguous, null, distinctDefinitions);
    }
}
