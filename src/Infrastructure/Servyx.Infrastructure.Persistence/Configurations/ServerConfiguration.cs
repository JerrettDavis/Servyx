using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Server"/> to the <c>Servers</c> table. All mapping lives here rather than as attributes on
/// the entity so <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Servers");

        builder.HasKey(server => server.Id);

        // Ids are minted by the domain (ServerId.New()), never by the store — a store-generated key would
        // mean the id does not exist until after the insert, which breaks write-ahead record keeping.
        builder.Property(server => server.Id)
            .ValueGeneratedNever();

        builder.Property(server => server.Name)
            .IsRequired()
            .HasMaxLength(200);

        // Matches ServerDefinitionBindingRecord.ServerId's own maxLength — both columns hold the same kind
        // of value (a discovery-native container id), so they share the same bound.
        builder.Property(server => server.ContainerId)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(server => server.GameDefinitionId)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(server => server.DefinitionContentHash)
            .IsRequired()
            .HasMaxLength(128);

        // Nullable: Server.HostId is set on adoption only when the discovered container's host resolves to a
        // database-registered Host row, and stays null for one adopted from local/non-SSH discovery or from a
        // configuration-declared host that has no row of its own — see Server.HostId's own remarks. Not
        // .IsRequired(); the EF convention already leaves a nullable value-type property optional, this line
        // just makes the "no .IsRequired() here" absence a deliberate, documented one rather than a silent
        // omission.
        builder.Property(server => server.HostId);

        // The durable identity adoption correlates "already tracked" on. One container can be adopted at
        // most once — a second AdoptAsync call for the same container id must land on AlreadyAdopted, never
        // a second row — so this is enforced at the database, not only in ServerAdoptionService's own
        // pre-check.
        builder.HasIndex(server => server.ContainerId)
            .IsUnique();

        // Enums are stored as their names, not their ordinals: the ordinal of a value in a hand-maintained
        // enum is an accident of source order, and a reordering would silently reinterpret every existing
        // row. Names also make the table readable to an operator inspecting it during an incident.
        builder.Property(server => server.AdoptionMode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(server => server.WriteMode)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(server => server.WriteModeChangedBy)
            .HasMaxLength(200);

        // Seeded false by an explicit default rather than left to the CLR default alone: the migration adds
        // this column to a table that already has rows, and every one of those servers must read as "not
        // mirroring" — mirroring is opt-in, and a backfill of true would silently enable derived-surface
        // writes on servers whose operators never asked for them.
        builder.Property(server => server.MirrorDerivedSurfaces)
            .IsRequired()
            .HasDefaultValue(false);

        // Attribution columns mirror WriteModeChangedBy/At exactly, bound included — same kind of fact, same
        // shape, so a query that reports one can report the other without special-casing.
        builder.Property(server => server.MirrorDerivedSurfacesChangedBy)
            .HasMaxLength(200);

        builder.HasIndex(server => server.HostId);
        builder.HasIndex(server => server.GameDefinitionId);
    }
}
