using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>Dependency-injection registration for the built-in configuration adapters, codecs, and merger.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DotEnvConfigAdapter"/>, <see cref="IniConfigAdapter"/>,
    /// <see cref="PropertiesConfigAdapter"/>, and <see cref="JsonConfigAdapter"/> (all as
    /// <see cref="IConfigAdapter"/>, resolvable via
    /// <see cref="IConfigAdapter.FormatId"/>), <see cref="UnrealOptionSettingsCodec"/> (as
    /// <see cref="IConfigValueCodec"/>, resolvable via <see cref="IConfigValueCodec.CodecId"/>), and
    /// <see cref="ConfigMerger"/> as <see cref="IConfigMerger"/>.
    /// </summary>
    public static IServiceCollection AddServyxConfig(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConfigAdapter, DotEnvConfigAdapter>();
        services.AddSingleton<IConfigAdapter, IniConfigAdapter>();
        services.AddSingleton<IConfigAdapter, PropertiesConfigAdapter>();
        services.AddSingleton<IConfigAdapter, JsonConfigAdapter>();
        services.AddSingleton<IConfigValueCodec, UnrealOptionSettingsCodec>();
        services.AddSingleton<IConfigMerger, ConfigMerger>();

        return services;
    }
}
