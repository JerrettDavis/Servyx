using Servyx.Domain.Backups;

namespace Servyx.Application.Backups;

/// <summary>
/// The outcome of <see cref="IBackupDashboard.ListAsync"/>: either both halves of the listing, split by
/// <see cref="BackupOwnership"/>, or the reason the listing could not be produced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The split is the point.</strong> A single flat list would let a caller render one control set
/// over both ownerships and discover the difference only when a delete threw. Handing back two named
/// collections makes "this artifact is Servyx-owned" a property of which collection it arrived in, not a
/// field a view has to remember to check.
/// </para>
/// <para>
/// <strong>A failure is a case, not an empty list.</strong> "The provider could not be reached" and "this
/// server has no backups" are opposite facts, and collapsing them into <c>[]</c> tells an operator the
/// most reassuring of the two at exactly the moment it is least likely to be true.
/// </para>
/// </remarks>
public abstract record BackupListResult
{
    // Private so the case set is closed to this file. A new outcome is a deliberate, reviewable act.
    private BackupListResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The listing succeeded, already partitioned by ownership.</summary>
    /// <param name="ServyxOwned">Artifacts Servyx created and may prune.</param>
    /// <param name="Foreign">Artifacts some other mechanism created. Listable, inspectable, restorable — never prunable.</param>
    public sealed record Listed(
        IReadOnlyList<BackupArtifact> ServyxOwned,
        IReadOnlyList<BackupArtifact> Foreign) : BackupListResult
    {
        /// <summary>Every artifact, Servyx-owned first, newest first within each half.</summary>
        public IReadOnlyList<BackupArtifact> All => [.. ServyxOwned, .. Foreign];

        /// <inheritdoc />
        public override string Message =>
            $"{ServyxOwned.Count} Servyx-owned and {Foreign.Count} foreign artifact(s).";
    }

    /// <summary>The provider could not produce a listing. Nothing is known about what exists.</summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : BackupListResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Backups could not be listed ({FailureKind}): {Detail}. This is not the same as 'there are none'.";
    }
}

/// <summary>The outcome of <see cref="IBackupDashboard.CreateAsync"/>.</summary>
/// <remarks>
/// A create that failed after the quiesce step is not the same as one that never started, and neither is
/// the same as one that wrote a partial archive. The provider distinguishes those by exception type; this
/// hierarchy keeps the distinction visible to a caller that cannot reference the provider's assembly.
/// </remarks>
public abstract record BackupCreateResult
{
    private BackupCreateResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The archive and its sidecar manifest were written.</summary>
    /// <param name="Artifact">The artifact the provider reported.</param>
    public sealed record Created(BackupArtifact Artifact) : BackupCreateResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Created backup '{Artifact.Id}' ({Artifact.SizeBytes} bytes) at {Artifact.CreatedAt:u}.";
    }

    /// <summary>
    /// The attempt failed. <strong>No artifact was produced</strong> — the provider aborts before writing
    /// anything when the quiesce step fails, and a write failure leaves no half-listed archive because the
    /// listing is keyed on the archive file itself.
    /// </summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : BackupCreateResult
    {
        /// <inheritdoc />
        public override string Message => $"The backup was not created ({FailureKind}): {Detail}";
    }
}

/// <summary>The outcome of <see cref="IBackupDashboard.InspectAsync"/>.</summary>
public abstract record BackupInspectResult
{
    private BackupInspectResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The archive's index was read. Nothing was extracted and nothing was written.</summary>
    /// <param name="BackupId">The artifact that was inspected.</param>
    /// <param name="Entries">The entry names the manifest (or the archive's tar headers) declare.</param>
    public sealed record Inspected(string BackupId, IReadOnlyList<string> Entries) : BackupInspectResult
    {
        /// <inheritdoc />
        public override string Message => $"'{BackupId}' contains {Entries.Count} entr(y/ies).";
    }

    /// <summary>The archive's index could not be read.</summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : BackupInspectResult
    {
        /// <inheritdoc />
        public override string Message => $"The backup could not be inspected ({FailureKind}): {Detail}";
    }
}

/// <summary>
/// The outcome of <see cref="IBackupDashboard.PlanRestoreAsync"/>. Planning is read-only: no case in this
/// hierarchy — including <see cref="Planned"/> — means anything was overwritten.
/// </summary>
public abstract record RestorePlanResult
{
    private RestorePlanResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>
    /// The restore was previewed. <strong>Nothing has been overwritten.</strong> Applying the plan is a
    /// separate, explicit call to <see cref="IBackupDashboard.ApplyRestoreAsync"/>.
    /// </summary>
    /// <param name="Plan">The plan, naming every path a restore would overwrite.</param>
    public sealed record Planned(RestorePlan Plan) : RestorePlanResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Restoring '{Plan.BackupId}' would overwrite {Plan.AffectedPaths.Count} path(s). Nothing has been written.";
    }

    /// <summary>The restore could not even be previewed.</summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : RestorePlanResult
    {
        /// <inheritdoc />
        public override string Message => $"The restore could not be previewed ({FailureKind}): {Detail}";
    }
}

/// <summary>
/// The outcome of <see cref="IBackupDashboard.ApplyRestoreAsync"/> — the one operation on this surface
/// that overwrites live save data.
/// </summary>
public abstract record RestoreApplyResult
{
    private RestoreApplyResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>The archive's contents were written over the paths the plan named.</summary>
    /// <param name="RestorePlanId">The plan that was applied. It is now spent and cannot be applied again.</param>
    /// <param name="OverwrittenPathCount">How many paths the applied plan named.</param>
    public sealed record Restored(string RestorePlanId, int OverwrittenPathCount) : RestoreApplyResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Restore applied. {OverwrittenPathCount} path(s) were overwritten from the archive. There is no undo.";
    }

    /// <summary>
    /// The plan was refused: unknown, already applied, expired, or computed against an archive that has
    /// changed since. <strong>Nothing was written.</strong>
    /// </summary>
    /// <param name="Detail">The refusal as the provider described it.</param>
    public sealed record Stale(string Detail) : RestoreApplyResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"{Detail} Nothing was overwritten. Preview the restore again and confirm the plan you are then shown.";
    }

    /// <summary>
    /// The restore began and then failed. Unlike <see cref="Stale"/>, some paths may already have been
    /// overwritten — a partially restored tree is a real state and this case exists to say so.
    /// </summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : RestoreApplyResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"The restore failed part-way ({FailureKind}): {Detail} Some paths may already have been overwritten.";
    }
}

/// <summary>
/// The outcome of <see cref="IBackupDashboard.PreviewPruneAsync"/> or
/// <see cref="IBackupDashboard.ApplyPruneAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="Previewed"/> and <see cref="Pruned"/> are separate cases carrying the same shape on purpose:
/// "these would go" and "these are gone" must never be rendered by the same branch, because the only
/// difference between them is whether the archives still exist.
/// </remarks>
public abstract record BackupPruneResult
{
    private BackupPruneResult()
    {
    }

    /// <summary>A human-readable statement of what happened, suitable for showing to a user verbatim.</summary>
    public abstract string Message { get; }

    /// <summary>How many foreign artifacts were seen and left untouched.</summary>
    public abstract int SkippedForeign { get; }

    /// <summary>A dry run. <strong>Nothing was deleted.</strong></summary>
    /// <param name="Candidates">Artifact ids retention would remove if this were applied.</param>
    /// <param name="SkippedForeign">Foreign artifacts seen and left alone. They are never candidates.</param>
    public sealed record Previewed(IReadOnlyList<string> Candidates, int SkippedForeign) : BackupPruneResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Dry run: {Candidates.Count} Servyx-owned artifact(s) would be removed and {SkippedForeign} foreign "
            + "artifact(s) were skipped. Nothing has been deleted.";

        /// <inheritdoc />
        public override int SkippedForeign { get; } = SkippedForeign;
    }

    /// <summary>Retention was applied and the listed artifacts are gone.</summary>
    /// <param name="Removed">Artifact ids that were deleted.</param>
    /// <param name="SkippedForeign">Foreign artifacts seen and left alone.</param>
    public sealed record Pruned(IReadOnlyList<string> Removed, int SkippedForeign) : BackupPruneResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"{Removed.Count} Servyx-owned artifact(s) were removed; {SkippedForeign} foreign artifact(s) were "
            + "skipped and remain on disk.";

        /// <inheritdoc />
        public override int SkippedForeign { get; } = SkippedForeign;
    }

    /// <summary>
    /// A foreign artifact appeared among the removal candidates, so the prune was refused and
    /// <strong>nothing was deleted</strong>.
    /// </summary>
    /// <remarks>
    /// Unreachable against a correct provider — <c>DockerBackupProvider</c> partitions foreign artifacts
    /// out before retention is computed and re-asserts ownership at the one method that deletes. This case
    /// exists because "unreachable" is a claim about today's provider, and the Application layer refuses
    /// independently rather than trusting it.
    /// </remarks>
    /// <param name="ForeignIds">The foreign artifact ids that appeared as candidates.</param>
    public sealed record RefusedForeign(IReadOnlyList<string> ForeignIds) : BackupPruneResult
    {
        /// <inheritdoc />
        public override string Message =>
            $"Refused: retention named {ForeignIds.Count} artifact(s) Servyx does not own "
            + $"({string.Join(", ", ForeignIds)}). Foreign artifacts are never pruned. Nothing was deleted.";

        /// <inheritdoc />
        public override int SkippedForeign => ForeignIds.Count;
    }

    /// <summary>The prune could not be computed or completed.</summary>
    /// <param name="Detail">The failure as the provider described it.</param>
    /// <param name="FailureKind">The provider exception's type name, for diagnostics.</param>
    public sealed record Failed(string Detail, string FailureKind) : BackupPruneResult
    {
        /// <inheritdoc />
        public override string Message => $"Retention could not be applied ({FailureKind}): {Detail}";

        /// <inheritdoc />
        public override int SkippedForeign => 0;
    }
}
