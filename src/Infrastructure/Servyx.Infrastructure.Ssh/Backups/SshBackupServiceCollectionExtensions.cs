using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Ssh.Backups;

/// <summary>
/// Opt-in dependency-injection registration for SSH-backed <em>backups</em>.
/// </summary>
public static class SshBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SshBackupProvider"/> as an <see cref="IBackupProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxSsh()</c> registers only the transport and the
    /// connector pool, and this file is not reachable from it. Creating a backup runs <c>tar</c> on someone's
    /// machine, restoring one overwrites live game files, and pruning deletes archives; a composition root
    /// that wants any of that has to say so here, in one line a reader can find without tracing a dependency
    /// graph. Milestone 1 hosts must not call it, and a host with <c>Servyx:Provisioning:Enabled</c> unset
    /// never reaches it.
    /// </para>
    /// <para>
    /// <strong>No adopter is registered, because this project ships none.</strong>
    /// <c>AddServyxDockerBackups()</c> registers <c>PalworldCronBackupAdopter</c> only because the Palworld
    /// image genuinely ships a cron job whose output is knowable in advance. A generic SSH host has no such
    /// convention, and an adopter that guessed which of a stranger's tarballs were backups would be worse
    /// than none. A host that knows its own layout may register its own <see cref="IBackupAdopter"/>; the
    /// provider consults every registered one whose <see cref="IBackupAdopter.Supports"/> accepts the
    /// context's deployment kind, and surfaces what they find as
    /// <see cref="BackupOwnership.Foreign"/> — listable and inspectable, never pruned.
    /// </para>
    /// <para>
    /// Requires an <see cref="ISshBackupContextSource"/> to be registered by the composition root. That is
    /// deliberately not defaulted here: turning a server id into a connected host, a data root, and a
    /// definition's substituted backup block is knowledge only the host has, and a plausible-looking default
    /// would silently back up the wrong paths — or, worse, write archives into the directory it is archiving.
    /// </para>
    /// <para>
    /// It also requires the server's target to be write-enabled through a <c>WriteModeGrant</c>, exactly as
    /// SSH provisioning does. Without one, <see cref="SshBackupProvider.CreateAsync"/> and
    /// <see cref="SshBackupProvider.RestoreAsync"/> refuse up front with
    /// <see cref="Domain.Transport.WritesDisabledException"/> while listing, inspecting, previewing and
    /// dry-run pruning keep working.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    public static IServiceCollection AddServyxSshBackups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBackupProvider>(sp => new SshBackupProvider(
            sp.GetRequiredService<ISshBackupContextSource>(),
            sp.GetServices<IBackupAdopter>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
