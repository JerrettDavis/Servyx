using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process;

/// <summary>Dependency-injection registration for the local-process infrastructure implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LocalProcessTransport"/> as an <see cref="ITransport"/>.
    /// </summary>
    /// <remarks>
    /// Registers the transport only. Nothing registered here can install or remove anything: reads, directory
    /// listings, and command execution against an already-configured target are all a transport does. Making
    /// installation reachable is a separate, explicit decision — see
    /// <see cref="Provisioning.ProcessProvisioningServiceCollectionExtensions.AddServyxProcessProvisioning"/>,
    /// which mirrors the same split the SSH project draws between <c>AddServyxSsh()</c> and
    /// <c>AddServyxSshProvisioning()</c>.
    /// </remarks>
    public static IServiceCollection AddServyxLocalProcess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Constructed explicitly rather than by type: LocalProcessTransport's only constructor parameter is an
        // optional TimeSpan the container has no way to supply meaningfully.
        services.AddSingleton<ITransport>(_ => new LocalProcessTransport());

        return services;
    }
}
