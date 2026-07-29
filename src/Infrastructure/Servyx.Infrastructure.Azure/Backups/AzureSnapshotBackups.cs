using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// Opt-in composition for Azure managed-disk snapshot backups — the one entry point a host has to name before
/// any of this capability is reachable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a factory and not an <c>IServiceCollection</c> extension.</strong> The Docker and SSH backup
/// providers are registered by <c>AddServyxDockerBackups()</c> and <c>AddServyxSshBackups()</c>. This project
/// has <em>no</em> <c>PackageReference</c> at all — deliberately, see the .csproj — and that includes
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>, which is what makes it an adapter that can
/// carry no logger and therefore has no reachable path that could write an Azure client secret or an access
/// token. Taking that dependency to gain one registration line would trade a real security property for a
/// syntactic convenience, so the opt-in is a factory instead — exactly as it is for
/// <c>DigitalOceanSnapshotBackups</c> and <c>EbsSnapshotBackups</c>.
/// </para>
/// <para>
/// <strong>This registers mutating, billable capability.</strong> Taking a snapshot set starts a charge that
/// recurs per GB-month until something deletes it; pruning deletes ARM resources irreversibly. A composition
/// root that wants either has to say so here, in one line a reader can find without tracing a dependency graph.
/// Milestone 1 hosts must not call it, and a host with <c>Servyx:Provisioning:Enabled</c> unset never reaches
/// it — nothing in this repository calls this method outside its tests, so with the flag off the behaviour of
/// the process is unchanged in the strongest sense: the type is never constructed and no token is ever
/// exchanged.
/// </para>
/// <para>
/// <strong>What it does NOT register is a restore path</strong>, because there is not one. Restoring from a
/// managed-disk snapshot creates a new managed disk that must then be attached, and swapping an OS disk means
/// deallocating the machine — see
/// <see cref="AzureSnapshotBackupProvider.RestoreAsync(string, System.Threading.CancellationToken)"/>. A host
/// that registers this provider gets create, list, inspect, plan-restore and prune; a restore attempt through
/// the interface refuses with a message describing the real procedure.
/// </para>
/// <para>
/// <strong>No <see cref="IBackupAdopter"/> is registered, because there is nothing to adopt.</strong> An
/// adopter's job is to discover backups a workload's own mechanism made, so they can be surfaced as
/// <see cref="BackupOwnership.Foreign"/>. Here that discovery is not a separate mechanism: every snapshot in the
/// machine's resource group is already visible to <see cref="AzureSnapshotBackupProvider.ListAsync"/>, and the
/// ones Servyx did not create are labelled foreign by <see cref="AzureSnapshotOwnership.Classify"/> as they are
/// read.
/// </para>
/// <para>
/// Requires an <see cref="IAzureSnapshotContextSource"/> from the host. That is deliberately not defaulted:
/// mapping a Servyx server to a resource group and a virtual machine name is knowledge only the composition
/// root has, and a plausible-looking default would snapshot the wrong machine and — far worse — would make
/// another machine's snapshots look prunable.
/// </para>
/// </remarks>
public static class AzureSnapshotBackups
{
    /// <summary>
    /// Builds a managed-disk-snapshot-backed <see cref="IBackupProvider"/> for one Azure subscription.
    /// </summary>
    /// <param name="httpClient">The HTTP client the API calls go out on.</param>
    /// <param name="secretStore">Where the service principal's client secret lives. Resolved per token exchange.</param>
    /// <param name="servicePrincipal">The identity to authenticate as. Carries only the secret's URN.</param>
    /// <param name="subscriptionId">The subscription the machines and their snapshots live in.</param>
    /// <param name="contexts">Maps a Servyx server id to the virtual machine that backs it.</param>
    /// <param name="timeProvider">Clock used for backup set naming and poll pacing.</param>
    /// <returns>
    /// The provider, typed as the concrete class rather than <see cref="IBackupProvider"/>, so a host can also
    /// reach <see cref="AzureSnapshotBackupProvider.EstimateStorageCeilingAsync"/> — the one member not on the
    /// interface, and the only way to ask what a server's snapshots are costing.
    /// </returns>
    public static AzureSnapshotBackupProvider Create(
        HttpClient httpClient,
        ISecretStore secretStore,
        AzureServicePrincipal servicePrincipal,
        string subscriptionId,
        IAzureSnapshotContextSource contexts,
        TimeProvider? timeProvider = null) =>
        new(httpClient, secretStore, servicePrincipal, subscriptionId, contexts, timeProvider);
}
