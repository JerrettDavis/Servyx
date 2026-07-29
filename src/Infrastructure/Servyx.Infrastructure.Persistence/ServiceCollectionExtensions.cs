using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Provisioning;
using Servyx.Infrastructure.Persistence.Interceptors;
using Servyx.Infrastructure.Persistence.Provisioning;

namespace Servyx.Infrastructure.Persistence;

/// <summary>Dependency-injection registration for the Servyx persistence implementation.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ServyxDbContext"/> against SQLite at <paramref name="connectionString"/>, together
    /// with the <see cref="SqliteWriteAheadLogInterceptor"/> that puts each opened connection into WAL mode.
    /// </summary>
    /// <remarks>
    /// Does not apply migrations. Schema creation is a deployment decision — the caller (an app host, a
    /// migration job, or a test fixture) is responsible for invoking <c>Database.Migrate()</c> at whatever
    /// point in startup it considers safe, rather than having a DI registration mutate the database as a side
    /// effect of composing the container.
    /// </remarks>
    public static IServiceCollection AddServyxPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton<SqliteWriteAheadLogInterceptor>();

        services.AddDbContext<ServyxDbContext>((sp, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(sp.GetRequiredService<SqliteWriteAheadLogInterceptor>()));

        return services;
    }

    /// <summary>
    /// Registers <see cref="EfProvisioningLedger"/> as the <see cref="IProvisioningLedger"/>, backed by the
    /// <see cref="ServyxDbContext"/> that <see cref="AddServyxPersistence"/> registers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A sibling of <see cref="AddServyxPersistence"/> rather than part of it, on purpose.</strong>
    /// <see cref="AddServyxPersistence"/> registers storage and nothing else — it does not even apply
    /// migrations — so a host that only reads inventory gets a database and no opinion about provisioning.
    /// Binding <see cref="IProvisioningLedger"/> is a different decision: it declares which implementation
    /// wins in a container that may also see the non-durable
    /// <c>Servyx.Application.Provisioning.InMemoryProvisioningLedger</c>. Keeping it a separate call means
    /// the choice is written down in the composition root, where an operator can read it, instead of
    /// arriving as a side effect of asking for a database.
    /// </para>
    /// <para>
    /// Registered scoped, matching <see cref="ServyxDbContext"/>'s own lifetime. Because every ledger method
    /// commits, resolve it inside a scope dedicated to the provisioning operation rather than one shared
    /// with unrelated pending writes.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxProvisioningLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IProvisioningLedger, EfProvisioningLedger>();

        return services;
    }
}
