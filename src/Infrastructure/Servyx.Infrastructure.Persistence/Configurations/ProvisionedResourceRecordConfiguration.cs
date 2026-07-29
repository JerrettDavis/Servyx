using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Infrastructure.Persistence.Converters;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ProvisionedResourceRecord"/> to the <c>ProvisionedResources</c> table — the write-ahead
/// ledger described on that type.
/// </summary>
public sealed class ProvisionedResourceRecordConfiguration : IEntityTypeConfiguration<ProvisionedResourceRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProvisionedResourceRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProvisionedResources");

        builder.HasKey(record => record.Id);

        // The row id is minted by Servyx before the provider call, so the store must never generate it —
        // a store-generated key would only exist after the insert, which is too late to be useful.
        builder.Property(record => record.Id)
            .ValueGeneratedNever();

        builder.Property(record => record.ProvisionerId)
            .IsRequired()
            .HasMaxLength(64);

        // Nullable: an Intended row is written before the provider has assigned an id.
        builder.Property(record => record.ProviderResourceId)
            .HasMaxLength(256);

        builder.Property(record => record.Region)
            .HasMaxLength(64);

        // Same JSON + value-comparer pairing as ProviderAccount.CredentialUrns; see JsonCollectionConverters.
        builder.Property(record => record.Tags)
            .IsRequired()
            .HasColumnName("TagsJson")
            .HasConversion(JsonCollectionConverters.StringDictionary, JsonCollectionConverters.StringDictionaryComparer);

        // Stored by name. An orphan sweep filters on this column and a human reads it during an incident;
        // neither is served by an integer whose meaning depends on the source order of the enum.
        builder.Property(record => record.State)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(record => record.JobId)
            .HasMaxLength(128);

        // Composite index for "what does this provisioner think it owns", the lookup an orphan sweep performs
        // once per provider resource it finds. Not unique: several Intended rows legitimately carry a null
        // ProviderResourceId at the same time, and reconciliation, not a constraint, is what resolves them.
        builder.HasIndex(record => new { record.ProvisionerId, record.ProviderResourceId });

        // Standalone index on the state column so "find every Intended row" — the sweep's entry query — stays
        // cheap as the ledger grows and never degrades into a full table scan.
        builder.HasIndex(record => record.State);
    }
}
