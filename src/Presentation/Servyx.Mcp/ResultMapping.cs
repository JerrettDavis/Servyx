using Servyx.Application.Backups;
using Servyx.Domain.Backups;

namespace Servyx.Mcp;

/// <summary>An MCP-facing view of a <see cref="BackupArtifact"/>. <see cref="BackupArtifact.Ownership"/> crosses as a lower-kebab string — see the contract rule in <see cref="ResultMapping"/>'s remarks.</summary>
public sealed record BackupArtifactDto(string Id, string Ownership, DateTimeOffset CreatedAt, long SizeBytes, string Location)
{
    /// <summary>Maps a domain <see cref="BackupArtifact"/> to its MCP-facing shape.</summary>
    public static BackupArtifactDto From(BackupArtifact artifact) => new(
        artifact.Id, KebabCase.From(artifact.Ownership.ToString()), artifact.CreatedAt, artifact.SizeBytes, artifact.Location);
}

/// <summary>The MCP-facing mapping of <see cref="BackupListResult"/>. Exactly one of the case-specific field groups is populated, selected by <see cref="Outcome"/>.</summary>
public sealed record BackupListToolResult(
    string Outcome,
    string Message,
    IReadOnlyList<BackupArtifactDto>? ServyxOwned,
    IReadOnlyList<BackupArtifactDto>? Foreign,
    string? Detail,
    string? FailureKind);

/// <summary>The MCP-facing mapping of <see cref="BackupInspectResult"/>.</summary>
public sealed record BackupInspectToolResult(
    string Outcome,
    string Message,
    string? BackupId,
    IReadOnlyList<string>? Entries,
    string? Detail,
    string? FailureKind);

/// <summary>The MCP-facing mapping of <see cref="RestorePlanResult"/>.</summary>
public sealed record RestorePlanToolResult(
    string Outcome,
    string Message,
    string? PlanId,
    string? BackupId,
    IReadOnlyList<string>? AffectedPaths,
    string? Detail,
    string? FailureKind);

/// <summary>
/// The MCP-facing mapping of <see cref="BackupPruneResult"/>. <see cref="Candidates"/> (from <c>Previewed</c>)
/// and <see cref="Removed"/> (from <c>Pruned</c>) are kept as separate fields, never merged — see that
/// union's own remarks: "'these would go' and 'these are gone' must never be rendered by the same branch."
/// An agent reading one field for both would report a dry run as a deletion.
/// </summary>
/// <param name="SkippedForeign">
/// Only meaningful when <see cref="Outcome"/> indicates the backup dashboard was actually consulted. On an
/// "unavailable" outcome no dashboard call was made, and this is forced to <c>0</c> because the underlying
/// type is a non-nullable <c>int</c> — that zero means "not applicable," not "no foreign artifacts were
/// skipped."
/// </param>
public sealed record BackupPruneToolResult(
    string Outcome,
    string Message,
    int SkippedForeign,
    IReadOnlyList<string>? Candidates,
    IReadOnlyList<string>? Removed,
    IReadOnlyList<string>? ForeignIds,
    string? Detail,
    string? FailureKind);

/// <summary>
/// Maps the Application layer's closed discriminated unions (<c>Servyx.Application.Backups.BackupResults</c>)
/// to their MCP-facing shapes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately maps four of <see cref="IBackupDashboard"/>'s six result unions, not six.</b> The two
/// left unmapped — <see cref="BackupCreateResult"/> (from <see cref="IBackupDashboard.CreateAsync"/>) and
/// <see cref="RestoreApplyResult"/> (from <see cref="IBackupDashboard.ApplyRestoreAsync"/>) — are exactly the
/// two mutating, irreversible operations this build withholds; see
/// <c>Inventory/McpWithheldOperationTests</c>. Adding a mapping for either would produce dead code whose only
/// function is to make a withheld destructive operation trivial to wire up later. Do not "complete" this
/// file by adding them.
/// </para>
/// <para>
/// <b>One <c>Outcome</c> discriminant per union case, equal to the case's type name in lower-kebab</b> —
/// e.g. <see cref="BackupListResult.Listed"/> becomes <c>"listed"</c>, <see cref="BackupPruneResult.RefusedForeign"/>
/// becomes <c>"refused-foreign"</c>. Never <c>bool Success</c>: outcomes are named facts, and a refusal is
/// as normal a result as a success.
/// </para>
/// <para>
/// <b>The union's own <see cref="BackupListResult.Message"/> (and its siblings') crosses verbatim.</b>
/// Those strings were written for a human reading a dashboard, and are the best prose describing what
/// happened anywhere in this codebase; re-deriving a shorter MCP-specific message would throw that away
/// for no benefit.
/// </para>
/// <para>
/// <b>Case-specific fields are added flat, and no two cases are ever collapsed into one field.</b> See
/// <see cref="BackupPruneToolResult"/>'s remarks for the concrete hazard that rule prevents.
/// </para>
/// </remarks>
public static class ResultMapping
{
    /// <summary>Maps a <see cref="BackupListResult"/> to its MCP-facing shape.</summary>
    public static BackupListToolResult Map(BackupListResult result) => result switch
    {
        BackupListResult.Listed listed => new BackupListToolResult(
            Outcome: "listed",
            Message: listed.Message,
            ServyxOwned: listed.ServyxOwned.Select(BackupArtifactDto.From).ToList(),
            Foreign: listed.Foreign.Select(BackupArtifactDto.From).ToList(),
            Detail: null,
            FailureKind: null),

        BackupListResult.Failed failed => new BackupListToolResult(
            Outcome: "failed",
            Message: failed.Message,
            ServyxOwned: null,
            Foreign: null,
            Detail: failed.Detail,
            FailureKind: failed.FailureKind),

        _ => throw Unrecognized(result),
    };

    /// <summary>Maps a <see cref="BackupInspectResult"/> to its MCP-facing shape.</summary>
    public static BackupInspectToolResult Map(BackupInspectResult result) => result switch
    {
        BackupInspectResult.Inspected inspected => new BackupInspectToolResult(
            Outcome: "inspected",
            Message: inspected.Message,
            BackupId: inspected.BackupId,
            Entries: inspected.Entries,
            Detail: null,
            FailureKind: null),

        BackupInspectResult.Failed failed => new BackupInspectToolResult(
            Outcome: "failed",
            Message: failed.Message,
            BackupId: null,
            Entries: null,
            Detail: failed.Detail,
            FailureKind: failed.FailureKind),

        _ => throw Unrecognized(result),
    };

    /// <summary>Maps a <see cref="RestorePlanResult"/> to its MCP-facing shape.</summary>
    public static RestorePlanToolResult Map(RestorePlanResult result) => result switch
    {
        RestorePlanResult.Planned planned => new RestorePlanToolResult(
            Outcome: "planned",
            Message: planned.Message,
            PlanId: planned.Plan.Id,
            BackupId: planned.Plan.BackupId,
            AffectedPaths: planned.Plan.AffectedPaths,
            Detail: null,
            FailureKind: null),

        RestorePlanResult.Failed failed => new RestorePlanToolResult(
            Outcome: "failed",
            Message: failed.Message,
            PlanId: null,
            BackupId: null,
            AffectedPaths: null,
            Detail: failed.Detail,
            FailureKind: failed.FailureKind),

        _ => throw Unrecognized(result),
    };

    /// <summary>Maps a <see cref="BackupPruneResult"/> to its MCP-facing shape.</summary>
    public static BackupPruneToolResult Map(BackupPruneResult result) => result switch
    {
        BackupPruneResult.Previewed previewed => new BackupPruneToolResult(
            Outcome: "previewed",
            Message: previewed.Message,
            SkippedForeign: previewed.SkippedForeign,
            Candidates: previewed.Candidates,
            Removed: null,
            ForeignIds: null,
            Detail: null,
            FailureKind: null),

        BackupPruneResult.Pruned pruned => new BackupPruneToolResult(
            Outcome: "pruned",
            Message: pruned.Message,
            SkippedForeign: pruned.SkippedForeign,
            Candidates: null,
            Removed: pruned.Removed,
            ForeignIds: null,
            Detail: null,
            FailureKind: null),

        BackupPruneResult.RefusedForeign refused => new BackupPruneToolResult(
            Outcome: "refused-foreign",
            Message: refused.Message,
            SkippedForeign: refused.SkippedForeign,
            Candidates: null,
            Removed: null,
            ForeignIds: refused.ForeignIds,
            Detail: null,
            FailureKind: null),

        BackupPruneResult.Failed failed => new BackupPruneToolResult(
            Outcome: "failed",
            Message: failed.Message,
            SkippedForeign: failed.SkippedForeign,
            Candidates: null,
            Removed: null,
            ForeignIds: null,
            Detail: failed.Detail,
            FailureKind: failed.FailureKind),

        _ => throw Unrecognized(result),
    };

    /// <summary>
    /// Every switch above is closed over its union's private constructor, so this default arm is
    /// unreachable today; it throws rather than silently mapping an unrecognized case to a made-up
    /// outcome if a new case is ever added to the union without a matching arm here.
    /// </summary>
    private static NotSupportedException Unrecognized(object result) => new(
        $"Unrecognized {result.GetType().BaseType?.Name ?? result.GetType().Name} case " +
        $"'{result.GetType().Name}'; {nameof(ResultMapping)} must be updated to give it its own outcome.");
}
