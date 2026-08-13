using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="AuditEntry"/> to the <c>AuditEntries</c> table. All mapping lives here rather than as
/// attributes on the entity so <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AuditEntries");

        builder.HasKey(entry => entry.Id);

        // Ids are minted by the writer (AuditLogger / Guid.NewGuid()), never by the store — matches every
        // other entity's Id in this model (UserConfiguration, HostConfiguration, ServerConfiguration).
        builder.Property(entry => entry.Id)
            .ValueGeneratedNever();

        builder.Property(entry => entry.TimestampUtc)
            .IsRequired();

        builder.Property(entry => entry.Actor)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(entry => entry.Action)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(entry => entry.TargetType)
            .HasMaxLength(100);

        builder.Property(entry => entry.TargetId)
            .HasMaxLength(200);

        builder.Property(entry => entry.Details)
            .HasMaxLength(2000);

        // The trail's primary access pattern is "most recent entries first" — see IAuditEntryRepository.ListRecentAsync
        // and this table's own remarks on AuditEntry. Not unique: many entries can share a timestamp.
        builder.HasIndex(entry => entry.TimestampUtc);
    }
}
