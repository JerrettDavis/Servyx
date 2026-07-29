using Servyx.Domain.Backups;
using Servyx.Domain.Secrets;

namespace Servyx.Infrastructure.Aws.Backups;

/// <summary>
/// Opt-in composition for AWS EBS snapshot backups — the one entry point a host has to name before any of this
/// capability is reachable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a factory and not an <c>IServiceCollection</c> extension.</strong> The Docker and SSH backup
/// providers are registered by <c>AddServyxDockerBackups()</c> and <c>AddServyxSshBackups()</c>. This project
/// has <em>no</em> <c>PackageReference</c> at all — deliberately, see the .csproj — and that includes
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>, which is what makes it an adapter that can
/// carry no logger and therefore has no reachable path that could write an AWS secret access key, a derived
/// signing key, or a signature. Taking that dependency to gain one registration line would trade a real
/// security property for a syntactic convenience, so the opt-in is a factory instead — exactly as it is for
/// <c>DigitalOceanSnapshotBackups</c>.
/// </para>
/// <para>
/// <strong>This registers mutating, billable capability.</strong> Taking a snapshot set starts a charge that
/// recurs per GB-month until something deletes it; pruning deletes snapshots irreversibly. A composition root
/// that wants either has to say so here, in one line a reader can find without tracing a dependency graph.
/// Milestone 1 hosts must not call it, and a host with <c>Servyx:Provisioning:Enabled</c> unset never reaches
/// it — nothing in this repository calls this method outside its tests, so with the flag off the behaviour of
/// the process is unchanged in the strongest sense: the type is never constructed.
/// </para>
/// <para>
/// <strong>What it does NOT register is a restore path</strong>, because there is not one. Restoring from an
/// EBS snapshot creates a new volume that must then be attached, and swapping a root volume means stopping the
/// instance — see <see cref="EbsSnapshotBackupProvider.RestoreAsync(string, System.Threading.CancellationToken)"/>.
/// A host that registers this provider gets create, list, inspect, plan-restore and prune; a restore attempt
/// through the interface refuses with a message describing the real procedure.
/// </para>
/// <para>
/// <strong>No <see cref="IBackupAdopter"/> is registered, because there is nothing to adopt.</strong> An
/// adopter's job is to discover backups a workload's own mechanism made, so they can be surfaced as
/// <see cref="BackupOwnership.Foreign"/>. Here that discovery is not a separate mechanism: snapshots of this
/// instance's volumes are already visible to <see cref="EbsSnapshotBackupProvider.ListAsync"/>, and the ones
/// Servyx did not create are labelled foreign by <see cref="EbsSnapshotOwnership.Classify"/> as they are read.
/// </para>
/// <para>
/// Requires an <see cref="IEbsSnapshotContextSource"/> from the host. That is deliberately not defaulted:
/// mapping a Servyx server to an EC2 instance id is knowledge only the composition root has, and a
/// plausible-looking default would snapshot the wrong machine and — far worse — would make another machine's
/// snapshots look prunable.
/// </para>
/// </remarks>
public static class EbsSnapshotBackups
{
    /// <summary>
    /// Builds an EBS-snapshot-backed <see cref="IBackupProvider"/> for one AWS account and region.
    /// </summary>
    /// <param name="httpClient">The HTTP client the API calls go out on.</param>
    /// <param name="secretStore">Where the AWS key pair lives. Resolved per request and never cached.</param>
    /// <param name="identity">The URNs of the key pair. Only URNs are held.</param>
    /// <param name="region">The AWS region the instance and its snapshots live in.</param>
    /// <param name="contexts">Maps a Servyx server id to the EC2 instance that backs it.</param>
    /// <param name="timeProvider">Clock used for backup set naming and poll pacing.</param>
    /// <returns>
    /// The provider, typed as the concrete class rather than <see cref="IBackupProvider"/>, so a host can also
    /// reach <see cref="EbsSnapshotBackupProvider.EstimateStorageCeilingAsync"/> — the one member not on the
    /// interface, and the only way to ask what a server's snapshots are costing.
    /// </returns>
    public static EbsSnapshotBackupProvider Create(
        HttpClient httpClient,
        ISecretStore secretStore,
        AwsSigningIdentity identity,
        string region,
        IEbsSnapshotContextSource contexts,
        TimeProvider? timeProvider = null) =>
        new(httpClient, secretStore, identity, region, contexts, timeProvider);
}
