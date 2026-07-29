using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Provisioning;

namespace Servyx.Application.Provisioning;

/// <summary>Opt-in dependency-injection registration for the provisioning plan-execution layer.</summary>
public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ProvisioningExecutor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Registers the type that applies provisioning plans, and is deliberately not part of
    /// <c>AddServyxApplication()</c>.</strong> The executor itself performs no provider call — it drives
    /// whichever <c>IProvisioningOperation</c> it is handed — but it is the only sanctioned route by which
    /// one gets driven, so making it opt-in keeps the mutating path visible in a composition root.
    /// </para>
    /// <para>
    /// Requires an <see cref="IProvisioningLedger"/> to already be registered. None is registered here on
    /// purpose: this project can see both a durable implementation and a fake one
    /// (<see cref="InMemoryProvisioningLedger"/>, which is not durable and must never be picked up by
    /// accident in a host that provisions real, billable resources), and picking for the host would make
    /// that choice invisible. A host provisioning real resources calls
    /// <c>AddServyxProvisioningLedger()</c> from <c>Servyx.Infrastructure.Persistence</c>; a test wires the
    /// in-memory one itself.
    /// </para>
    /// <para>
    /// <strong>Registered scoped, not singleton, and the reason is not stylistic.</strong> The durable
    /// ledger — <c>EfProvisioningLedger</c> over <c>ServyxDbContext</c> — is itself scoped, and a singleton
    /// may not capture a scoped dependency. Registering this as a singleton makes the container fail
    /// validation at startup ("Cannot consume scoped service <c>IProvisioningLedger</c> from singleton
    /// <c>ProvisioningExecutor</c>"), which is the correct diagnosis of a real defect: a captured
    /// <c>DbContext</c> shared across every request is exactly the thing the ledger's "one unit of work per
    /// call" remark forbids. Matching the ledger's lifetime is what makes the write-ahead commit belong to
    /// the operation that issued it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxProvisioningExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ProvisioningExecutor>();

        return services;
    }
}
