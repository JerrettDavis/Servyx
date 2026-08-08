using Microsoft.Extensions.DependencyInjection;

namespace Servyx.Mcp;

/// <summary>
/// Registers this assembly's MCP tool surface's own supporting services (as distinct from
/// <c>AddServyxCore</c>, which every host — this one included — calls separately to register the shared
/// composition root the tools read from).
/// </summary>
/// <remarks>
/// <c>IProvisioningDashboard</c> is registered <b>Scoped</b> by <c>AddServyxCore</c> — it rides an
/// <c>EfProvisioningLedger</c>-backed <c>ProvisioningExecutor</c>, both of which ride a
/// <c>ServyxDbContext</c> — so if a provisioning tool is ever added to this assembly it must take an
/// <c>IServiceScopeFactory</c> and create an explicit scope per call, exactly as
/// <c>ScheduledBackupService</c> and Blazor Server's own circuit scope already do for the same service.
/// Resolving <c>IProvisioningDashboard</c> directly from a stdio host's root <see cref="IServiceProvider"/>
/// — there is no per-request scope in a stdio process the way there is a circuit in Blazor Server or a
/// request in ASP.NET Core — throws "Cannot resolve scoped service from root provider" the first time the
/// tool is invoked.
/// </remarks>
public static class ServyxMcpToolsServiceCollectionExtensions
{
    /// <summary>
    /// Registers this assembly's own tool-supporting services. No tools are registered here — MCP tool
    /// discovery is <c>WithToolsFromAssembly</c>'s job, driven by <c>[McpServerToolType]</c>, at each
    /// host's own composition call site — so this method has nothing to add today and exists as the one
    /// place a future tool's supporting services would be registered.
    /// </summary>
    public static IServiceCollection AddServyxMcpTools(this IServiceCollection services) => services;
}
