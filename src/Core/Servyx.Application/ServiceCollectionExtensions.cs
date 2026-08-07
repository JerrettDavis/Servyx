using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servyx.Application.Servers;
using Servyx.Domain.Definitions;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Application;

/// <summary>Dependency-injection registration for the Application layer's use cases.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IServerQueryService"/> in single-criteria mode: every discovered server is
    /// governed by <paramref name="criteria"/>. Used when exactly one game definition loaded — see
    /// <c>Servyx.Web</c>'s definition bootstrap — so the single-definition case keeps this exact
    /// construction path, byte-for-byte, regardless of how many definitions the multi-definition overload
    /// below handles.
    /// </summary>
    /// <param name="criteria">
    /// The adoption criteria (image repository, required mount path) servers are matched against, derived
    /// from the one loaded definition's docker deployment profile — see <see cref="AdoptionCriteriaFactory"/>.
    /// There is deliberately no hardcoded fallback here any more: a caller with no definition to derive
    /// criteria from should call the <see cref="AddServyxApplication(IServiceCollection, IReadOnlyList{DefinitionAdoptionCriteria})"/>
    /// overload instead, with whatever criteria set it does have (possibly empty).
    /// </param>
    public static IServiceCollection AddServyxApplication(this IServiceCollection services, AdoptionCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(criteria);

        services.AddSingleton(criteria);
        services.AddSingleton<IServerQueryService, ServerQueryService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IServerQueryService"/> in multi-definition mode: each discovered server is
    /// independently resolved to its own governing definition out of <paramref name="criteriaSet"/> via
    /// <see cref="ServerBindingResolver"/>, rather than every server sharing one ambient criteria/settings/
    /// lifecycle. Used whenever the loaded definition count is not exactly one — most notably zero
    /// definitions, where <paramref name="criteriaSet"/> is empty and adoption honestly matches nothing,
    /// rather than falling back to any hardcoded default game.
    /// </summary>
    /// <param name="criteriaSet">One <see cref="DefinitionAdoptionCriteria"/> per loaded definition with a derivable docker profile. May be empty.</param>
    /// <remarks>
    /// Requires <see cref="IBoundDefinitionLookup"/> to already be registered — <c>Servyx.Web</c>'s
    /// composition root supplies the real implementation, backed by <c>GameDefinitionCatalog</c>, since
    /// that type lives in <c>Servyx.Definitions</c> and this project deliberately does not reference it (see
    /// <see cref="IBoundDefinitionLookup"/>'s own remarks). <see cref="IServerDefinitionBindingStore"/> is
    /// resolved optionally: with none registered, bindings are re-resolved fresh on every call rather than
    /// anchored across restarts.
    /// </remarks>
    public static IServiceCollection AddServyxApplication(
        this IServiceCollection services, IReadOnlyList<DefinitionAdoptionCriteria> criteriaSet)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(criteriaSet);

        services.AddSingleton(criteriaSet);
        services.AddSingleton<IServerQueryService>(sp => new ServerQueryService(
            sp.GetRequiredService<IServerDiscovery>(),
            sp.GetRequiredService<IMetricsSource>(),
            sp.GetRequiredService<ILogStream>(),
            sp.GetRequiredService<ITransport>(),
            criteriaSet,
            sp.GetRequiredService<IBoundDefinitionLookup>(),
            sp.GetRequiredService<ILogger<ServerQueryService>>(),
            sp.GetService<IServerDefinitionBindingStore>()));

        return services;
    }
}
