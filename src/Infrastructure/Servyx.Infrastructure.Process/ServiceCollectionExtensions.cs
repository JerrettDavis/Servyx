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
        //
        // KNOWN GAP (M4): this is the one Servyx transport still registered without a WriteGuardedTransport in
        // front of it, and LocalExecutionTarget's writes are real, so a caller who resolves ITransport here
        // gets an unguarded write path. It is named explicitly in TransportWriteGuardArchitectureTests'
        // exemption list, which fails the moment a *new* transport joins it. Closing it means changing
        // LocalProcessTransportTests and ProvisionedLocalTargetHandoffTests, which pin both this registration's
        // concrete type and ConnectAsync's concrete return type — an M8 change (bare process hosts), not one
        // M4 may make on its way past.
        services.AddSingleton<ITransport>(_ => new LocalProcessTransport());

        return services;
    }
}
