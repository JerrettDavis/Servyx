using Microsoft.Extensions.DependencyInjection;
using Servyx.Application.Servers;

namespace Servyx.Application;

/// <summary>Dependency-injection registration for the Application layer's use cases.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IServerQueryService"/>. Consumes whichever <c>Servyx.Domain</c>
    /// abstractions (<c>IServerDiscovery</c>, <c>IMetricsSource</c>, <c>ILogStream</c>,
    /// <c>ITransport</c>) are already registered in DI — typically by an infrastructure project's own
    /// <c>AddServyxXxx()</c> extension (e.g. <c>AddServyxDocker()</c>) — without this project ever
    /// referencing that infrastructure project directly.
    /// </summary>
    /// <param name="criteria">
    /// The adoption criteria (image repository, required mount path) servers are matched against. When
    /// omitted, defaults to <see cref="AdoptionCriteria.PalworldDefault"/>, this milestone's single
    /// supported deployment. A caller that has parsed a game definition itself (see
    /// <c>Servyx.Web</c>'s definition loader) should pass the criteria it derived instead.
    /// </param>
    public static IServiceCollection AddServyxApplication(this IServiceCollection services, AdoptionCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(criteria ?? AdoptionCriteria.PalworldDefault);
        services.AddSingleton<IServerQueryService, ServerQueryService>();

        return services;
    }
}
