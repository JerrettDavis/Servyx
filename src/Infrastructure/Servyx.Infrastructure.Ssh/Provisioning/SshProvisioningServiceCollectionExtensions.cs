using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Provisioning;

/// <summary>
/// Opt-in dependency-injection registration for SSH <em>provisioning</em>.
/// </summary>
public static class SshProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SshProcessProvisioner"/> as an <see cref="IProvisioner"/> for the host at
    /// <paramref name="endpoint"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxSsh()</c> registers only the transport and the
    /// connector pool; nothing it registers can install or remove anything. Everything reachable from
    /// <see cref="SshProcessProvisioner"/>'s <see cref="IProvisioner"/> surface is likewise read-only
    /// (<c>PlanAsync</c> opens no connection at all; <c>RefreshAsync</c> reads one file; <c>ReconcileAsync</c>
    /// lists a directory), but <c>CreateOperation</c> and <c>DestroyAsync</c> are not, and an install created
    /// here downloads gigabytes onto someone's machine.
    /// </para>
    /// <para>
    /// Calling this method is therefore an explicit decision by a composition root to make installation and
    /// destruction reachable. Do not fold it into <c>AddServyxSsh()</c> "for convenience": the separation is
    /// what lets anyone reading a composition root see, without tracing a dependency graph, whether that
    /// process can mutate infrastructure. Milestone 1 hosts must not call it.
    /// </para>
    /// <para>
    /// Requires an SSH <see cref="ITransport"/> to already be registered — normally by <c>AddServyxSsh()</c>.
    /// The transport is selected out of the registered set <em>by <see cref="ITransport.TransportId"/></em>,
    /// never by position, so adding another transport to the composition root cannot silently hand this
    /// provisioner a Docker connection.
    /// </para>
    /// <para>
    /// <paramref name="endpoint"/> is the single source for both halves of the handoff: the provisioner
    /// connects with the very <c>TargetDescriptor</c> it stamps onto the resources it produces, so the host
    /// installed on and the host recorded in the ledger cannot diverge.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="endpoint">The SSH endpoint, in <see cref="SshEndpoint"/>'s <c>[user@]host[:port]</c> form.</param>
    /// <param name="credentialUrn">The secret-store URN of the host's SSH credentials. Never a literal credential.</param>
    /// <param name="transportOptions">Additional descriptor options the SSH transport reads (trust policy, pinned fingerprints, and so on).</param>
    /// <param name="markerRoot">
    /// Where marker files are written and swept from. Defaults to
    /// <see cref="SshProcessProvisioner.DefaultMarkerRoot"/>.
    /// </param>
    public static IServiceCollection AddServyxSshProvisioning(
        this IServiceCollection services,
        string endpoint,
        string? credentialUrn = null,
        IReadOnlyDictionary<string, string>? transportOptions = null,
        string? markerRoot = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        services.AddSingleton(sp =>
        {
            var transport = sp.GetServices<ITransport>()
                .SingleOrDefault(t => string.Equals(t.TransportId, SshProcessProvisioner.SshTransportId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"No ITransport with TransportId '{SshProcessProvisioner.SshTransportId}' is registered. " +
                    "Call AddServyxSsh() before AddServyxSshProvisioning().");

            return new SshProcessProvisioner(transport, endpoint, credentialUrn, transportOptions, markerRoot);
        });

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<SshProcessProvisioner>());

        // The maintenance half — update planning and drift detection — rides on the same instance and is
        // published here rather than from AddServyxSsh(), mirroring AddServyxDockerProvisioning(). Every
        // member of IMaintainer is read-only, so this line grants no new mutating capability; it stays behind
        // this opt-in method so a host with the flag off — which never calls this method — has no IMaintainer
        // at all, exactly as it has no IProvisioner.
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<SshProcessProvisioner>());

        return services;
    }
}
