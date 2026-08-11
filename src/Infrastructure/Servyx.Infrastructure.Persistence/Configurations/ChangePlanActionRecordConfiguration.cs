using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ChangePlanActionRecord"/> to the <c>ChangePlanActions</c> table.
/// </summary>
public sealed class ChangePlanActionRecordConfiguration : IEntityTypeConfiguration<ChangePlanActionRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChangePlanActionRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ChangePlanActions");

        builder.HasKey(action => action.Id);

        // Minted by the previewer, never by the store, matching ChangePlanRecord.Id's own reasoning: the row
        // is written before any apply attempt, not generated as a side effect of one.
        builder.Property(action => action.Id)
            .ValueGeneratedNever();

        builder.Property(action => action.ChangePlanId)
            .IsRequired();

        builder.Property(action => action.Ordinal)
            .IsRequired();

        // Stored by name, matching every other enum column in this schema (see ChangePlanRecordConfiguration.Status).
        builder.Property(action => action.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(action => action.SurfaceId)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(action => action.ResolvedPath)
            .IsRequired()
            .HasMaxLength(1024);

        // Stored as its underlying int, not by name, deliberately diverging from the rest of this schema:
        // TransportCapabilities is a [Flags] enum, and a persisted row here always holds a bitwise-OR'd
        // combination (e.g. FileWrite | ExecuteCommand), not a single named member. Enum.ToString() on a
        // combined flags value only round-trips cleanly when every bit maps back to a declared name or
        // combination, which is not guaranteed as capabilities are added over time; the underlying int has no
        // such fragility and is exactly what TransportCapabilities already is at the call sites that produce
        // it (see e.g. DockerTransport/SshTransport). This column is apply-engine plumbing, not something an
        // operator reads directly during an incident the way Status or Kind are, so losing the by-name
        // readability costs nothing in practice.
        builder.Property(action => action.RequiredCapabilities)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(action => action.UnifiedDiff)
            .IsRequired();

        builder.Property(action => action.Reversible)
            .IsRequired();

        builder.Property(action => action.PreImageHash)
            .HasMaxLength(128);

        // No max length on the *Content columns: full surface content, unmasked (see this entity's own
        // remarks), whose size is entirely surface-defined.
        builder.Property(action => action.PreImageContent);

        // Not nullable, and NOT given a store-side HasDefaultValue: EF omits a property sitting at its CLR
        // default from an INSERT when the column has a store default, so `false` — the value that means
        // "delete this file to revert" — would silently become `true` on the way to the database. The
        // migration backfills existing rows to true instead, which is a one-off statement about history
        // rather than a standing rule about writes.
        builder.Property(action => action.PreImageExisted)
            .IsRequired();

        builder.Property(action => action.PostImageContent);

        builder.Property(action => action.PostImageHash)
            .HasMaxLength(128);

        // Same shape as PostImageHash — a bare hex SHA-256 — and deliberately a second column rather than an
        // overwrite of that one: approved and observed are different facts and both have to outlive the
        // images. See the entity's own remarks.
        builder.Property(action => action.ObservedPostImageHash)
            .HasMaxLength(128);

        // Not nullable: "no write reached the server" is a real answer, not a missing one, and the retention
        // sweep reads this column to decide whether a plan's pre-images may be discarded.
        builder.Property(action => action.WriteReachedServer)
            .IsRequired();

        builder.Property(action => action.ContainsSecrets)
            .IsRequired();

        builder.Property(action => action.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        // Stored by name, like Status and Kind above: an operator reads this column while deciding whether a
        // change they were told landed was ever actually confirmed on disk.
        builder.Property(action => action.PostWriteVerification)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(action => action.AppliedAt);

        builder.Property(action => action.RevertedAt);

        builder.Property(action => action.FailureReason);

        // ── Revert evidence ────────────────────────────────────────────────────────────────────────────
        // The revert-phase counterparts of WriteReachedServer / ObservedPostImageHash / PostWriteVerification
        // / FailureReason above, in their own columns for the same reason those are in theirs: what a revert
        // did and what an apply did are different facts about the same row, and a revert must never be able
        // to overwrite the account of the apply it is undoing.

        // Same "no write reached the server is a real answer" reasoning as WriteReachedServer.
        builder.Property(action => action.RevertWriteReachedServer)
            .IsRequired();

        // A bare hex SHA-256, the same shape as PreImageHash and ObservedPostImageHash.
        builder.Property(action => action.RevertObservedImageHash)
            .HasMaxLength(128);

        // Stored by name like every other enum column here, and NULLABLE rather than defaulted: "no revert
        // was attempted" and PostWriteVerification.NotAttempted ("a revert ran and never looked") are
        // different statements, and only a nullable column can hold both.
        builder.Property(action => action.RevertVerification)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(action => action.RevertFailureReason);

        // Real referential integrity to ChangePlanRecord.Id, with cascade delete: an action row has no
        // independent existence — deleting a plan (including transitively, via ChangePlanRecord's own cascade
        // from Server) must discard every action under it.
        builder.HasOne<ChangePlanRecord>()
            .WithMany()
            .HasForeignKey(action => action.ChangePlanId)
            .OnDelete(DeleteBehavior.Cascade);

        // Apply/revert both walk a plan's actions in Ordinal order — this is the query that index serves.
        // Unique per plan: two actions at the same ordinal within one plan would make "execution order"
        // ambiguous, which is exactly the property this table exists to pin down.
        builder.HasIndex(action => new { action.ChangePlanId, action.Ordinal })
            .IsUnique();
    }
}
