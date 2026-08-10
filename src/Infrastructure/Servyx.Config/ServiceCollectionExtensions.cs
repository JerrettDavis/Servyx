using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>Dependency-injection registration for the built-in configuration adapters, codecs, and merger.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DotEnvConfigAdapter"/>, <see cref="IniConfigAdapter"/>,
    /// <see cref="PropertiesConfigAdapter"/>, <see cref="JsonConfigAdapter"/>, and
    /// <see cref="YamlConfigAdapter"/> (all as
    /// <see cref="IConfigAdapter"/>, resolvable via
    /// <see cref="IConfigAdapter.FormatId"/>), <see cref="UnrealOptionSettingsCodec"/> (as
    /// <see cref="IConfigValueCodec"/>, resolvable via <see cref="IConfigValueCodec.CodecId"/>),
    /// <see cref="ConfigMerger"/> as <see cref="IConfigMerger"/>, <see cref="SurfaceResolver"/> as
    /// <see cref="ISurfaceResolver"/>, and <see cref="SettingStateResolverFactory"/> as
    /// <see cref="ISettingStateResolverFactory"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IServerConfigSessionSource"/> is registered the same way, and for the same reason: a
    /// factory needs one to construct, but only a composition root knows which container and which host
    /// directory a given server's surfaces live on. <see cref="UnconfiguredServerConfigSessionSource"/>
    /// opens no session for any server, so every setting resolves to "unreadable, and here is why" until a
    /// host registers a real one.
    /// <para>
    /// <see cref="ISurfaceResolutionContextSource"/> is registered with
    /// <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService, TImplementation}(IServiceCollection)"/>
    /// rather than <c>AddSingleton</c>: <see cref="SurfaceResolver"/> needs one to construct at all, but the
    /// per-server deployment facts it supplies are owned by the composition root, not by this package. The
    /// <see cref="UnconfiguredSurfaceResolutionContextSource"/> placeholder keeps the container valid on its
    /// own and reports honestly that no server is known; a real source registered by the host takes
    /// precedence.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxConfig(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConfigAdapter, DotEnvConfigAdapter>();
        services.AddSingleton<IConfigAdapter, IniConfigAdapter>();
        services.AddSingleton<IConfigAdapter, PropertiesConfigAdapter>();
        services.AddSingleton<IConfigAdapter, JsonConfigAdapter>();
        services.AddSingleton<IConfigAdapter, YamlConfigAdapter>();
        services.AddSingleton<IConfigValueCodec, UnrealOptionSettingsCodec>();
        services.AddSingleton<IConfigMerger, ConfigMerger>();
        services.TryAddSingleton<ISurfaceResolutionContextSource, UnconfiguredSurfaceResolutionContextSource>();
        services.AddSingleton<ISurfaceResolver, SurfaceResolver>();
        services.TryAddSingleton<IServerConfigSessionSource, UnconfiguredServerConfigSessionSource>();
        services.AddSingleton<ISettingStateResolverFactory, SettingStateResolverFactory>();

        return services;
    }
}
