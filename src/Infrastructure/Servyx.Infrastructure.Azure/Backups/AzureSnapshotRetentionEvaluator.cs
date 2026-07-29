using System.Globalization;

using Servyx.Domain.Backups;

namespace Servyx.Infrastructure.Azure.Backups;

/// <summary>
/// Decides which Servyx-owned backup sets a <see cref="RetentionPolicy"/> keeps and which it releases.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The unit is a backup set, not a snapshot.</strong> One Servyx backup of an Azure virtual machine is
/// every managed-disk snapshot written for one capture, and half a set is not a backup of anything — an OS disk
/// with no matching data disk restores a machine to a state it was never in. So retention evaluates
/// <em>sets</em>, and <see cref="AzureSnapshotBackupProvider.PruneAsync"/> deletes a set's snapshots all
/// together or not at all.
/// </para>
/// <para>
/// Pure computation over a list of artifacts: it performs no I/O and deletes nothing, so it can be exercised
/// directly, and <see cref="AzureSnapshotBackupProvider.PruneAsync"/>'s dry-run and live paths compute their
/// answer from the identical call rather than from two implementations that could disagree.
/// </para>
/// <para>
/// <strong>Foreign artifacts are rejected at the door.</strong> <see cref="SelectForRemoval"/> throws if it is
/// handed one, rather than quietly filtering it out. Filtering would make the function tolerant of a caller
/// that had already lost track of ownership; throwing means any future code path that forgets to partition by
/// ownership fails in a test instead of irreversibly deleting snapshots Servyx did not create. This is the
/// second of the three barriers described on <see cref="AzureSnapshotBackupProvider.PruneAsync"/>.
/// </para>
/// <para>
/// Deliberately a separate implementation from the Docker, SSH, DigitalOcean and AWS adapters' evaluators, with
/// the same bucketing rule. Infrastructure projects reference <c>Servyx.Domain</c> and nothing else, so sharing
/// it would mean either a cross-adapter reference or promoting retention into the domain; a pinned test asserts
/// this one keeps the same set the others would.
/// </para>
/// </remarks>
public static class AzureSnapshotRetentionEvaluator
{
    /// <summary>
    /// Returns the backup sets <paramref name="policy"/> does not keep, oldest first.
    /// </summary>
    /// <remarks>
    /// Each granularity keeps the newest artifact in each of its most recent buckets, up to that granularity's
    /// keep-count: <c>KeepHourly</c> distinct clock hours, <c>KeepDaily</c> distinct days, <c>KeepWeekly</c>
    /// distinct ISO-8601 weeks, all evaluated newest-first in UTC. An artifact survives if any granularity keeps
    /// it, so a single nightly capture counts as that day's daily <em>and</em> that week's weekly rather than
    /// being double-charged.
    /// </remarks>
    /// <param name="artifacts">Candidate backup sets. Every one must be <see cref="BackupOwnership.Servyx"/>.</param>
    /// <param name="policy">The retention policy to apply.</param>
    /// <exception cref="ForeignAzureSnapshotProtectedException">Any candidate is <see cref="BackupOwnership.Foreign"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A keep-count is negative.</exception>
    public static IReadOnlyList<BackupArtifact> SelectForRemoval(
        IReadOnlyList<BackupArtifact> artifacts,
        RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.KeepHourly);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.KeepDaily);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.KeepWeekly);

        foreach (var artifact in artifacts)
        {
            if (artifact.Ownership != BackupOwnership.Servyx)
            {
                throw new ForeignAzureSnapshotProtectedException(
                    $"Backup '{artifact.Id}' is {artifact.Ownership} and must never be evaluated against a Servyx "
                    + "retention policy. Deleting an Azure snapshot cannot be undone and may be removing the only "
                    + "copy of somebody's saves.",
                    artifact.Location);
            }
        }

        var newestFirst = artifacts
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        var keep = new HashSet<string>(StringComparer.Ordinal);
        Retain(newestFirst, policy.KeepHourly, HourBucket, keep);
        Retain(newestFirst, policy.KeepDaily, DayBucket, keep);
        Retain(newestFirst, policy.KeepWeekly, WeekBucket, keep);

        return artifacts
            .Where(a => !keep.Contains(a.Id))
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void Retain(
        IReadOnlyList<BackupArtifact> newestFirst,
        int keepCount,
        Func<DateTimeOffset, string> bucketOf,
        HashSet<string> keep)
    {
        if (keepCount <= 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in newestFirst)
        {
            var bucket = bucketOf(artifact.CreatedAt);
            if (!seen.Add(bucket))
            {
                continue; // A newer capture already represents this bucket.
            }

            keep.Add(artifact.Id);
            if (seen.Count >= keepCount)
            {
                return;
            }
        }
    }

    private static string HourBucket(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture);

    private static string DayBucket(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string WeekBucket(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{ISOWeek.GetYear(utc)}-W{ISOWeek.GetWeekOfYear(utc):00}");
    }
}
