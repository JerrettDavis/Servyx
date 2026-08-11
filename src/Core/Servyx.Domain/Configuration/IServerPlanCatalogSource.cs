using Servyx.Domain.Definitions.Model;

namespace Servyx.Domain.Configuration;

/// <summary>
/// The definition-level facts <see cref="IPlanExecutor.PreviewAsync"/> needs about one server: which
/// definition governs it, at what version, and what settings that definition declares.
/// </summary>
/// <param name="DefinitionId">
/// The governing definition's <c>metadata.id</c>. Recorded on the plan row so a later apply can detect the
/// definition changing underneath the plan — see <see cref="Servyx.Domain.Entities.ChangePlanRecord.DefinitionId"/>.
/// </param>
/// <param name="DefinitionVersion">
/// The definition's content hash or version at preview time, for the same drift check.
/// </param>
/// <param name="Settings">
/// The governing definition's settings catalogue, flattened out of its <see cref="SettingGroup"/>s — the same
/// list <see cref="SettingStateScope.Settings"/> carries. A desired key not in this list cannot be planned,
/// and is reported as a <see cref="BlockedChange"/> rather than ignored.
/// </param>
public sealed record ServerPlanCatalog(
    string DefinitionId,
    string DefinitionVersion,
    IReadOnlyList<SettingDescriptor> Settings);

/// <summary>
/// Supplies the per-server <see cref="ServerPlanCatalog"/> the plan previewer resolves setting keys against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The sibling of <see cref="IServerConfigSessionSource"/>, and split from it for the same reason it
/// is split from <see cref="ISurfaceResolutionContextSource"/>.</strong> The planning <em>rules</em> — collect
/// every write binding, refuse a derived surface, derive consequences from regeneration triggers — are
/// game- and deployment-agnostic and live in one place. Which definition governs a given server, and what
/// that definition says, is a fact owned by whichever composition root loaded the definition catalogue.
/// </para>
/// <para>
/// <strong>This interface exists specifically so the previewer never reaches for
/// <c>IServerQueryService</c>.</strong> That service optionally consumes
/// <see cref="ISettingStateResolverFactory"/>, which consumes <see cref="IServerConfigSessionSource"/>; all of
/// them are singletons, so a previewer that asked the query service "what definition governs this server"
/// could be asking an instance already executing further up its own call stack. The memoized
/// <see cref="Lazy{T}"/> such sources use publishes its task at the first await, so the re-entrant call does
/// not recurse and fail loudly — it receives the pending task the outer frame is awaiting, and the two wait
/// on each other forever. Deferring the lookup behind a <c>Func</c> does not help; the cycle is at call time,
/// not construction time. An implementation of this interface must therefore be a leaf that reads the
/// definition catalogue and nothing that can route back into the settings pipeline. See
/// <c>ServyxSurfaceResolutionContextSource</c>'s own remarks for the incident that established this rule.
/// </para>
/// </remarks>
public interface IServerPlanCatalogSource
{
    /// <summary>
    /// Returns the governing definition's planning facts for <paramref name="serverId"/>, or
    /// <see langword="null"/> when no definition is known to govern it.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is a supported answer, not an error, matching
    /// <see cref="IServerConfigSessionSource.GetAsync"/>: a server whose definition cannot be resolved yields
    /// a plan in which every requested change is blocked with a reason, rather than an exception out of a
    /// page load.
    /// </remarks>
    /// <param name="serverId">The server whose governing definition is being asked about.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ServerPlanCatalog?> GetAsync(string serverId, CancellationToken ct = default);
}
