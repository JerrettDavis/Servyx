using Servyx.Domain.Configuration;
using Servyx.Domain.Definitions.Model;
using GameDefinition = Servyx.Domain.Definitions.Model.GameDefinition;

namespace Servyx.Composition;

/// <summary>
/// Answers which game definition governs a server, and what settings it declares, for
/// <c>PlanExecutor</c>'s use.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A leaf by construction, and that is the whole point.</strong> This type holds one already-loaded
/// <see cref="GameDefinition"/> and reads fields off it. It resolves nothing from the service provider, opens
/// no session, and — critically — never touches <c>IServerQueryService</c>. That service optionally consumes
/// <see cref="ISettingStateResolverFactory"/>, which consumes <see cref="IServerConfigSessionSource"/>, which
/// <c>PlanExecutor</c> also consumes; all of them are singletons, so a plan-catalogue lookup that routed back
/// through the query service could be asking an instance already executing further up the same call stack and
/// would deadlock against a memoized task rather than fail. See
/// <see cref="ServyxSurfaceResolutionContextSource"/>'s own remarks for the incident that established this,
/// and <c>AddServyxCoreSettingStateReentrancyTests</c> — the regression that proved it — in the Web test
/// project.
/// </para>
/// <para>
/// <strong>Single-definition scoped, exactly like the surface source next to it.</strong> The
/// <see cref="GameDefinition"/> supplied here is the composition root's <c>singleDefinition</c>: non-null only
/// when exactly one definition is loaded, the same rule the RCON, backup and surface-resolution wiring already
/// applies. With zero or several definitions loaded this source answers <see langword="null"/> for every
/// server, and a preview then refuses loudly rather than planning against a guessed catalogue — which is the
/// safe direction, because the wrong catalogue would name real bindings on real surfaces.
/// </para>
/// <para>
/// <strong>The server id is not validated against discovery here.</strong> Whether the id names a real
/// container is answered downstream, by <see cref="IServerConfigSessionSource"/> (no sessions, so every
/// surface is unreachable and every change blocked with a reason) and by <see cref="IServerSettingsService"/>
/// (no tracked server row, so a plan cannot be recorded at all). Repeating the discovery round trip here would
/// add a second way for the same question to be answered differently, and would put this type back on the
/// layer it deliberately sits below.
/// </para>
/// </remarks>
public sealed class ServyxServerPlanCatalogSource : IServerPlanCatalogSource
{
    private readonly ServerPlanCatalog? _catalog;

    /// <summary>Creates the source over the single loaded definition, if there is one.</summary>
    /// <param name="definition">
    /// The single loaded game definition, or <see langword="null"/> when zero or more than one is loaded.
    /// </param>
    /// <param name="definitionVersion">
    /// The definition's content hash, recorded on every plan so a later apply can detect the definition
    /// changing underneath it. Falls back to the definition's declared <c>metadata.version</c> when no content
    /// hash is supplied — never to a placeholder, because a version column that always reads the same value
    /// would make the drift check silently vacuous.
    /// </param>
    public ServyxServerPlanCatalogSource(GameDefinition? definition, string? definitionVersion = null)
    {
        if (definition is null)
        {
            _catalog = null;
            return;
        }

        _catalog = new ServerPlanCatalog(
            definition.Metadata.Id,
            string.IsNullOrWhiteSpace(definitionVersion) ? definition.Metadata.Version : definitionVersion,

            // Flattened out of the definition's SettingGroups, matching SettingStateScope.Settings — the
            // catalogue is a flat keyed namespace, and groups exist only for display.
            [.. definition.Settings.SelectMany(group => group.Items)]);
    }

    /// <inheritdoc />
    public Task<ServerPlanCatalog?> GetAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        return Task.FromResult(_catalog);
    }
}
