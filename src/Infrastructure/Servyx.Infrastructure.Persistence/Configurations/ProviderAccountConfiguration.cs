using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Converters;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ProviderAccount"/> to the <c>ProviderAccounts</c> table. All mapping lives here rather than
/// as attributes on the entity so <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class ProviderAccountConfiguration : IEntityTypeConfiguration<ProviderAccount>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ProviderAccount> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ProviderAccounts");

        builder.HasKey(account => account.Id);

        builder.Property(account => account.Id)
            .ValueGeneratedNever()
            .HasMaxLength(128);

        builder.Property(account => account.ProviderId)
            .IsRequired()
            .HasMaxLength(64);

        builder.Property(account => account.DisplayName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(account => account.DefaultRegion)
            .HasMaxLength(64);

        // Stored as a JSON array in a single column, with an explicit value comparer. The comparer is not
        // decoration: without it EF compares the converted collection by reference, never snapshots it, and
        // an in-place edit to the list on a tracked entity is silently dropped by SaveChanges. Deliberately
        // left without a max length — this is a JSON payload, not a user-facing field, and clipping it would
        // corrupt the document rather than reject the write.
        builder.Property(account => account.CredentialUrns)
            .IsRequired()
            .HasColumnName("CredentialUrnsJson")
            .HasConversion(JsonCollectionConverters.StringList, JsonCollectionConverters.StringListComparer);

        builder.Property(account => account.ScopeHint)
            .HasMaxLength(512);

        builder.HasIndex(account => account.ProviderId);
    }
}
