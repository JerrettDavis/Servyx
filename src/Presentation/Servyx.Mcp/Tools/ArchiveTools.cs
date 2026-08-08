using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Servyx.Application.Backups;
using Servyx.Composition;
using Servyx.Domain.Backups;

namespace Servyx.Mcp.Tools;

/// <summary>
/// The read-only half of the backup surface: listing, inspecting, previewing a restore, and previewing a
/// prune. Every apply/create/restore/delete member of <see cref="IBackupDashboard"/> is withheld from this
/// build entirely — see <c>Inventory/McpWithheldOperationTests</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why <see cref="IBackupDashboard"/> is resolved through <see cref="IServiceProvider"/> rather than
/// as a normal injected parameter.</strong> <c>AddServyxCoreCore</c> registers <c>IBackupDashboard</c> only
/// when <c>ProvisioningGate.Enabled</c> is true — with the gate closed, nothing in this process implements
/// it at all. A tool parameter typed as <c>IBackupDashboard</c> would make the MCP SDK's DI resolution throw
/// before this method's own body (and its capability check) ever ran, turning an expected, describable
/// refusal into an unhandled exception. Resolving it optionally, after the process-level capability check
/// below, keeps every refusal here a normal (non-erroring) result.
/// </para>
/// </remarks>
[McpServerToolType]
public static class ArchiveTools
{
    private const string NoApplyNotice =
        "This build exposes no apply tool for this operation. Report the result to a human operator, who " +
        "applies it in the web UI. Nothing has been written or deleted.";

    [McpServerTool(Name = "servyx_backups_list", UseStructuredContent = true)]
    [Description("Lists every known backup artifact for a server, partitioned into Servyx-owned and foreign.")]
    public static async Task<BackupListToolResult> ListAsync(
        [Description("The server's discovery id.")] string serverId,
        ServyxCoreComposition composition,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (TryUnavailable(composition, services, out var outcome))
        {
            return new BackupListToolResult(outcome!.Value.Outcome, outcome.Value.Message, null, null, null, outcome.Value.ReasonCode);
        }

        var dashboard = services.GetRequiredService<IBackupDashboard>();
        var result = await dashboard.ListAsync(serverId, cancellationToken).ConfigureAwait(false);
        return ResultMapping.Map(result);
    }

    [McpServerTool(Name = "servyx_backup_inspect", UseStructuredContent = true)]
    [Description("Reads a backup archive's index without extracting it. Writes nothing. May be Servyx-owned or foreign.")]
    public static async Task<BackupInspectToolResult> InspectAsync(
        [Description("The artifact id to inspect.")] string backupId,
        ServyxCoreComposition composition,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (TryUnavailable(composition, services, out var outcome))
        {
            return new BackupInspectToolResult(outcome!.Value.Outcome, outcome.Value.Message, null, null, null, outcome.Value.ReasonCode);
        }

        var dashboard = services.GetRequiredService<IBackupDashboard>();
        var result = await dashboard.InspectAsync(backupId, cancellationToken).ConfigureAwait(false);
        return ResultMapping.Map(result);
    }

    [McpServerTool(Name = "servyx_backup_restore_plan", UseStructuredContent = true)]
    [Description(
        "Previews what restoring a backup artifact would overwrite. Writes nothing. " + NoApplyNotice)]
    public static async Task<RestorePlanToolResult> RestorePlanAsync(
        [Description("The artifact id to preview a restore of. May be Servyx-owned or foreign.")] string backupId,
        ServyxCoreComposition composition,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (TryUnavailable(composition, services, out var outcome))
        {
            return new RestorePlanToolResult(outcome!.Value.Outcome, outcome.Value.Message, null, null, null, null, outcome.Value.ReasonCode);
        }

        var dashboard = services.GetRequiredService<IBackupDashboard>();
        var result = await dashboard.PlanRestoreAsync(backupId, cancellationToken).ConfigureAwait(false);
        return ResultMapping.Map(result);
    }

    [McpServerTool(Name = "servyx_backup_prune_preview", UseStructuredContent = true)]
    [Description(
        "Reports what a retention policy would remove for a server, deleting nothing. " + NoApplyNotice)]
    public static async Task<BackupPruneToolResult> PrunePreviewAsync(
        [Description("The server whose artifacts are evaluated.")] string serverId,
        [Description("How many hourly backups to retain.")] int keepHourly,
        [Description("How many daily backups to retain.")] int keepDaily,
        [Description("How many weekly backups to retain.")] int keepWeekly,
        ServyxCoreComposition composition,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (TryUnavailable(composition, services, out var outcome))
        {
            return new BackupPruneToolResult(outcome!.Value.Outcome, outcome.Value.Message, 0, null, null, null, null, outcome.Value.ReasonCode);
        }

        var dashboard = services.GetRequiredService<IBackupDashboard>();
        var policy = new RetentionPolicy(keepHourly, keepDaily, keepWeekly);
        var result = await dashboard.PreviewPruneAsync(serverId, policy, cancellationToken).ConfigureAwait(false);
        return ResultMapping.Map(result);
    }

    /// <summary>
    /// Checks the process-level <see cref="ServyxCapability.BackupProvider"/> fact first (never re-derived —
    /// read straight off the shared <see cref="ServyxCapabilityReport"/>), then defensively re-checks
    /// <see cref="IBackupDashboard.ProviderConfigured"/> in case a dashboard is registered but its provider
    /// is not, since <see cref="IBackupDashboard"/> throws <see cref="InvalidOperationException"/> on every
    /// member otherwise.
    /// </summary>
    private static bool TryUnavailable(
        ServyxCoreComposition composition, IServiceProvider services, out (string Outcome, string Message, string? ReasonCode)? outcome)
    {
        var status = composition.Capabilities.Get(ServyxCapability.BackupProvider);
        if (!status.Available)
        {
            outcome = ("unavailable", status.Explanation ?? "Backups are unavailable.", status.ReasonCode);
            return true;
        }

        var dashboard = services.GetService<IBackupDashboard>();
        if (dashboard is null || !dashboard.ProviderConfigured)
        {
            outcome = ("unavailable", "No backup provider is registered in this process.", UnavailableReason.NoProviderRegistered);
            return true;
        }

        outcome = null;
        return false;
    }
}
