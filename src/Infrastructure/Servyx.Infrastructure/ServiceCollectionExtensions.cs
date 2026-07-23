using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Connectors;
using Servyx.Domain.Secrets;
using Servyx.Infrastructure.Connectors;
using Servyx.Infrastructure.Secrets;

namespace Servyx.Infrastructure;

/// <summary>Dependency-injection registration for the built-in secret store and host key trust services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DataProtectionSecretStore"/> as <see cref="ISecretStore"/>,
    /// <see cref="FileHostKeyStore"/> as <see cref="IHostKeyStore"/>, and <see cref="HostKeyVerifier"/> as
    /// <see cref="IHostKeyVerifier"/>, all as singletons configured by <paramref name="configure"/>.
    /// </summary>
    public static IServiceCollection AddServyxSecrets(this IServiceCollection services, Action<SecretsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SecretsOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<ISecretStore>(_ => new DataProtectionSecretStore(options));
        services.AddSingleton<IHostKeyStore>(_ => new FileHostKeyStore(options.HostKeyStoreFilePath));
        services.AddSingleton<IHostKeyVerifier, HostKeyVerifier>();

        return services;
    }
}
