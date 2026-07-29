using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <para>
    /// The registered <see cref="ITransport"/> is a <see cref="WriteGuardedTransport"/>, not a bare
    /// <see cref="SshTransport"/>: no Servyx transport may reach a caller without the write guard in front
    /// of it, and <c>TransportWriteGuardArchitectureTests</c> asserts it. SFTP writes are genuinely
    /// implemented here (unlike Docker's, which arrived only in M4), so without this the SSH transport
    /// would be the one hole in the guarantee. Write posture comes from <see cref="WriteModeGrant"/>s the
    /// composition root registers; <c>AddServyxSshProvisioning</c> registers one for the single endpoint it
    /// was configured with, which is how provisioning keeps its marker-file writes.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxSsh(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWriteModeResolver>(sp =>
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        services.AddSingleton<ITransport>(sp => new WriteGuardedTransport(
            ActivatorUtilities.CreateInstance<SshTransport>(sp),
            sp.GetRequiredService<IWriteModeResolver>()));

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
