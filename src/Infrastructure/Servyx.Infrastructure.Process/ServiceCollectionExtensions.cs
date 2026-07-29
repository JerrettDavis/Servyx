using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process;

/// <summary>Dependency-injection registration for the local-process infrastructure implementations.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LocalProcessTransport"/>, behind a <see cref="WriteGuardedTransport"/>, as an
    /// <see cref="ITransport"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers the transport only. Nothing registered here can install or remove anything: reads, directory
    /// listings, and command execution against an already-configured target are all a transport does. Making
    /// installation reachable is a separate, explicit decision — see
    /// <see cref="Provisioning.ProcessProvisioningServiceCollectionExtensions.AddServyxProcessProvisioning"/>,
    /// which mirrors the same split the SSH project draws between <c>AddServyxSsh()</c> and
    /// <c>AddServyxSshProvisioning()</c>.
    /// </para>
    /// <para>
    /// The <see cref="ITransport"/> registered here is a <see cref="WriteGuardedTransport"/>, never a bare
    /// <see cref="LocalProcessTransport"/> — the same shape <c>AddServyxDocker()</c> and <c>AddServyxSsh()</c>
    /// have. That is the structural half of the write guard: no caller resolving <see cref="ITransport"/>
    /// from this container can obtain a session whose mutating members are not gated by the target's own
    /// <see cref="WriteMode"/>, because the concrete transport is not registered under any service type at
    /// all. <c>TransportWriteGuardArchitectureTests</c> asserts this for every Servyx transport registration,
    /// and — since this was the last exemption — its exemption list is now empty.
    /// </para>
    /// <para>
    /// <b>This registration grants no write capability by itself.</b> The write mode comes from
    /// <see cref="IWriteModeResolver"/>, registered here via <c>TryAdd</c> as a
    /// <see cref="GrantedWriteModeResolver"/> over whatever <see cref="WriteModeGrant"/>s the composition
    /// root registered — of which a host that configures none has zero, making every target
    /// <see cref="WriteMode.ReadOnly"/>. <c>AddServyxProcessProvisioning()</c> registers one, scoped to the
    /// single machine endpoint it was configured with, which is how provisioning keeps its marker writes.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxLocalProcess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWriteModeResolver>(sp =>
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        // The inner transport is constructed explicitly rather than by type: LocalProcessTransport's only
        // constructor parameter is an optional TimeSpan the container has no way to supply meaningfully.
        services.AddSingleton<ITransport>(sp => new WriteGuardedTransport(
            new LocalProcessTransport(),
            sp.GetRequiredService<IWriteModeResolver>()));

        return services;
    }
}
