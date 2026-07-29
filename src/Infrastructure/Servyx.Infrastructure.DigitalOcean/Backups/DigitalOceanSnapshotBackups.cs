using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.DigitalOcean.Backups;

/// <summary>
/// Opt-in composition for DigitalOcean snapshot backups — the one entry point a host has to name before any
/// of this capability is reachable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a factory and not an <c>IServiceCollection</c> extension.</strong> The Docker and SSH backup
/// providers are registered by <c>AddServyxDockerBackups()</c> and <c>AddServyxSshBackups()</c>. This project
/// has <em>no</em> <c>PackageReference</c> at all — deliberately, see the .csproj — and that includes
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>, which is what makes it the one adapter that
/// can carry no logger and therefore has no reachable path that could log the API token. Taking that
/// dependency to gain one registration line would trade a real security property for a syntactic convenience,
/// so the opt-in is a factory instead: a host that wants snapshot backups calls
/// <see cref="Create"/> in its own registration and hands the result to its container.
/// </para>
/// <para>
/// <strong>This registers mutating, billable capability.</strong> Taking a snapshot starts a charge that
/// recurs per GB-month until something deletes it; restoring from one replaces a droplet's boot disk; pruning
/// deletes snapshots irreversibly. A composition root that wants any of that has to say so here, in one line
/// a reader can find without tracing a dependency graph. Milestone 1 hosts must not call it, and a host with
/// <c>Servyx:Provisioning:Enabled</c> unset never reaches it — nothing in this repository calls this method
/// outside its tests, so with the flag off the behaviour of the process is unchanged in the strongest sense:
/// the type is never constructed.
/// </para>
/// <para>
/// <strong>No <see cref="IBackupAdopter"/> is registered, because there is nothing to adopt.</strong> An
/// adopter's job is to discover backups a workload's own mechanism made, so they can be surfaced as
/// <see cref="BackupOwnership.Foreign"/>. Here that discovery is not a separate mechanism at all: every
/// snapshot in the account is already visible to <see cref="DigitalOceanSnapshotBackupProvider.ListAsync"/>,
/// and the ones Servyx did not create are labelled foreign by <see cref="SnapshotOwnership.Classify"/> as
/// they are read. Foreign snapshots are therefore listed, inspectable and restorable without any adopter, and
/// are never pruned.
/// </para>
/// <para>
/// Requires an <see cref="IDigitalOceanSnapshotContextSource"/> from the host. That is deliberately not
/// defaulted: mapping a Servyx server to a droplet id is knowledge only the composition root has, and a
/// plausible-looking default would snapshot — or, far worse, restore over — the wrong machine.
/// </para>
/// </remarks>
public static class DigitalOceanSnapshotBackups
{
    /// <summary>
    /// Builds a snapshot-backed <see cref="IBackupProvider"/> for one DigitalOcean account.
    /// </summary>
    /// <param name="http">The HTTP client the API calls go out on.</param>
    /// <param name="secretStore">Where the DigitalOcean personal access token lives.</param>
    /// <param name="apiTokenUrn">The URN the token is stored at. Resolved per request and never cached.</param>
    /// <param name="contexts">Maps a Servyx server id to the droplet that backs it.</param>
    /// <param name="timeProvider">Clock used for snapshot naming and restore-plan expiry.</param>
    /// <returns>
    /// The provider, typed as the concrete class rather than <see cref="IBackupProvider"/>: the acknowledging
    /// restore overload is not on the interface, and a host that registers only the interface has registered
    /// a provider whose restore always refuses. Both are legitimate — refusing is the safe default — so the
    /// choice is left to the caller rather than made silently here.
    /// </returns>
    public static DigitalOceanSnapshotBackupProvider Create(
        HttpClient http,
        ISecretStore secretStore,
        SecretUrn apiTokenUrn,
        IDigitalOceanSnapshotContextSource contexts,
        TimeProvider? timeProvider = null) =>
        new(http, secretStore, apiTokenUrn, contexts, timeProvider);
}
