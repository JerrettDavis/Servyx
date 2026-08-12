using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servyx.Domain.Discovery;
using Servyx.Domain.Hosts;
using Servyx.Domain.Observability;
using Servyx.Domain.Transport;

namespace Servyx.Infrastructure.Ssh.Docker;

/// <summary>Dependency-injection registration for viewing a remote Docker container over SSH.</summary>
/// <remarks>
/// <para>
/// Mirrors <c>Servyx.Infrastructure.Ssh.ServiceCollectionExtensions.AddServyxSsh</c>'s guard shape exactly:
/// the only <see cref="ITransport"/> registered under the <see cref="ITransport"/> service type is a
/// <see cref="WriteGuardedTransport"/> wrapping <see cref="SshDockerTransport"/> wrapping
/// <see cref="SshTransport"/>, never a bare instance of either inner type under any service type.
/// <c>TransportWriteGuardArchitectureTests</c> asserts this the same way it does for every other Servyx
/// transport.
/// </para>
/// <para>
/// <strong>Discovery machinery is unconditional; the single-host surfaces are not.</strong> Unlike every
/// other registration in this method, <see cref="HostConnectionRegistry"/>, <see cref="IHostConnectionSource"/>,
/// and <see cref="CompositeServerDiscovery"/> (registered here as its own concrete type) are wired regardless
/// of whether <paramref name="options"/> declares a host, because <see cref="HostConnectionRegistry"/> also
/// fans out over database-registered hosts (see <see cref="Servyx.Domain.Hosts.IHostRepository"/>), and those
/// can be registered by an operator through the UI at any point <em>after</em> this DI composition already
/// ran. Without this unconditional registration, a fresh, zero-config install would have nothing in the
/// container for that later registration flow to attach to — see <see cref="HostConnectionRegistry"/>'s own
/// remarks. <see cref="CompositeServerDiscovery"/> is deliberately NOT registered under the
/// <see cref="IServerDiscovery"/> service type here; that swap — which fully replaces whatever
/// <c>AddServyxDocker</c> registered for local Docker discovery — still only happens when
/// <see cref="SshDockerWiringOptions.Any"/> is <see langword="true"/> (below), so a plain local-docker-only
/// install (the common case) keeps discovering local containers exactly as before. Wiring registered hosts
/// into that same discovery/adoption path for a zero-config install is later-increment scope.
/// </para>
/// <para>
/// <strong>Everything else stays no-op with nothing configured.</strong> When <paramref name="options"/> has
/// <see cref="SshDockerWiringOptions.Any"/> <see langword="false"/>, the <see cref="ITransport"/> service-type
/// registration, <see cref="TargetDescriptor"/>, <see cref="IExecutionTarget"/>, <see cref="IServerDiscovery"/>,
/// <see cref="ILogStream"/>, and <see cref="IMetricsSource"/> registrations below are all skipped — in
/// particular this does not touch whatever <c>AddServyxDocker</c> already registered for those service types.
/// Only a declared host causes any of that to run.
/// </para>
/// </remarks>
public static class SshDockerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ssh+docker transport, write-guarded, and — when <paramref name="options"/> declares a
    /// host — replaces the local Docker observation surface (<see cref="ITransport"/>,
    /// <see cref="IServerDiscovery"/>, <see cref="ILogStream"/>, <see cref="IMetricsSource"/>) with one
    /// backed by that remote host instead, plus the <see cref="TargetDescriptor"/> the dashboard's probe
    /// consumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Session lifetime.</strong> <see cref="SshDockerServerDiscovery"/>, <see cref="SshDockerLogStream"/>,
    /// and <see cref="SshDockerMetricsSource"/> each take an already-connected <see cref="IExecutionTarget"/>
    /// in their constructor — there is no lazy/async-factory shape in any of the three. A dependency-injection
    /// singleton factory is a synchronous <c>Func&lt;IServiceProvider, T&gt;</c>, so obtaining a genuinely
    /// connected session inside one would ordinarily force a choice between blocking the calling thread on
    /// <see cref="ITransport.ConnectAsync"/> or wrapping the three services behind an adapter that defers the
    /// connect.
    /// </para>
    /// <para>
    /// This registration takes neither. <see cref="LazyConnectingExecutionTarget"/> is a trivial-to-construct
    /// <see cref="IExecutionTarget"/> that captures a connect delegate and does nothing with it until the
    /// first real call — <see cref="IExecutionTarget.ExecuteAsync"/>, <see cref="IExecutionTarget.ExistsAsync"/>,
    /// etc. — at which point it awaits <see cref="ITransport.ConnectAsync"/> once (a
    /// <see cref="SemaphoreSlim"/>-guarded memoization; concurrent first callers all await the same connect
    /// rather than opening a session each) and forwards to the result from then on. Resolving
    /// <see cref="IServerDiscovery"/>/<see cref="ILogStream"/>/<see cref="IMetricsSource"/> from the container
    /// therefore constructs a real <see cref="SshDockerServerDiscovery"/> (etc.) synchronously and instantly —
    /// nothing here calls <c>.Result</c> or <c>.GetAwaiter().GetResult()</c> — and the SSH connection is opened
    /// lazily, on the same first-use schedule <c>ServyxSshBackupContextSource</c> already uses for its own
    /// cached sessions, not at startup or at DI-resolution time.
    /// </para>
    /// <para>
    /// <strong>Disposal.</strong> <see cref="LazyConnectingExecutionTarget"/> is registered as the
    /// <see cref="IExecutionTarget"/> service type as well, so its cached inner session — if one was ever
    /// opened — is disposed when the container is, the same pattern <c>ServyxBackupContextSource</c> and
    /// <c>ServyxSshBackupContextSource</c> use.
    /// </para>
    /// <para>
    /// <strong>Single host, except discovery.</strong> <see cref="ITransport"/>/<see cref="TargetDescriptor"/>/
    /// <see cref="IExecutionTarget"/>/<see cref="ILogStream"/>/<see cref="IMetricsSource"/> below are all wired
    /// from <c>options.Hosts[0]</c> only, and only when <see cref="SshDockerWiringOptions.Any"/> is
    /// <see langword="true"/>. <see cref="HostConnectionRegistry"/>/<see cref="IHostConnectionSource"/>/
    /// <see cref="CompositeServerDiscovery"/> are the exception: they are always registered (see this type's
    /// own remarks), and fan out across every entry in <c>options.Hosts</c> plus every enabled
    /// database-registered <see cref="Servyx.Domain.Entities.Host"/> row — see <see cref="SshDockerWiringOptions"/>'s
    /// remarks for the full scope line. Whether that fan-out also becomes the process-wide
    /// <see cref="IServerDiscovery"/> — displacing local Docker discovery — still depends on
    /// <see cref="SshDockerWiringOptions.Any"/>, exactly as it always has.
    /// </para>
    /// <para>
    /// <strong>Not silent about what it did.</strong> When a host is wired, this logs at
    /// <see cref="LogLevel.Information"/> which host key, endpoint, and container were used — never a secret
    /// value. <c>CredentialUrn</c> is a reference into <see cref="Servyx.Domain.Secrets.ISecretStore"/>, not
    /// the credential itself, so logging it is as safe as logging the endpoint.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddServyxSshDocker(
        this IServiceCollection services, SshDockerWiringOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        services.TryAddSingleton<IWriteModeResolver>(sp =>
            new GrantedWriteModeResolver(sp.GetServices<WriteModeGrant>()));

        // Unconditional: HostConnectionRegistry/IHostConnectionSource/CompositeServerDiscovery exist in the
        // container regardless of options.Any, because the registry also fans out across database-registered
        // hosts (IHostRepository), which can gain a row long after this DI composition ran — a later
        // host-registration flow needs something here to attach to even on a zero-config install. This does
        // NOT register CompositeServerDiscovery under the IServerDiscovery service type — that swap (which
        // fully displaces local Docker discovery) stays gated behind options.Any below, exactly as before, so
        // a plain local-docker-only install keeps its existing discovery untouched. BuildSshDockerTransport
        // constructs a private ssh+docker transport instance for the registry's own use — deliberately NOT the
        // same instance as the (conditionally registered) global ITransport below, so this registration never
        // depends on whether that later block ran.
        services.AddSingleton<HostConnectionRegistry>(sp => new HostConnectionRegistry(
            options,
            sp.GetRequiredService<IHostRepository>(),
            BuildSshDockerTransport(sp),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HostConnectionRegistry>()));
        services.AddSingleton<IHostConnectionSource>(sp => sp.GetRequiredService<HostConnectionRegistry>());

        // The same singleton again, under the one-method view a host-registration use case is allowed to hold
        // (see IHostConnectionRefresher). Registered here, beside the registry itself and equally
        // unconditionally, because "a host row was just written" can happen on any install — including the
        // zero-config one this whole block exists to keep wired.
        services.AddSingleton<IHostConnectionRefresher>(sp => sp.GetRequiredService<HostConnectionRegistry>());

        // The read-only host-key probe the registration flow shows an operator a fingerprint from, before
        // anything pins it. Stateless and trust-free by construction (see SshHostKeyProbe), so one instance for
        // the process lifetime is right, and registering it unconditionally costs nothing on an install that
        // never registers a host.
        services.TryAddSingleton<IHostKeyProbe>(_ => new SshHostKeyProbeAdapter());

        services.AddSingleton<CompositeServerDiscovery>(sp =>
            new CompositeServerDiscovery(sp.GetRequiredService<IHostConnectionSource>(), sp.GetService<ILoggerFactory>()));

        if (!options.Any)
        {
            return services;
        }

        var host = options.Hosts[0];

        logger.LogInformation(
            "ssh+docker: wired host '{HostKey}' — endpoint {Endpoint}, container {Container}, credential "
            + "{CredentialUrn}.",
            host.Name, host.Target.Endpoint, host.ContainerName,
            host.Target.CredentialUrn ?? "(none configured)");

        services.RemoveAll<ITransport>();
        services.AddSingleton<ITransport>(BuildSshDockerTransport);

        services.RemoveAll<TargetDescriptor>();
        services.AddSingleton(host.Target);

        services.RemoveAll<IExecutionTarget>();
        services.AddSingleton<IExecutionTarget>(sp => new LazyConnectingExecutionTarget(
            ct => sp.GetRequiredService<ITransport>().ConnectAsync(sp.GetRequiredService<TargetDescriptor>(), ct)));

        services.RemoveAll<IServerDiscovery>();
        services.AddSingleton<IServerDiscovery>(sp => sp.GetRequiredService<CompositeServerDiscovery>());

        services.RemoveAll<ILogStream>();
        services.AddSingleton<ILogStream>(sp =>
            new SshDockerLogStream(sp.GetRequiredService<IExecutionTarget>()));

        services.RemoveAll<IMetricsSource>();
        services.AddSingleton<IMetricsSource>(sp =>
            new SshDockerMetricsSource(sp.GetRequiredService<IExecutionTarget>()));

        return services;
    }

    /// <summary>
    /// Builds one write-guarded ssh+docker <see cref="ITransport"/> instance: <see cref="WriteGuardedTransport"/>
    /// wrapping <see cref="SshDockerTransport"/> wrapping <see cref="SshTransport"/>. Both
    /// <see cref="SshTransport"/> and <see cref="SshDockerTransport"/> are stateless with respect to any one
    /// host — <see cref="ITransport.ConnectAsync"/> takes the <see cref="TargetDescriptor"/> per call — so a
    /// fresh instance from this method is functionally interchangeable with any other; it is called once for
    /// the global <see cref="ITransport"/> registration (only when a host is configured) and once for
    /// <see cref="HostConnectionRegistry"/>'s own private transport (always), rather than making the registry
    /// depend on whichever instance the global registration happens to hold, if any.
    /// </summary>
    private static ITransport BuildSshDockerTransport(IServiceProvider sp) => new WriteGuardedTransport(
        new SshDockerTransport(
            ActivatorUtilities.CreateInstance<SshTransport>(sp),
            sp.GetService<ILoggerFactory>()),
        sp.GetRequiredService<IWriteModeResolver>());
}
