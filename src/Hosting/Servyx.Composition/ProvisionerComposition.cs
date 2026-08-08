using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Connectors;
using Servyx.Domain.Provisioning;
using Servyx.Domain.Secrets;
using Servyx.Domain.Transport;
using Servyx.Infrastructure.Aws.Provisioning;
using Servyx.Infrastructure.Azure.Provisioning;
using Servyx.Infrastructure.DigitalOcean.Provisioning;
using Servyx.Infrastructure.Process;
using Servyx.Infrastructure.Process.Provisioning;
using Servyx.Infrastructure.Ssh;
using Servyx.Infrastructure.Ssh.Provisioning;

namespace Servyx.Composition;

/// <summary>
/// Registers the provisioners <see cref="ProvisionerWiringOptions"/> found enabled, so /deploy can offer more
/// than one target.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every registration here is additive and nothing here overwrites anything.</strong> The container
/// already holds exactly one <see cref="ITransport"/> (Docker's, from <c>AddServyxDocker()</c>) and one
/// <see cref="IProvisioner"/> (Docker's, from <c>AddServyxDockerProvisioning()</c>). Only the second of those
/// is a set anyone reads with <c>GetServices</c>; <see cref="ITransport"/> is injected <em>singly</em> by
/// <see cref="ServyxBackupContextSource"/>, so a second <see cref="ITransport"/> registration would resolve
/// there and point Docker's backups at the wrong machine. That is not a hypothetical — it is the exact reason
/// <c>Program.cs</c> refuses to call <c>AddServyxSsh()</c> for SSH backups.
/// </para>
/// <para>
/// <strong>So this file registers no transport at all.</strong> The two transport-backed provisioners are
/// composed over transports constructed inline and handed to nobody else, which is why
/// <c>AddServyxSshProvisioning()</c> and <c>AddServyxProcessProvisioning()</c> are deliberately <em>not</em>
/// called: both resolve <see cref="ITransport"/> out of the container by <see cref="ITransport.TransportId"/>
/// and therefore require the matching <c>AddServyxSsh()</c> / <c>AddServyxLocalProcess()</c> call, each of
/// which registers the second <see cref="ITransport"/> the paragraph above forbids. The provisioner types
/// themselves are unchanged and still verify the transport id they were handed.
/// </para>
/// <para>
/// <strong>The four cloud adapters stay factory-composed, and that property is load-bearing.</strong>
/// <c>Servyx.Infrastructure.{DigitalOcean,Azure,Aws}</c> carry no <c>PackageReference</c> at all — including
/// no <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> and no
/// <c>Microsoft.Extensions.Logging.Abstractions</c> — which is what makes them the adapters that can hold no
/// logger and so have no reachable path that could log an API token, a client secret, or an AWS signing key.
/// Giving any of them an <c>IServiceCollection</c> extension would trade that for one registration line. They
/// are therefore constructed here, at the composition root, exactly as
/// <c>DigitalOceanSnapshotBackups.Create</c> argues for the backup half.
/// </para>
/// <para>
/// <strong>Each cloud provisioner gets its own <see cref="HttpClient"/>, and none is registered.</strong> A
/// shared client in the container would be precisely the silently-overwritten dependency this file exists to
/// avoid, and the three providers do not want the same handler anyway. Each client is private to the
/// provisioner that holds it, lives as long as that singleton, and uses a pooled connection lifetime so a
/// long-running process still follows DNS.
/// </para>
/// </remarks>
public static class ProvisionerComposition
{
    /// <summary>
    /// How long a pooled connection is reused before being re-established, so a process that runs for weeks
    /// still picks up a provider's DNS changes.
    /// </summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers one <see cref="IProvisioner"/> (and, where the adapter implements it, one
    /// <see cref="IMaintainer"/>) per provisioner <paramref name="options"/> found enabled.
    /// </summary>
    /// <remarks>
    /// Registers nothing at all for <see cref="ProvisionerWiringOptions.None"/>, which is what a closed
    /// provisioning gate and an unconfigured open one both produce — so calling this method unconditionally
    /// inside the gate leaves a host that configured nothing byte-for-byte as it was.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="options">The provisioners the operator enabled.</param>
    public static IServiceCollection AddServyxConfiguredProvisioners(
        this IServiceCollection services,
        ProvisionerWiringOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Ssh is { } ssh)
        {
            AddSsh(services, ssh);
        }

        if (options.Process is { } process)
        {
            AddProcess(services, process);
        }

        if (options.DigitalOcean is { } digitalOcean)
        {
            AddDigitalOcean(services, digitalOcean);
        }

        if (options.Azure is { } azure)
        {
            AddAzure(services, azure);
        }

        if (options.AwsEc2 is { } awsEc2)
        {
            AddAwsEc2(services, awsEc2);
        }

        if (options.AwsLightsail is { } awsLightsail)
        {
            AddAwsLightsail(services, awsLightsail);
        }

        return services;
    }

    /// <summary>
    /// Composes <see cref="SshProcessProvisioner"/> over an SSH transport this provisioner alone can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write guard is the same one <c>AddServyxSsh()</c> puts in front of the transport, over the same
    /// endpoint-scoped grant <c>AddServyxSshProvisioning()</c> registers — but the grant is handed straight to
    /// this transport's resolver instead of being registered as a container-wide
    /// <see cref="WriteModeGrant"/>. That is a deliberate narrowing: the SSH <em>backup</em> block builds its
    /// own <see cref="WriteGuardedTransport"/> over <c>GetServices&lt;WriteModeGrant&gt;()</c>, so a registered
    /// grant would also make backups writable at this endpoint without the operator ever setting
    /// <c>Servyx:Servers:&lt;name&gt;:WriteMode</c>. Marker writes are the only thing this grant needs to
    /// permit, and this is the only object that can use it.
    /// </para>
    /// </remarks>
    private static void AddSsh(IServiceCollection services, SshProvisionerOptions options)
    {
        services.AddSingleton(sp => new SshProcessProvisioner(
            new WriteGuardedTransport(
                new SshTransport(
                    sp.GetRequiredService<ISecretStore>(),
                    sp.GetRequiredService<IHostKeyVerifier>(),
                    sp.GetRequiredService<ILoggerFactory>()),
                new GrantedWriteModeResolver(
                [
                    new WriteModeGrant(
                        WriteMode.Enabled,
                        SshBackupWiringOptions.TransportId,
                        options.Endpoint),
                ])),
            options.Endpoint,
            options.CredentialUrn.Value,
            transportOptions: null,
            options.MarkerRoot));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<SshProcessProvisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<SshProcessProvisioner>());
    }

    /// <summary>
    /// Composes <see cref="LocalProcessProvisioner"/> over a local transport this provisioner alone can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write guard is the same one <c>AddServyxLocalProcess()</c> puts in front of the transport, over the
    /// same endpoint-scoped grant <c>AddServyxProcessProvisioning()</c> registers — but, exactly as
    /// <see cref="AddSsh"/> does, the grant is handed straight to this transport's resolver instead of being
    /// registered as a container-wide <see cref="WriteModeGrant"/>, so it can widen nothing but this one
    /// object. The transport is still never published as an <see cref="ITransport"/>.
    /// </para>
    /// <para>
    /// The grant's endpoint comes from <see cref="LocalProcessProvisioner.EndpointFor"/> rather than being
    /// spelled out here, because the provisioner constructed on the next line derives its own descriptors'
    /// endpoint from the same <c>options.MachineId</c> through that same function. A grant naming a different
    /// string would match nothing and silently leave every marker write refused.
    /// </para>
    /// </remarks>
    private static void AddProcess(IServiceCollection services, ProcessProvisionerOptions options)
    {
        services.AddSingleton(_ => new LocalProcessProvisioner(
            new WriteGuardedTransport(
                new LocalProcessTransport(),
                new GrantedWriteModeResolver(
                [
                    new WriteModeGrant(
                        WriteMode.Enabled,
                        LocalProcessTransport.Id,
                        LocalProcessProvisioner.EndpointFor(options.MachineId)),
                ])),
            options.MachineId,
            credentialUrn: null,
            transportOptions: null,
            options.MarkerRoot));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<LocalProcessProvisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<LocalProcessProvisioner>());
    }

    private static void AddDigitalOcean(IServiceCollection services, DigitalOceanProvisionerOptions options)
    {
        services.AddSingleton(sp => new DigitalOceanDropletProvisioner(
            CreateHttpClient(),
            sp.GetRequiredService<ISecretStore>(),
            options.ApiTokenUrn,
            options.SshCredentialUrn,
            transportOptions: null,
            options.SshUsername,
            sp.GetService<TimeProvider>()));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<DigitalOceanDropletProvisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<DigitalOceanDropletProvisioner>());
    }

    private static void AddAzure(IServiceCollection services, AzureProvisionerOptions options)
    {
        services.AddSingleton(sp => new AzureVirtualMachineProvisioner(
            CreateHttpClient(),
            sp.GetRequiredService<ISecretStore>(),
            options.ServicePrincipal,
            options.SubscriptionId,
            options.SshCredentialUrn,
            transportOptions: null,
            options.SshUsername,
            sp.GetService<TimeProvider>()));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<AzureVirtualMachineProvisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<AzureVirtualMachineProvisioner>());
    }

    private static void AddAwsEc2(IServiceCollection services, AwsEc2ProvisionerOptions options)
    {
        services.AddSingleton(sp => new AwsEc2Provisioner(
            CreateHttpClient(),
            sp.GetRequiredService<ISecretStore>(),
            options.Identity,
            options.Region,
            options.SshCredentialUrn,
            transportOptions: null,
            options.SshUsername,
            sp.GetService<TimeProvider>()));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<AwsEc2Provisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<AwsEc2Provisioner>());
    }

    private static void AddAwsLightsail(IServiceCollection services, AwsLightsailProvisionerOptions options)
    {
        services.AddSingleton(sp => new AwsLightsailProvisioner(
            CreateHttpClient(),
            sp.GetRequiredService<ISecretStore>(),
            options.Identity,
            options.Region,
            options.SshCredentialUrn,
            transportOptions: null,
            sp.GetService<TimeProvider>()));

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<AwsLightsailProvisioner>());
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<AwsLightsailProvisioner>());
    }

    /// <summary>
    /// One client per provisioner, owned by that provisioner and registered nowhere. See the type remarks.
    /// </summary>
    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime });
}
