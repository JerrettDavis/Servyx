using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Infrastructure.Persistence.Converters;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ServerDefinitionBindingRecord"/> to the <c>ServerDefinitionBindings</c> table.</summary>
public sealed class ServerDefinitionBindingRecordConfiguration : IEntityTypeConfiguration<ServerDefinitionBindingRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServerDefinitionBindingRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServerDefinitionBindings");

        builder.HasKey(record => record.ServerId);

        // The discovery-native container id is the natural key; Servyx never mints one, so the store must
        // never generate it.
        builder.Property(record => record.ServerId)
            .IsRequired()
            .HasMaxLength(256)
            .ValueGeneratedNever();

        // Stored by name, matching ProvisionedResourceRecord.State — a human reads this column while
        // diagnosing an ambiguous or stale binding, and an integer whose meaning depends on enum
        // declaration order would not survive a reorder.
        builder.Property(record => record.State)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(record => record.DefinitionId)
            .HasMaxLength(128);

        builder.Property(record => record.DefinitionContentHash)
            .HasMaxLength(128);

        builder.Property(record => record.DefinitionSourceId)
            .HasMaxLength(128);

        builder.Property(record => record.DefinitionSourcePath)
            .HasMaxLength(1024);

        // Same JSON + value-comparer pairing as ProvisionedResourceRecord.Tags; see JsonCollectionConverters.
        builder.Property(record => record.CandidateDefinitionIds)
            .IsRequired()
            .HasColumnName("CandidateDefinitionIdsJson")
            .HasConversion(JsonCollectionConverters.StringList, JsonCollectionConverters.StringListComparer);

        // A rebind sweep (future work — see IServerDefinitionBindingStore's remarks) would query "every
        // Ambiguous or NeedsRebind row" as its entry point; indexed now so that query never needs a full
        // table scan once the table has grown.
        builder.HasIndex(record => record.State);
    }
}
