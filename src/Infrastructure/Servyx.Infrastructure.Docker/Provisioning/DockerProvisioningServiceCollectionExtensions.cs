using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Servyx.Domain.Provisioning;

namespace Servyx.Infrastructure.Docker.Provisioning;

/// <summary>
/// Opt-in dependency-injection registration for Docker <em>provisioning</em>.
/// </summary>
public static class DockerProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="DockerContainerProvisioner"/> as an <see cref="IProvisioner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This method registers mutating capability, and is deliberately NOT part of the default
    /// read-only composition root.</strong> <c>AddServyxDocker()</c> registers only read-only Docker
    /// services — the transport, discovery, metrics, and log streaming — and nothing it registers can
    /// create, start, or remove a container. Everything reachable from
    /// <see cref="DockerContainerProvisioner"/>'s <see cref="IProvisioner"/> surface is likewise read-only
    /// (<c>PlanAsync</c> issues no Docker call at all; <c>RefreshAsync</c> inspects; <c>ReconcileAsync</c>
    /// lists), but <c>CreateOperation</c> and <c>DestroyAsync</c> are not, and a container created here is
    /// a real, running workload.
    /// </para>
    /// <para>
    /// Calling this method is therefore an explicit decision by a composition root to make container
    /// creation and destruction reachable. Do not fold it into <c>AddServyxDocker()</c> "for convenience":
    /// the separation is what lets anyone reading a composition root see, without tracing a dependency
    /// graph, whether that process can mutate infrastructure. Milestone 1 hosts must not call it.
    /// </para>
    /// <para>
    /// <strong>One endpoint, resolved once, used for both halves.</strong> This registration deliberately
    /// does <em>not</em> resolve the ambient <see cref="IDockerClient"/> that <c>AddServyxDocker()</c>
    /// registers. That client is built from <c>DOCKER_HOST</c> (or an OS default), which has nothing to do
    /// with <paramref name="endpoint"/>; pairing the two would mean the provisioner creates containers on one
    /// daemon while stamping a different daemon's address onto every <c>TargetDescriptor</c> — a silent,
    /// unrecoverable ledger corruption rather than a loud failure. Instead the endpoint is resolved exactly
    /// once here, and that single <see cref="Uri"/> is both handed to <see cref="IDockerClientFactory"/> to
    /// build the client and (as its verbatim <see cref="Uri.OriginalString"/>) stamped onto the descriptors.
    /// The two cannot diverge because there is only one of them.
    /// </para>
    /// <para>
    /// Requires an <see cref="IDockerClientFactory"/> and an <see cref="IDockerEnvironment"/> to already be
    /// registered — normally by <c>AddServyxDocker()</c>. The client this method builds is scoped to the
    /// provisioner and is not published as an <see cref="IDockerClient"/> registration, so nothing else in
    /// the container silently changes which daemon it talks to.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="endpoint">
    /// The Docker endpoint the provisioner both connects to and stamps onto every <c>TargetDescriptor</c> it
    /// produces. When <see langword="null"/>, <see cref="DockerEndpointResolver"/> resolves it the same way it
    /// does for a hand-configured target (<c>DOCKER_HOST</c>, then an OS default) — and the resolved value,
    /// not an empty string, is what descriptors carry, so a later change to <c>DOCKER_HOST</c> cannot
    /// retroactively re-point a resource Servyx has already recorded.
    /// </param>
    public static IServiceCollection AddServyxDockerProvisioning(this IServiceCollection services, string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var resolved = DockerEndpointResolver.Resolve(endpoint, sp.GetRequiredService<IDockerEnvironment>());
            var client = sp.GetRequiredService<IDockerClientFactory>().Create(resolved);

            return new DockerContainerProvisioner(client, resolved.OriginalString);
        });

        services.AddSingleton<IProvisioner>(sp => sp.GetRequiredService<DockerContainerProvisioner>());

        // The maintenance half — update planning and drift detection — rides on the same instance and is
        // published here rather than in AddServyxDocker(). Every member of IMaintainer is read-only, so this
        // line grants no new mutating capability; it is inside this method anyway because it is the same
        // object, built from the same single resolved endpoint, and splitting it out would let a composition
        // root acquire an IMaintainer pointed at a daemon other than the one the descriptors name. Keeping
        // it here also keeps maintenance behind Servyx:Provisioning:Enabled: a host with the flag off never
        // calls this method, so it has no IMaintainer at all, exactly as it has no IProvisioner.
        services.AddSingleton<IMaintainer>(sp => sp.GetRequiredService<DockerContainerProvisioner>());

        // Patch detection rides on the same instance for the same reasons, and is read-only for a stronger
        // one: it never pulls, so it grants no ability to write to the host's image store. It is published
        // here rather than in AddServyxDocker() so that a host with Servyx:Provisioning:Enabled off has no
        // IPatchDetector at all — and because a detector built from a different endpoint than the
        // descriptors name would answer "is a patch available?" about the wrong daemon's image store.
        services.AddSingleton<IPatchDetector>(sp => sp.GetRequiredService<DockerContainerProvisioner>());

        return services;
    }
}
