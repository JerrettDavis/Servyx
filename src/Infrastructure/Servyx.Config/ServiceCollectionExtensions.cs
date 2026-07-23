using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Configuration;

namespace Servyx.Config;

/// <summary>Dependency-injection registration for the built-in configuration adapters, codecs, and merger.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DotEnvConfigAdapter"/> and <see cref="IniConfigAdapter"/> (both as
    /// <see cref="IConfigAdapter"/>, resolvable via <see cref="IConfigAdapter.FormatId"/>),
    /// <see cref="UnrealOptionSettingsCodec"/> (as <see cref="IConfigValueCodec"/>, resolvable via
    /// <see cref="IConfigValueCodec.CodecId"/>), and <see cref="ConfigMerger"/> as <see cref="IConfigMerger"/>.
    /// </summary>
    public static IServiceCollection AddServyxConfig(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConfigAdapter, DotEnvConfigAdapter>();
        services.AddSingleton<IConfigAdapter, IniConfigAdapter>();
        services.AddSingleton<IConfigValueCodec, UnrealOptionSettingsCodec>();
        services.AddSingleton<IConfigMerger, ConfigMerger>();

        return services;
    }
}
