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

    /// <summary>
    /// Previewed configuration change plans, from preview through apply/revert. Persistence only — see
    /// <see cref="ChangePlanRecord"/>'s own remarks; no <c>IPlanExecutor</c> is wired to this table yet.
    /// </summary>
    public DbSet<ChangePlanRecord> ChangePlans => Set<ChangePlanRecord>();

    /// <summary>The ordered actions making up each <see cref="ChangePlanRecord"/>. See <see cref="ChangePlanActionRecord"/>'s own remarks.</summary>
    public DbSet<ChangePlanActionRecord> ChangePlanActions => Set<ChangePlanActionRecord>();

    /// <summary>
    /// Servyx user accounts — the durable store behind the identity/RBAC foundation. Not yet consulted by
    /// authentication; see <see cref="User"/>'s own remarks.
    /// </summary>
    public DbSet<User> Users => Set<User>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<ServerId>().HaveConversion<ServerIdConverter>();
        configurationBuilder.Properties<HostId>().HaveConversion<HostIdConverter>();
        configurationBuilder.Properties<ChangePlanId>().HaveConversion<ChangePlanIdConverter>();
        configurationBuilder.Properties<UserId>().HaveConversion<UserIdConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ServyxDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    // Only these two overloads need overriding. DbContext.SaveChanges() and
    // SaveChangesAsync(CancellationToken) are themselves virtual, but their base implementations do nothing
    // but forward to SaveChanges(bool)/SaveChangesAsync(bool, CancellationToken) with acceptAllChangesOnSuccess:
    // true — and because that forwarding call is a virtual dispatch (`this.SaveChanges(true)`, not
    // `base.SaveChanges(true)`), it resolves to the override below at runtime regardless of which of the four
    // public entry points the caller used. This is the same pattern EF Core's own SaveChangesInterceptor
    // guidance overrides for auditing/timestamp scenarios, for the same reason. Proven empirically here too:
    // every test in ChangePlanRecordTests.cs calls the parameterless SaveChanges(), and
    // RowVersion_PreventsADoubleApply_WhenTwoContextsRaceToTransitionStatus only passes if the parameterless
    // call actually rotated RowVersion on the first attempt's save — if it hadn't, the second attempt's stale
    // token would still match and no DbUpdateConcurrencyException would be thrown.

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AssignFreshChangePlanRowVersions();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AssignFreshChangePlanRowVersions();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Assigns a fresh <see cref="Guid"/> to <see cref="ChangePlanRecord.RowVersion"/> for every plan about to
    /// be inserted or updated, immediately before the save. This is what actually makes
    /// <see cref="ChangePlanRecord.RowVersion"/> function as an optimistic concurrency token: see
    /// <c>ChangePlanRecordConfiguration</c>'s own remarks for why this is done here, in application code,
    /// rather than through <c>IsRowVersion()</c>/a database-generated value — the latter would be
    /// provider-specific behavior this provider-agnostic context does not allow.
    /// </summary>
    /// <remarks>
    /// SCOPED TO <see cref="ChangePlanRecord"/> ONLY, deliberately — it is the only entity in this model that
    /// carries a concurrency token today. This is NOT a generic "rotate every IsConcurrencyToken() Guid
    /// property on every entity" sweep: a model-metadata-driven version of that was considered and rejected
    /// for this phase as unwarranted complexity (reflecting over every tracked entry's properties on every
    /// SaveChanges call) for a single call site. <strong>If a second entity is ever given a Guid concurrency
    /// token, this method must be updated to cover it too</strong> — nothing here discovers that
    /// automatically.
    /// </remarks>
    private void AssignFreshChangePlanRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<ChangePlanRecord>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.NewGuid();
            }
        }
    }
}
