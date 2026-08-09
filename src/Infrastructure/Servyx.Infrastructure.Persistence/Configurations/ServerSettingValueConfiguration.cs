using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servyx.Domain.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ServerSettingValue"/> to the <c>ServerSettingValues</c> table. All mapping lives here
/// rather than as attributes on the entity, matching <see cref="ServerConfiguration"/>, so
/// <c>Servyx.Domain</c> keeps zero persistence dependencies.
/// </summary>
public sealed class ServerSettingValueConfiguration : IEntityTypeConfiguration<ServerSettingValue>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServerSettingValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServerSettingValues");

        // Composite key: one desired value per (server, setting key). A re-save overwrites the existing row
        // rather than appending a history row — see ServerSettingValue's own remarks.
        builder.HasKey(row => new { row.ServerId, row.Key });

        builder.Property(row => row.ServerId)
            .ValueGeneratedNever();

        // Matches SettingDescriptor.Key's own shape — a definition-schema key, not a surface binding key —
        // and is short by convention (e.g. "admin-password"), but given headroom well past any observed key.
        builder.Property(row => row.Key)
            .IsRequired()
            .HasMaxLength(200);

        // No max length: a desired value's shape is entirely setting-defined (a Text setting may be a long
        // multi-line message-of-the-day), and this table has no opinion about SettingType.
        builder.Property(row => row.Value)
            .IsRequired();

        builder.Property(row => row.UpdatedBy)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(row => row.UpdatedAt)
            .IsRequired();

        // No separate index on ServerId: it is the leading column of the composite primary key above, so
        // IServerSettingsService.LoadAsync's "every row for this server" query already uses that index — a
        // second one would duplicate it for no benefit.

        // Real referential integrity to Server.Id, with cascade delete: forgetting a server must discard its
        // desired values, not leave them orphaned. No navigation property either side — Server stays
        // persistence-ignorant (see its own remarks) — so this is a foreign key declared without navigation.
        // Deliberately a divergence from ServerDefinitionBindings, which Phase 1's ForgetAsync leaves behind
        // on purpose: that row is discovery-layer state serving ServerQueryService, independent of whether
        // Servyx is still tracking the server for adoption purposes. A desired setting value is operator
        // intent ABOUT a tracked server and must not outlive the tracking — otherwise a later re-adopt of the
        // same container (which mints a brand new ServerId; see ServerAdoptionService.AdoptAsync) could never
        // see the orphaned row again in practice, but leaving it to accumulate forever is still the wrong
        // default for a table whose entire reason to exist is "current intent for a server Servyx tracks".
        builder.HasOne<Server>()
            .WithMany()
            .HasForeignKey(row => row.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
