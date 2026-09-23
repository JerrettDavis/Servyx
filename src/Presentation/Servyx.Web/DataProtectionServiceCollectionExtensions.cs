using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Servyx.Web;

/// <summary>
/// Registers the ASP.NET Core web host's own Data Protection key ring — the one antiforgery tokens and the
/// operator auth cookie actually go through — with durable file-system persistence.
/// </summary>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers Data Protection with a file-system key ring under <paramref name="keyRingDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Not to be confused with <c>DataProtectionSecretStore</c>'s private, standalone Data Protection
    /// container for encrypted secrets. Left unconfigured, ASP.NET Core falls back to an ephemeral,
    /// non-persisted key ring on Linux; every container recreation (a deploy, or an automatic Watchtower
    /// update) then silently invalidates every outstanding antiforgery token and session cookie until the
    /// operator reloads the page.
    /// </remarks>
    /// <param name="services">The container to register into.</param>
    /// <param name="keyRingDirectory">
    /// The directory the key ring is persisted to. Defaults to a <c>dataprotection-keys</c> subdirectory of
    /// <c>AppContext.BaseDirectory/servyx-data</c> — the same durable volume, and the same
    /// <c>AppContext.BaseDirectory/servyx-data</c> convention, <c>SecretsOptions</c> already uses.
    /// </param>
    public static IServiceCollection AddServyxWebHostDataProtection(
        this IServiceCollection services,
        string? keyRingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var directory = string.IsNullOrWhiteSpace(keyRingDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "servyx-data", "dataprotection-keys")
            : keyRingDirectory;

        services.AddDataProtection()
            .SetApplicationName("Servyx")
            .PersistKeysToFileSystem(new DirectoryInfo(directory));

        return services;
    }
}
