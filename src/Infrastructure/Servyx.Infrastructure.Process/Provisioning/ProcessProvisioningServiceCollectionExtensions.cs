using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Process.Provisioning;

/// <summary>
/// Opt-in dependency-injection registration for local-process <em>provisioning</em>.
/// </summary>
public static class ProcessProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LocalProcessProvisioner"/> as an <see cref="IProvisioner"/> for the machine Servyx
    /// is running on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxLocalProcess()</c> registers only the transport;
    /// nothing it registers can install or remove anything. Everything reachable from
    /// <see cref="LocalProcessProvisioner"/>'s <see cref="IProvisioner"/> surface is likewise read-only
    /// (<c>PlanAsync</c> opens no session at all; <c>RefreshAsync</c> reads one file; <c>ReconcileAsync</c>
    /// lists a directory), but <c>CreateOperation</c> and <c>DestroyAsync</c> are not, and an install created
    /// here downloads gigabytes onto the machine the panel itself is running on.
    /// </para>
    /// <para>
    /// Calling this method is therefore an explicit decision by a composition root to make installation and
    /// destruction reachable. Do not fold it into <c>AddServyxLocalProcess()</c> "for convenience": the
    /// separation is what lets anyone reading a composition root see, without tracing a dependency graph,
    /// whether that process can mutate infrastructure. Milestone 1 hosts must not call it.
    /// </para>
    /// <para>
    /// Requires a local <see cref="ITransport"/> to already be registered — normally by
    /// <c>AddServyxLocalProcess()</c>. The transport is selected out of the registered set <em>by
    /// <see cref="ITransport.TransportId"/></em>, never by position, so adding another transport to the
    /// composition root cannot silently hand this provisioner a Docker or SSH connection.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="machineId">
    /// A stable name for this machine, stamped into every descriptor the provisioner produces. Defaults to
    /// <see cref="Environment.MachineName"/>.
    /// </param>
    /// <param name="credentialUrn">The secret-store URN of any credentials the target needs. Never a literal credential.</param>
    /// <param name="transportOptions">Additional descriptor options.</param>
    /// <param name="markerRoot">
    /// Where marker files are written and swept from. Defaults to
    /// <see cref="LocalProcessProvisioner.DefaultMarkerRoot"/>.
    /// </param>
    public static IServiceCollection AddServyxProcessProvisioning(
        this IServiceCollection services,
        string? machineId = null,
        string? credentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string? markerRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var transport = sp.GetServices<ITransport>()
                .SingleOrDefault(t => string.Equals(t.TransportId, LocalProcessProvisioner.LocalTransportId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No ITransport with TransportId '{LocalProcessProvisioner.LocalTransportId}' is registered. " +
                    "Call AddServyxLocalProcess() before AddServyxProcessProvisioning().");

            return new LocalProcessProvisioner(transport, machineId, credentialUrn, transportOptions, markerRoot);
        });

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<LocalProcessProvisioner>());

        return services;
    }
}
