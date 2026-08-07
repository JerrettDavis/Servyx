using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Servyx.Infrastructure.Ssh.Tests.Integration;

/// <summary>
/// A throwaway container on the local Docker daemon standing in for a game server for lifecycle purposes —
/// something write-mutation tests can genuinely start, stop, restart, and kill without it being, or ever
/// being mistakable for, a real workload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Image choice: <c>busybox:latest</c>, run as <c>tail -f /dev/null</c>.</b> Busybox is a few hundred
/// kilobytes, pulls in seconds even cold, and both starts and restarts essentially instantly — important
/// because mutation tests start and stop it repeatedly. <c>tail -f /dev/null</c> is the standard idiom for
/// a container that stays running indefinitely while doing nothing: it never exits on its own, so
/// "restarted" and "still running" are unambiguous to observe, and it needs no game-specific bootstrap that
/// could itself fail and confound a lifecycle assertion. It carries no dependency on, and no resemblance to,
/// the real <c>thijsvanloef/palworld-server-docker</c> image production runs.
/// </para>
/// <para>
/// <b>Name generation:</b> <see cref="Name"/> is always <c>$"servyx-mutation-test-{Guid.NewGuid():N}"</c>,
/// computed inside <see cref="StartAsync"/>. There is no constructor, parameter, or configuration path
/// anywhere on this type by which a caller can supply a name — <see cref="MutationTargetGuardTests"/>
/// asserts that reflectively. The name is also registered with <see cref="MutationTargetGuard"/> before this
/// method returns, and unregistered unconditionally in <see cref="DisposeAsync"/>, which is what lets the
/// guard's layer 2 (live registry) work at all.
/// </para>
/// <para>
/// <b>The stand-in port:</b> <see cref="Port"/> binds <see cref="StandInPort"/> to a Testcontainers-assigned
/// random host port purely so <see cref="MutationTargetGuard"/>'s endpoint-pinning layer has a genuine,
/// live, ephemeral port to pin a target's endpoint to — nothing inside the container listens on it, since
/// mutation tests exercise container lifecycle, not network I/O against the workload itself.
/// </para>
/// <para>
/// <b>Disposal and the Ryuk reaper:</b> <see cref="DisposeAsync"/> unregisters the name first (in a
/// <see langword="finally"/>, so a container-removal failure can never leave a name the guard would still
/// approve) and then disposes the underlying Testcontainers <see cref="IContainer"/>, which stops and
/// removes it. Testcontainers' Ryuk reaper is enabled by default for this container (nothing in this type
/// or elsewhere in this test assembly sets <c>TESTCONTAINERS_RYUK_DISABLED</c>), so even a process that
/// crashes or is killed mid-test before <see cref="DisposeAsync"/> runs still has this container reaped
/// shortly after — a crashed run cannot leave it behind indefinitely.
/// </para>
/// </remarks>
internal sealed class DisposableWorkloadContainer : IAsyncDisposable
{
    private const string ImageName = "busybox:latest";
    private const int StandInPort = 7777;

    private readonly IContainer _container;
    private readonly IDisposable _registration;

    private DisposableWorkloadContainer(IContainer container, string name, string containerId, string host, int port, IDisposable registration)
    {
        _container = container;
        _registration = registration;
        Name = name;
        ContainerId = containerId;
        Host = host;
        Port = port;
    }

    /// <summary>Always <c>$"servyx-mutation-test-{Guid.NewGuid():N}"</c>. Generated here; never supplied.</summary>
    public string Name { get; }

    /// <summary>The Docker-assigned container id, as reported by Testcontainers after start.</summary>
    public string ContainerId { get; }

    /// <summary>The loopback host mutation tests should connect through (Testcontainers' own host).</summary>
    public string Host { get; }

    /// <summary>The Testcontainers-mapped host port for <see cref="StandInPort"/>.</summary>
    public int Port { get; }

    /// <summary>
    /// Starts a new, uniquely-named throwaway container and registers it with
    /// <see cref="MutationTargetGuard"/> before returning.
    /// </summary>
    public static async Task<DisposableWorkloadContainer> StartAsync(CancellationToken ct = default)
    {
        var name = $"{MutationTargetGuard.RequiredPrefix}{Guid.NewGuid():N}";

        var container = new ContainerBuilder(ImageName)
            .WithName(name)
            .WithCommand("tail", "-f", "/dev/null")
            .WithPortBinding(StandInPort, assignRandomHostPort: true)
            .Build();

        await container.StartAsync(ct).ConfigureAwait(false);

        var host = container.Hostname;
        var port = container.GetMappedPublicPort(StandInPort);
        var registration = MutationTargetGuard.Register(name, port);

        return new DisposableWorkloadContainer(container, name, container.Id, host, port, registration);
    }

    /// <summary>
    /// Inspects the container's REAL, live state through a fresh <see cref="DockerClient"/> that this
    /// harness creates itself — never through any Servyx transport or code under test — so assertions made
    /// against the result are independent of whatever the mutation being tested claims happened.
    /// </summary>
    public async Task<ContainerInspectResponse> InspectAsync(CancellationToken ct = default)
    {
        using var client = CreateIndependentDockerClient();
        return await client.Containers.InspectContainerAsync(ContainerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the container still exists on the daemon, via the same independent
    /// <see cref="DockerClient"/> as <see cref="InspectAsync"/>. Safe to call after <see cref="DisposeAsync"/>,
    /// since it depends only on the captured <see cref="ContainerId"/>, not on this instance's own state.
    /// </summary>
    public async Task<bool> StillExistsAsync(CancellationToken ct = default)
    {
        using var client = CreateIndependentDockerClient();
        try
        {
            await client.Containers.InspectContainerAsync(ContainerId, ct).ConfigureAwait(false);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
    }

    // Docker.DotNet.DockerClientConfiguration (the plain "Docker.DotNet" package Servyx.Infrastructure.Docker
    // compiles against) is not what this project's compiler actually binds "Docker.DotNet" to: Testcontainers
    // depends on the "Docker.DotNet.Enhanced" fork, which republishes the same assembly name/namespace at a
    // higher version, so ordinary assembly conflict resolution picks it for every "using Docker.DotNet;" in
    // this project (see the ReflectionTypeLoadException handling in TransportWriteGuardArchitectureTests for
    // the same fact observed from the other direction). DockerClientBuilder is that fork's equivalent entry
    // point — the same one Testcontainers itself builds its own daemon connection through — so using it here
    // needs no extern alias and stays genuinely independent of any Servyx code.
    private static DockerClient CreateIndependentDockerClient() => new DockerClientBuilder().Build();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _registration.Dispose();
        }
    }
}
