using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <para>
    /// The <see cref="ITransport"/> registered here is a <see cref="WriteGuardedTransport"/>, never a bare
    /// <see cref="DockerTransport"/>. That is the structural half of the write guard: no caller resolving
    /// <see cref="ITransport"/> from this container can obtain a session whose mutating members are not
    /// gated by the target's own <see cref="WriteMode"/>, because the concrete transport is not registered
    /// under any service type at all. <c>TransportWriteGuardArchitectureTests</c> asserts this for every
    /// Servyx transport registration.
    /// </para>
    /// <para>
    /// <b>This registration grants no write capability by itself.</b> The write mode comes from
    /// <see cref="IWriteModeResolver"/>, registered here via <c>TryAdd</c> as a
    /// <see cref="GrantedWriteModeResolver"/> over whatever <see cref="WriteModeGrant"/>s the composition
    /// root registered — of which a host that configures none has zero, making every target
    /// <see cref="WriteMode.ReadOnly"/>. Grants are resolved lazily, so a host may register them before or
    /// after this call, and each names a specific server: a grant that would enable writes for everything
    /// the daemon can see cannot be constructed (see <see cref="WriteModeGrant"/>).
    /// </para>
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

        services.TryAddSingleton<IWriteModeResolver>(sp =>
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        services.AddSingleton<ITransport>(sp =>
        {
            var writeModes = sp.GetRequiredService<IWriteModeResolver>();
            return new WriteGuardedTransport(
                new DockerTransport(
                    sp.GetRequiredService<IDockerClientFactory>(),
                    sp.GetRequiredService<IDockerEnvironment>(),
                    writeModes),
                writeModes);
        });

        services.AddSingleton<DockerServerDiscovery>();
        services.AddSingleton<IServerDiscovery>(sp => sp.GetRequiredService<DockerServerDiscovery>());
        services.AddSingleton<IMetricsSource, DockerMetricsSource>();
        services.AddSingleton<ILogStream, DockerLogStream>();

        return services;
    }
}
