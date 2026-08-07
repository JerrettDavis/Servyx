using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Definitions;

namespace Servyx.Definitions;

/// <summary>Dependency-injection registration for the filesystem-backed game-definition catalog.</summary>
/// <remarks>
/// <strong>Called from <c>Program.cs</c>.</strong> The composition root registers this catalog and then
/// immediately replaces its (lazily-constructed, still-empty) <see cref="IGameDefinitionProvider"/>/
/// <see cref="GameDefinitionCatalog"/> instances with an already-populated pair built from one synchronous
/// bootstrap-time refresh — see <c>Program.cs</c>'s own remarks on the "Game definition catalog" block for
/// why that extra step exists. The original hardcoded loader this catalog replaced as the production source
/// of adoption criteria, RCON commands, and lifecycle data has since been deleted outright.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The directory <see cref="FileSystemGameDefinitionProvider"/> enumerates. Defaults to
    /// <c>{AppContext.BaseDirectory}/definitions</c> when unset.
    /// </summary>
    public const string PathConfigKey = "Servyx:Definitions:Path";

    /// <summary>
    /// Whether <see cref="DefinitionCatalogRefreshService"/> subscribes to hot reload after its initial
    /// refresh. Defaults to <see langword="true"/> in the Development environment (per
    /// <c>ASPNETCORE_ENVIRONMENT</c>/<c>DOTNET_ENVIRONMENT</c>) and <see langword="false"/> otherwise.
    /// </summary>
    public const string WatchConfigKey = "Servyx:Definitions:Watch";

    /// <summary>
    /// Registers <see cref="FileSystemGameDefinitionProvider"/> (as <see cref="IGameDefinitionProvider"/>),
    /// <see cref="GameDefinitionCatalog"/> (also exposed as <see cref="IDefinitionCatalogDiagnostics"/>),
    /// and <see cref="DefinitionCatalogRefreshService"/> as a hosted service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PathConfigKey"/> and <see cref="WatchConfigKey"/> are read directly off
    /// <paramref name="configuration"/> at registration time (not bound through an options pattern), the
    /// same way most of this codebase's other <c>Servyx:*</c> keys are — see e.g.
    /// <c>Program.cs</c>'s reads of <c>Servyx:Secrets:RootDirectory</c>.
    /// </para>
    /// <para>
    /// This method has no <c>IHostEnvironment</c> parameter, so the Development default for
    /// <see cref="WatchConfigKey"/> is read from the <c>DOTNET_ENVIRONMENT</c>/<c>ASPNETCORE_ENVIRONMENT</c>
    /// environment variables directly rather than from <c>IHostEnvironment.EnvironmentName</c> — the same
    /// value ASP.NET Core itself derives <c>IHostEnvironment</c> from before the host is built, so this
    /// agrees with it in every normal deployment.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">Configuration to read <see cref="PathConfigKey"/> and <see cref="WatchConfigKey"/> from.</param>
    public static IServiceCollection AddServyxDefinitions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var root = configuration[PathConfigKey];
        var watch = ResolveWatch(configuration[WatchConfigKey]);

        // keep in sync with Program.cs's own bootstrap-time FileSystemGameDefinitionProvider construction,
        // which hardcodes trustEvaluator: null instead of resolving one from DI — equivalent today, since
        // nothing registers an IDefinitionTrustEvaluator, but this is a second place to update once trust
        // evaluation ships.
        services.AddSingleton<IGameDefinitionProvider>(sp => new FileSystemGameDefinitionProvider(
            root,
            sp.GetService<IDefinitionTrustEvaluator>(),
            sp.GetService<ILogger<FileSystemGameDefinitionProvider>>()));

        services.AddSingleton(sp => new GameDefinitionCatalog(
            sp.GetServices<IGameDefinitionProvider>(),
            sp.GetService<ILogger<GameDefinitionCatalog>>()));

        services.TryAddSingleton<IDefinitionCatalogDiagnostics>(sp => sp.GetRequiredService<GameDefinitionCatalog>());

        services.AddHostedService(sp => new DefinitionCatalogRefreshService(
            sp.GetRequiredService<GameDefinitionCatalog>(),
            sp.GetServices<IGameDefinitionProvider>(),
            watch,
            sp.GetService<ILogger<DefinitionCatalogRefreshService>>()));

        return services;
    }

    private static bool ResolveWatch(string? configuredValue)
    {
        if (bool.TryParse(configuredValue, out var explicitValue))
        {
            return explicitValue;
        }

        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
