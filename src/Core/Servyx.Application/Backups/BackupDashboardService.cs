using System.Collections.Concurrent;
using Servyx.Domain.Backups;

namespace Servyx.Application.Backups;

/// <summary>
/// The default <see cref="IBackupDashboard"/>: a thin, refusing projection over whichever
/// <see cref="IBackupProvider"/> the composition root chose to register.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It adds three things the provider cannot add for itself.</strong>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <em>An ownership audit that runs before deletion.</em> <see cref="ApplyPruneAsync"/> asks the provider
/// for a dry run first, compares its candidates against the listing's foreign half, and only then asks for
/// the live prune. The provider already refuses foreign artifacts three separate ways; this is a fourth
/// barrier that does not depend on trusting any of them, and it is the one a fake or future provider
/// cannot bypass.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>A preview the apply is bound to.</em> <see cref="PlanRestoreAsync"/> remembers how many paths each
/// plan named; <see cref="ApplyRestoreAsync"/> refuses a plan id it never issued, and refuses one whose
/// path count does not match what the caller says it approved. Planning on its own reaches
/// <see cref="IBackupProvider.PlanRestoreAsync"/> and nothing else — there is no path from a preview to a
/// write without a second, separate call.
/// </description>
/// </item>
/// <item>
/// <description>
/// <em>Structural failure reporting.</em> Provider exceptions live in the infrastructure assembly this
/// layer deliberately does not reference, so each is translated into a case of a closed hierarchy carrying
/// its message and type name. Nothing is caught and discarded; every catch produces a result a caller must
/// destructure to read.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>Cancellation is never translated.</strong> <see cref="OperationCanceledException"/> propagates
/// untouched: a cancelled request is not a backup failure, and reporting it as one would put a red error
/// on screen every time an operator navigated away mid-listing.
/// </para>
/// </remarks>
public sealed class BackupDashboardService : IBackupDashboard
{
    private readonly IBackupProvider? _provider;
    private readonly ConcurrentDictionary<string, IssuedPlan> _issuedPlans = new(StringComparer.Ordinal);

    /// <summary>Creates a dashboard over <paramref name="provider"/>.</summary>
    /// <param name="provider">
    /// The backup provider, or <see langword="null"/> when the composition root registered none — which is
    /// what a read-only host does. Null is a supported configuration, reported through
    /// <see cref="ProviderConfigured"/>, and every operation then throws rather than pretending.
    /// </param>
    public BackupDashboardService(IBackupProvider? provider = null) => _provider = provider;

    /// <summary>
    /// Whether a given artifact may ever be considered for pruning. Public and static so a view asks this
    /// one question rather than restating the ownership rule with its own comparison.
    /// </summary>
    /// <param name="artifact">The artifact to test.</param>
    public static bool IsPrunable(BackupArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return artifact.Ownership == BackupOwnership.Servyx;
    }

    /// <inheritdoc />
    public bool ProviderConfigured => _provider is not null;

    /// <inheritdoc />
    public async Task<BackupListResult> ListAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var provider = Require();

        try
        {
            var all = await provider.ListAsync(serverId, ct).ConfigureAwait(false) ?? [];
            return new BackupListResult.Listed(
                [.. all.Where(a => a.Ownership == BackupOwnership.Servyx).OrderByDescending(a => a.CreatedAt)],
                [.. all.Where(a => a.Ownership == BackupOwnership.Foreign).OrderByDescending(a => a.CreatedAt)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupListResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<BackupCreateResult> CreateAsync(string serverId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var provider = Require();

        try
        {
            return new BackupCreateResult.Created(await provider.CreateAsync(serverId, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupCreateResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<BackupInspectResult> InspectAsync(string backupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var provider = Require();

        try
        {
            var entries = await provider.InspectAsync(backupId, ct).ConfigureAwait(false) ?? [];
            return new BackupInspectResult.Inspected(backupId, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupInspectResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<RestorePlanResult> PlanRestoreAsync(string backupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var provider = Require();

        try
        {
            var plan = await provider.PlanRestoreAsync(backupId, ct).ConfigureAwait(false);

            // Remembered so ApplyRestoreAsync can refuse a plan id this process never issued, and refuse
            // one whose shape does not match what the caller says it approved. Nothing here writes.
            _issuedPlans[plan.Id] = new IssuedPlan(plan.BackupId, plan.AffectedPaths.Count);

            return new RestorePlanResult.Planned(plan);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RestorePlanResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<RestoreApplyResult> ApplyRestoreAsync(
        string restorePlanId,
        int expectedPathCount,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restorePlanId);
        var provider = Require();

        // Barrier 1 — this process must have issued the plan. A caller that fabricated a plan id, or that
        // is applying one from a previous process, is refused before the provider is contacted at all.
        if (!_issuedPlans.TryRemove(restorePlanId, out var issued))
        {
            return new RestoreApplyResult.Stale(
                $"Restore plan '{restorePlanId}' was not issued by this process, or has already been applied.");
        }

        // Barrier 2 — the approval must describe the preview. A UI that rendered one plan and confirmed a
        // different one is refused here rather than discovering the mismatch after the write.
        if (issued.AffectedPathCount != expectedPathCount)
        {
            return new RestoreApplyResult.Stale(
                $"Restore plan '{restorePlanId}' affects {issued.AffectedPathCount} path(s), but the confirmation "
                + $"claimed {expectedPathCount}.");
        }

        try
        {
            await provider.RestoreAsync(restorePlanId, ct).ConfigureAwait(false);
            return new RestoreApplyResult.Restored(restorePlanId, issued.AffectedPathCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsStaleRefusal(ex))
        {
            // The provider's own single-use/TTL/size revalidation refused. Nothing was written, so this is
            // kept distinct from Failed, which may have overwritten some paths already.
            return new RestoreApplyResult.Stale(ex.Message);
        }
        catch (Exception ex)
        {
            return new RestoreApplyResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public Task<BackupPruneResult> PreviewPruneAsync(
        string serverId,
        RetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(policy);

        return PreviewCoreAsync(serverId, policy, ct);
    }

    /// <inheritdoc />
    public async Task<BackupPruneResult> ApplyPruneAsync(
        string serverId,
        RetentionPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(policy);

        var provider = Require();

        // The dry run runs first, unconditionally, and its candidates are audited against the foreign half
        // of the listing. Deleting first and auditing afterwards would report the violation accurately and
        // far too late.
        var preview = await PreviewCoreAsync(serverId, policy, ct).ConfigureAwait(false);
        if (preview is not BackupPruneResult.Previewed previewed)
        {
            // Failed or RefusedForeign. Either way the live prune is not attempted.
            return preview;
        }

        if (previewed.Candidates.Count == 0)
        {
            return new BackupPruneResult.Pruned([], previewed.SkippedForeign);
        }

        try
        {
            var result = await provider.PruneAsync(serverId, policy, dryRun: false, ct).ConfigureAwait(false);
            var foreign = await ForeignIdsAsync(provider, serverId, ct).ConfigureAwait(false);
            var removed = result.Removed ?? [];

            // A second audit, after the fact. It cannot un-delete anything; it exists so that a provider
            // which deleted something it should not have is reported as a refusal rather than as success.
            var violations = removed.Where(foreign.Contains).ToList();
            return violations.Count > 0
                ? new BackupPruneResult.RefusedForeign(violations)
                : new BackupPruneResult.Pruned(removed, result.SkippedForeign);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupPruneResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    private async Task<BackupPruneResult> PreviewCoreAsync(string serverId, RetentionPolicy policy, CancellationToken ct)
    {
        var provider = Require();

        try
        {
            var result = await provider.PruneAsync(serverId, policy, dryRun: true, ct).ConfigureAwait(false);
            var candidates = result.Removed ?? [];
            var foreign = await ForeignIdsAsync(provider, serverId, ct).ConfigureAwait(false);

            var violations = candidates.Where(foreign.Contains).ToList();
            return violations.Count > 0
                ? new BackupPruneResult.RefusedForeign(violations)
                : new BackupPruneResult.Previewed(candidates, result.SkippedForeign);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BackupPruneResult.Failed(ex.Message, ex.GetType().Name);
        }
    }

    private static async Task<HashSet<string>> ForeignIdsAsync(IBackupProvider provider, string serverId, CancellationToken ct)
    {
        var all = await provider.ListAsync(serverId, ct).ConfigureAwait(false) ?? [];
        return [.. all.Where(a => a.Ownership == BackupOwnership.Foreign).Select(a => a.Id)];
    }

    /// <summary>
    /// Whether a provider exception means "refused, nothing written" rather than "failed part-way".
    /// Matched by type name because the exception type lives in the infrastructure assembly this layer
    /// does not reference — see the class remarks.
    /// </summary>
    private static bool IsStaleRefusal(Exception ex) =>
        string.Equals(ex.GetType().Name, "RestorePlanStaleException", StringComparison.Ordinal);

    private IBackupProvider Require() =>
        _provider ?? throw new InvalidOperationException(
            $"No {nameof(IBackupProvider)} is registered in this process, so backups cannot be listed, created, "
            + $"inspected, restored, or pruned. Check {nameof(ProviderConfigured)} before calling, and register a "
            + "provider at the composition root (AddServyxDockerBackups()) if this host is meant to have one.");

    private sealed record IssuedPlan(string BackupId, int AffectedPathCount);
}
