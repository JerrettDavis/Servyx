using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="User"/> to the <c>Users</c> table. All mapping lives here rather than as attributes on the
/// entity so <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Users");

        builder.HasKey(user => user.Id);

        // Ids are minted by the domain (UserId.New()), never by the store — matches every other entity's Id
        // in this model (HostConfiguration, ServerConfiguration).
        builder.Property(user => user.Id)
            .ValueGeneratedNever();

        builder.Property(user => user.Username)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(user => user.PasswordHash)
            .IsRequired()
            .HasMaxLength(512);

        // Stored as its name, not its ordinal: the ordinal of a value in a hand-maintained enum is an
        // accident of source order, and a reordering would silently reinterpret every existing row. Names
        // also make the table readable to an operator inspecting it during an incident. Matches
        // ServerConfiguration's AdoptionMode/WriteMode convention.
        builder.Property(user => user.Role)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(user => user.IsActive)
            .IsRequired();

        // Two accounts under the same sign-in name would make lookup and future login ambiguous.
        builder.HasIndex(user => user.Username)
            .IsUnique();
    }
}
