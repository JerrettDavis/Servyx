using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ChangePlanRecord"/> to the <c>ChangePlans</c> table.
/// </summary>
public sealed class ChangePlanRecordConfiguration : IEntityTypeConfiguration<ChangePlanRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ChangePlanRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ChangePlans");

        builder.HasKey(plan => plan.Id);

        // Minted by the previewer (ChangePlanId.New()), never by the store — the planId must exist before the
        // row is ever inserted, since it is handed back to the caller from PreviewAsync before ApplyAsync
        // (across a Blazor Server circuit boundary) can ever see it.
        builder.Property(plan => plan.Id)
            .ValueGeneratedNever();

        builder.Property(plan => plan.ServerId)
            .IsRequired();

        // Stored by name, matching ServerConfiguration/ProvisionedResourceRecordConfiguration: a human reads
        // this column while diagnosing a stuck or failed apply, and an integer whose meaning depends on enum
        // declaration order would not survive a reorder.
        builder.Property(plan => plan.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(plan => plan.CreatedAt)
            .IsRequired();

        // The sortable twin of CreatedAt. Mapped explicitly (rather than left to convention) because it has a
        // private setter and exists solely so ORDER BY works: see ChangePlanRecord.CreatedAtTicks for why a
        // DateTimeOffset column cannot carry that job on SQLite.
        builder.Property(plan => plan.CreatedAtTicks)
            .IsRequired();

        builder.Property(plan => plan.CreatedBy)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(plan => plan.ExpiresAt)
            .IsRequired();

        builder.Property(plan => plan.AppliedAt);

        builder.Property(plan => plan.AppliedBy)
            .HasMaxLength(200);

        builder.Property(plan => plan.RevertedAt);

        // Same max length as AppliedBy/CreatedBy: all three hold the same kind of value, Servyx's one shared
        // operator identity.
        builder.Property(plan => plan.RevertedBy)
            .HasMaxLength(200);

        builder.Property(plan => plan.DefinitionId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(plan => plan.DefinitionVersion)
            .IsRequired()
            .HasMaxLength(128);

        // No max length on the JSON columns below: each is an opaque, pre-serialized payload the writer owns
        // the shape of, not a user-facing field — clipping it would corrupt the document rather than reject
        // the write, matching ProviderAccount.CredentialUrns's own "deliberately left without a max length"
        // reasoning for JSON payload columns.
        builder.Property(plan => plan.ConsequencesJson)
            .IsRequired();

        builder.Property(plan => plan.SurfaceHashesJson)
            .IsRequired();

        builder.Property(plan => plan.BlockedJson)
            .IsRequired();

        builder.Property(plan => plan.DiagnosticsJson)
            .IsRequired();

        // Optimistic concurrency token: this is what makes a double-apply impossible. Two concurrent attempts
        // to transition Status on the same row race on this column, and the second SaveChanges throws
        // DbUpdateConcurrencyException instead of silently applying the plan twice.
        //
        // Deliberately NOT IsRowVersion()/ValueGeneratedOnAddOrUpdate() with a ValueGenerator: ServyxDbContext
        // is provider-agnostic by design (SQLite today, PostgreSQL a required drop-in swap — see its own
        // remarks), and a "let EF/the value-generation pipeline treat this as store-generated" configuration
        // makes EF omit the column from the INSERT statement and expect the database to supply a value via a
        // trigger or computed default — which SQLite has none of here, and which is exactly the kind of
        // provider-specific behavior this DbContext forbids. Instead this is a plain column, and
        // ServyxDbContext.SaveChanges/SaveChangesAsync assign it a fresh Guid for every Added/Modified
        // ChangePlanRecord immediately before the save — a portable, application-computed concurrency token
        // that works identically on every provider.
        builder.Property(plan => plan.RowVersion)
            .IsConcurrencyToken();

        // Real referential integrity to Server.Id, with cascade delete: a plan is meaningless without the
        // server it targets, so forgetting a server must discard its plans — the opposite lifecycle rule from
        // ProvisionedResourceRecord, which deliberately carries no FK because a leaked billable resource must
        // outlive the entity that requested it. See ChangePlanRecord's own remarks.
        builder.HasOne<Server>()
            .WithMany()
            .HasForeignKey(plan => plan.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        // A server detail page's "recent plans for this server" query is the expected entry point for reading
        // this table, and it is one query: filter by ServerId, order by CreatedAtTicks descending, take a
        // page. Composite in exactly that order so the index serves both halves — a plain ServerId index
        // would still leave the storage engine sorting a whole server's history to return twenty-five rows.
        //
        // This REPLACES the previous single-column ServerId index rather than sitting alongside it: an index
        // whose leading column is ServerId already answers every ServerId-only lookup (including the foreign
        // key's), so keeping both would be a second copy of the same data to maintain on every insert.
        builder.HasIndex(plan => new { plan.ServerId, plan.CreatedAtTicks });

        // A future expiry sweep (promoting stale Previewed rows to Stale) filters on Status the same way an
        // orphan sweep filters ProvisionedResourceRecord.State — indexed for the same reason.
        builder.HasIndex(plan => plan.Status);
    }
}
