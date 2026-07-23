using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Discovery;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Docker;

/// <summary>Dependency-injection registration for the Docker infrastructure implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Docker transport, execution target discovery, metrics, and log-stream services.
    /// </summary>
    /// <remarks>
    /// This milestone is read-only, so no write-guard decorator is registered here yet.
    /// // TODO(M4): Once Servyx.Application ships WriteGuardedExecutionTarget, every
    /// // IExecutionTarget produced by DockerTransport.ConnectAsync must be wrapped by it before
    /// // reaching any caller — no transport may be registered in DI without that decorator in front
    /// // of it. This extension currently registers ITransport directly; the wrapping needs to happen
    /// // either here (once the decorator type is available) or in whatever composition root resolves
    /// // ITransport, but it must happen before M4 ships any write path through this transport.
    /// </remarks>
    public static IServiceCollection AddServyxDocker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDockerEnvironment, SystemDockerEnvironment>();
        services.AddSingleton<IDockerClientFactory, DockerClientFactory>();

        services.AddSingleton<IDockerClient>(sp =>
        {
            var environment = sp.GetRequiredService<IDockerEnvironment>();
            var factory = sp.GetRequiredService<IDockerClientFactory>();
            var endpoint = DockerEndpointResolver.Resolve(explicitEndpoint: null, environment);
            return factory.Create(endpoint);
        });

        services.AddSingleton<ITransport, DockerTransport>();
        services.AddSingleton<DockerServerDiscovery>();
        services.AddSingleton<IServerDiscovery>(sp => sp.GetRequiredService<DockerServerDiscovery>());
        services.AddSingleton<IMetricsSource, DockerMetricsSource>();
        services.AddSingleton<ILogStream, DockerLogStream>();

        return services;
    }
}
