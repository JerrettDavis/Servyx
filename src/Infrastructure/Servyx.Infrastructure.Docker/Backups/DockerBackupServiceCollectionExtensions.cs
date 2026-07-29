using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Docker.Backups;

/// <summary>
/// Opt-in dependency-injection registration for Docker-backed <em>backups</em>.
/// </summary>
public static class DockerBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DockerBackupProvider"/> as an <see cref="IBackupProvider"/> and
    /// <see cref="PalworldCronBackupAdopter"/> as an <see cref="IBackupAdopter"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxDocker()</c> registers only read-only Docker
    /// services and is untouched by this file. Creating a backup writes an archive, restoring one
    /// overwrites live files, and pruning deletes archives; a composition root that wants any of that has
    /// to say so here, in one line a reader can find without tracing a dependency graph. Milestone 1 hosts
    /// must not call it.
    /// </para>
    /// <para>
    /// The adopter is registered alongside the provider rather than separately, because the provider's
    /// <c>ListAsync</c> is what surfaces foreign archives and it does so through the registered adopters.
    /// Registering the adopter grants no mutating capability of its own — see
    /// <see cref="PalworldCronBackupAdopter"/>, which reads directory listings and nothing else — but it is
    /// pointless without a provider to consult it.
    /// </para>
    /// <para>
    /// Requires an <see cref="IDockerBackupContextSource"/> to be registered by the composition root. That
    /// is deliberately not defaulted here: turning a server id into an execution target, a data directory,
    /// and a definition's substituted <c>backup:</c> block is knowledge only the host has, and a
    /// plausible-looking default would silently back up the wrong paths.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    public static IServiceCollection AddServyxDockerBackups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBackupAdopter, PalworldCronBackupAdopter>());
        services.AddSingleton<IBackupProvider>(sp => new DockerBackupProvider(
            sp.GetRequiredService<IDockerBackupContextSource>(),
            sp.GetServices<IBackupAdopter>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
