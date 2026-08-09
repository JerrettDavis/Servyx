using Microsoft.EntityFrameworkCore;
using Servyx.Domain.Common;
using Servyx.Domain.Entities;
using Servyx.Infrastructure.Persistence.Converters;
using Servyx.Infrastructure.Persistence.Entities;

namespace Servyx.Infrastructure.Persistence;

/// <summary>
/// The Servyx control-plane database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provider-agnostic by construction.</strong> SQLite is the default and the only provider wired up
/// today (see <c>ServiceCollectionExtensions.AddServyxPersistence</c>), but PostgreSQL has to stay a drop-in
/// alternative, so nothing in this model may be SQLite-specific: no explicit column types, no raw SQL, no
/// provider default-value expressions. Everything relational is expressed through
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes in <c>Configurations/</c>, and the one genuinely
/// SQLite-only behaviour Servyx wants — WAL journalling — is applied to the connection by an interceptor
/// registered alongside the provider, never through the model.
/// </para>
/// <para>
/// <strong>Timestamps.</strong> All time columns are <see cref="DateTimeOffset"/>, matching the domain
/// entities (<c>Server.CreatedAt</c>, <c>Host.CreatedAt</c>, <c>ProviderAccount.CreatedAt</c>) rather than
/// converting to <see cref="DateTime"/>. An offset-carrying type cannot lose its zone the way a
/// <see cref="DateTime"/> with an unenforced <see cref="DateTimeKind"/> can, and both SQLite (sortable ISO-8601
/// text) and PostgreSQL (<c>timestamptz</c>) map it natively. Callers are expected to write UTC values.
/// </para>
/// <para>
/// <strong>Strongly-typed ids.</strong> <see cref="ServerId"/> and <see cref="HostId"/> converters are
/// registered once in <see cref="ConfigureConventions"/> rather than repeated per entity configuration. The
/// convention route was chosen deliberately: it covers nullable occurrences
/// (<c>ProvisionedResourceRecord.ServerId</c>) and every entity added later without anyone having to remember,
/// whereas a per-property registration fails open — a forgotten one surfaces as an obscure "cannot be mapped"
/// model error at best, and a silently different column shape at worst.
/// </para>
/// </remarks>
public sealed class ServyxDbContext : DbContext
{
    /// <summary>Creates the context with the given options.</summary>
    public ServyxDbContext(DbContextOptions<ServyxDbContext> options)
        : base(options)
    {
    }

    /// <summary>Game servers Servyx knows about, adopted or provisioned.</summary>
    public DbSet<Server> Servers => Set<Server>();

    /// <summary>Machines Servyx can reach, on which servers run.</summary>
    public DbSet<Host> Hosts => Set<Host>();

    /// <summary>Configured infrastructure-provider accounts.</summary>
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();

    /// <summary>
    /// The write-ahead ledger of provider resources. See <see cref="ProvisionedResourceRecord"/> for the
    /// intent-before-billable-call invariant this table exists to guarantee.
    /// </summary>
    public DbSet<ProvisionedResourceRecord> ProvisionedResources => Set<ProvisionedResourceRecord>();

    /// <summary>
    /// Which game definition (by content hash) governs each discovered server. See
    /// <see cref="ServerDefinitionBindingRecord"/> and <c>IServerDefinitionBindingStore</c>.
    /// </summary>
    public DbSet<ServerDefinitionBindingRecord> ServerDefinitionBindings => Set<ServerDefinitionBindingRecord>();

    /// <summary>
    /// An operator's recorded DESIRED per-server setting values — intent only, never applied to a running
    /// server. See <see cref="ServerSettingValue"/>'s own remarks.
    /// </summary>
    public DbSet<ServerSettingValue> ServerSettingValues => Set<ServerSettingValue>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<ServerId>().HaveConversion<ServerIdConverter>();
        configurationBuilder.Properties<HostId>().HaveConversion<HostIdConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServyxDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
