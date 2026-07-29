using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Process.Backups;

/// <summary>
/// Opt-in dependency-injection registration for local-process <em>backups</em>.
/// </summary>
public static class LocalProcessBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LocalProcessBackupProvider"/> as an <see cref="IBackupProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxLocalProcess()</c> registers only the transport, and
    /// this file is not reachable from it — the same split
    /// <c>AddServyxProcessProvisioning()</c>, <c>AddServyxSshBackups()</c> and <c>AddServyxDockerBackups()</c>
    /// draw. Creating a backup writes an archive onto the machine the panel itself runs on, restoring one
    /// overwrites live save data on that machine, and pruning deletes archives; a composition root that wants
    /// any of that has to say so here, in one line a reader can find without tracing a dependency graph.
    /// Milestone 1 hosts must not call it, and a host with <c>Servyx:Provisioning:Enabled</c> unset never
    /// reaches it.
    /// </para>
    /// <para>
    /// <strong>No adopter is registered, because this project ships none.</strong>
    /// <c>AddServyxDockerBackups()</c> registers <c>PalworldCronBackupAdopter</c> only because the Palworld
    /// image genuinely ships a cron job whose output is knowable in advance. A bare machine has no such
    /// convention, and an adopter that guessed which of the operator's tarballs were backups would be worse
    /// than none. A host that knows its own layout may register its own <see cref="IBackupAdopter"/>; the
    /// provider consults every registered one whose <see cref="IBackupAdopter.Supports"/> accepts the
    /// context's deployment kind, and surfaces what they find as <see cref="BackupOwnership.Foreign"/> —
    /// listable and inspectable, never pruned.
    /// </para>
    /// <para>
    /// Requires an <see cref="ILocalBackupContextSource"/> to be registered by the composition root. That is
    /// deliberately not defaulted here: turning a server id into an installed data directory and a
    /// definition's substituted backup block is knowledge only the host has, and a plausible-looking default
    /// would silently back up the wrong paths — or, worse, write archives into the directory it is archiving.
    /// </para>
    /// <para>
    /// It also requires the server's target to be write-enabled through a <c>WriteModeGrant</c>, exactly as
    /// local provisioning does. Without one, <see cref="LocalProcessBackupProvider.CreateAsync"/>,
    /// <see cref="LocalProcessBackupProvider.RestoreAsync"/> and a live
    /// <see cref="LocalProcessBackupProvider.PruneAsync"/> refuse up front with
    /// <see cref="Domain.Transport.WritesDisabledException"/> while listing, inspecting, previewing and
    /// dry-run pruning keep working.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    public static IServiceCollection AddServyxLocalProcessBackups(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IBackupProvider>(sp => new LocalProcessBackupProvider(
            sp.GetRequiredService<ILocalBackupContextSource>(),
            sp.GetServices<IBackupAdopter>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
