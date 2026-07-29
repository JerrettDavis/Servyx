using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servyx.Domain.Connectors;
using Servyx.Domain.Rcon;

namespace Servyx.Infrastructure.Rcon;

/// <summary>Opt-in dependency-injection registration for the Source RCON protocol client.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SourceRconClient"/> as both <see cref="IRconClient"/> and
    /// <see cref="ISecretAwareRconClient"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This registers a protocol client, not a capability.</strong> A client on its own cannot do
    /// anything: it has no endpoint, no credential and no command catalogue. Composing those into an
    /// <see cref="IRconSession"/> — and deciding which server's write mode guards it — is the composition
    /// root's job, for the same reason <c>IDockerBackupContextSource</c> is: turning a server id into a
    /// host, a port, a secret URN and a definition's command block is host knowledge, and a plausible
    /// default here would point the control channel at the wrong server.
    /// </para>
    /// <para>
    /// Deliberately does not register an <see cref="IRconReachability"/>. The definition declares an ordered
    /// list of strategies per channel, and which of them exist is a per-deployment fact rather than a
    /// package-level one.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="timeouts">The connector-style timeout policy; defaults to <see cref="TimeoutPolicy.Default"/>.</param>
    public static IServiceCollection AddServyxRcon(this IServiceCollection services, TimeoutPolicy? timeouts = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ => new SourceRconClient(timeouts));
        services.TryAddSingleton<ISecretAwareRconClient>(sp => sp.GetRequiredService<SourceRconClient>());
        services.TryAddSingleton<IRconClient>(sp => sp.GetRequiredService<SourceRconClient>());

        return services;
    }
}
