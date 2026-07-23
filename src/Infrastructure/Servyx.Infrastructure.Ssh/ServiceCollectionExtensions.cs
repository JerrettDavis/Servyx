using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Connectors;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh;

/// <summary>Dependency-injection registration for the SSH infrastructure implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SshTransport"/> as an <see cref="ITransport"/> and <see cref="ConnectorPool"/>
    /// as the <see cref="IConnectorPool"/>.
    /// </summary>
    /// <remarks>
    /// Does not register an <see cref="IHostKeyVerifier"/>, <see cref="Domain.Secrets.ISecretStore"/>, or
    /// <see cref="IHostKeyStore"/> — those are cross-cutting services provided by
    /// <c>Servyx.Infrastructure</c> (see <c>src/Infrastructure/Servyx.Infrastructure/Connectors</c>), which
    /// this project depends on transitively through <c>Servyx.Domain</c> only, not directly; callers must
    /// register those separately (or via that project's own DI extension) before resolving
    /// <see cref="ITransport"/>/<see cref="IConnectorPool"/> here.
    /// </remarks>
    public static IServiceCollection AddServyxSsh(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ITransport, SshTransport>();

        services.AddSingleton<IConnectorPool>(sp => new ConnectorPool(
            connectorFactory: (key, _) => throw new InvalidOperationException(
                "No connector registry is wired up: ConnectorPool needs a way to map a ConnectorKey back to " +
                "the ConnectorDescriptor (and credentials) that produced it. Register IConnectorPool yourself " +
                "with a real connectorFactory once an application-layer connector registry exists, or replace " +
                "this registration."),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<ConnectorPool>>()));

        return services;
    }
}
