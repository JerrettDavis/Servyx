using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Host"/> to the <c>Hosts</c> table. All mapping lives here rather than as attributes on the
/// entity so <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class HostConfiguration : IEntityTypeConfiguration<Host>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Host> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Hosts");

        builder.HasKey(host => host.Id);

        builder.Property(host => host.Id)
            .ValueGeneratedNever();

        builder.Property(host => host.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(host => host.ConnectorId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(host => host.ProvisionedByJobId)
            .HasMaxLength(128);

        builder.Property(host => host.ProviderResourceId)
            .HasMaxLength(256);

        builder.Property(host => host.ProviderAccountId)
            .HasMaxLength(128);

        builder.Property(host => host.Endpoint)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(host => host.CredentialUrn)
            .HasMaxLength(512);

        builder.Property(host => host.TrustPolicy)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(host => host.PinnedFingerprints)
            .HasMaxLength(1024);

        builder.Property(host => host.RegisteredBy)
            .HasMaxLength(200);

        builder.HasIndex(host => host.ConnectorId);

        // Two rows for the same human-chosen name would make registration and lookup ambiguous.
        builder.HasIndex(host => host.Name)
            .IsUnique();

        // Answers "which host is this provider resource?" during an orphan sweep without a table scan.
        builder.HasIndex(host => new { host.ProviderAccountId, host.ProviderResourceId });
    }
}
