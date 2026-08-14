using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Servyx.Infrastructure.Persistence.Converters;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="ServerStatusSnapshot"/> to the <c>ServerStatusSnapshots</c> table.</summary>
public sealed class ServerStatusSnapshotConfiguration : IEntityTypeConfiguration<ServerStatusSnapshot>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    /// <summary>
    /// Converts <see cref="ServerStatusSnapshot.Ports"/> to and from a JSON array. Scoped to this
    /// configuration rather than added to the shared <see cref="JsonCollectionConverters"/> — the port list
    /// shape is specific to this one entity, unlike the string list/dictionary converters shared there.
    /// </summary>
    private static readonly ValueConverter<IReadOnlyList<ServerPortSnapshot>, string> PortsConverter = new(
        ports => JsonSerializer.Serialize(ports, SerializerOptions),
        json => JsonSerializer.Deserialize<List<ServerPortSnapshot>>(json, SerializerOptions) ?? new List<ServerPortSnapshot>());

    /// <summary>Structural comparer for <see cref="PortsConverter"/> — see <see cref="JsonCollectionConverters"/>'s remarks on why every JSON-backed collection needs one.</summary>
    private static readonly ValueComparer<IReadOnlyList<ServerPortSnapshot>> PortsComparer = new(
        (left, right) => left!.SequenceEqual(right!),
        ports => ports.Aggregate(0, (hash, port) => HashCode.Combine(hash, port)),
        ports => ports.ToList());

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ServerStatusSnapshot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ServerStatusSnapshots");

        builder.HasKey(row => row.ContainerId);

        // The discovery-native container id is the natural key; Servyx never mints one, so this row must
        // never generate it — same rule as ServerDefinitionBindingRecord.ServerId.
        builder.Property(row => row.ContainerId)
            .IsRequired()
            .HasMaxLength(256)
            .ValueGeneratedNever();

        builder.Property(row => row.Name)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(row => row.Game)
            .IsRequired()
            .HasMaxLength(256);

        // Stored by name, not ordinal — a human reads this column while diagnosing a stale cache entry, and
        // an integer whose meaning depends on enum declaration order would not survive a reorder. Same
        // convention as ServerDefinitionBindingRecordConfiguration.State.
        builder.Property(row => row.State)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(row => row.Health)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(row => row.HealthDetail)
            .HasMaxLength(2000);

        builder.Property(row => row.Host)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(row => row.HostKey)
            .HasMaxLength(256);

        builder.Property(row => row.BindingStatus)
            .IsRequired()
            .HasMaxLength(32);

        // Same JSON + value-comparer pairing ServerDefinitionBindingRecord.CandidateDefinitionIds uses.
        builder.Property(row => row.AmbiguousCandidateGameIds)
            .IsRequired()
            .HasColumnName("AmbiguousCandidateGameIdsJson")
            .HasConversion(JsonCollectionConverters.StringList, JsonCollectionConverters.StringListComparer);

        builder.Property(row => row.Ports)
            .IsRequired()
            .HasColumnName("PortsJson")
            .HasConversion(PortsConverter, PortsComparer);

        builder.Property(row => row.PlayersOnline);

        builder.Property(row => row.PlayersMax);

        builder.Property(row => row.UpdatedAt)
            .IsRequired();

        // The background refresh worker reads "everything last updated before X" nowhere today, but a
        // future staleness sweep would, and an unindexed scan over this table only gets worse as more
        // servers are adopted — indexed now for the same forward-looking reason
        // ServerDefinitionBindingRecordConfiguration indexes State.
        builder.HasIndex(row => row.UpdatedAt);
    }
}
