using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Backups;

namespace Servyx.Application.Backups;

/// <summary>Dependency-injection registration for the Application layer's backup surface.</summary>
public static class BackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IBackupDashboard"/> over whichever <see cref="IBackupProvider"/> is already in
    /// the container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not part of <c>AddServyxApplication()</c>.</strong> This registration is
    /// harmless on its own — with no provider registered the dashboard reports
    /// <see cref="IBackupDashboard.ProviderConfigured"/> as <see langword="false"/> and refuses every call
    /// — but it is the object a UI reaches mutating capability through, so a composition root that wants
    /// it says so in one line a reader can find. The capability itself still comes from
    /// <c>AddServyxDockerBackups()</c>.
    /// </para>
    /// <para>
    /// The provider is resolved with <c>GetService</c>, not <c>GetRequiredService</c>: a host that
    /// registered this without a provider gets a dashboard that says so, rather than a container that
    /// throws on the first page load.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    public static IServiceCollection AddServyxBackupDashboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBackupDashboard>(sp => new BackupDashboardService(sp.GetService<IBackupProvider>()));

        return services;
    }
}
