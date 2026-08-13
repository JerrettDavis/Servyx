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
/// <strong>Discovery machinery — including the <see cref="IServerDiscovery"/> service-type swap — is
/// unconditional; the single-host surfaces are not.</strong> Unlike every other registration in this
/// method, <see cref="HostConnectionRegistry"/>, <see cref="IHostConnectionSource"/>, and
/// <see cref="CompositeServerDiscovery"/> (registered here as its own concrete type) are wired regardless of
/// whether <paramref name="options"/> declares a host, because <see cref="HostConnectionRegistry"/> also
/// fans out over database-registered hosts (see <see cref="Servyx.Domain.Hosts.IHostRepository"/>), and those
/// can be registered by an operator through the UI at any point <em>after</em> this DI composition already
/// ran. <see cref="IServerDiscovery"/> itself is now ALSO always re-bound: when <paramref name="options"/>
/// declares a host, straight to <see cref="CompositeServerDiscovery"/>, exactly as before; when it does not,
/// to <see cref="HostAwareServerDiscovery"/>, which defers to whatever local discovery
/// <c>AddServyxDocker</c> registered for as long as <see cref="IHostConnectionSource"/> reports no host, and
/// switches to the composite fan-out — without a process restart — the moment a host is registered through
/// the UI. Earlier, this swap for the zero-config case never happened at all: a database-registered host's
/// containers would persist a row and invalidate <see cref="HostConnectionRegistry"/>'s cache, but
/// <see cref="IServerDiscovery"/> stayed bound to local Docker discovery for the process's whole lifetime, so
/// that host was never actually discoverable — see <see cref="HostAwareServerDiscovery"/>'s own remarks for
/// why plain <see cref="CompositeServerDiscovery"/> is not a safe unconditional replacement (it has no local
/// Docker fallback of its own, and a plain local-docker-only install is the common case).
/// </para>
/// <para>
/// <strong>Everything else stays no-op with nothing configured.</strong> When <paramref name="options"/> has
/// <see cref="SshDockerWiringOptions.Any"/> <see langword="false"/>, the <see cref="ITransport"/> service-type
/// registration, <see cref="TargetDescriptor"/>, <see cref="IExecutionTarget"/>, <see cref="ILogStream"/>, and
/// <see cref="IMetricsSource"/> registrations below are all skipped — in particular this does not touch
/// whatever <c>AddServyxDocker</c> already registered for those service types. <see cref="IServerDiscovery"/>
/// is the one exception — see the previous paragraph.
/// </para>
/// </remarks>
public static class SshDockerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ssh+docker transport, write-guarded; unconditionally re-binds
    /// <see cref="IServerDiscovery"/> to something host-aware (see the class remarks); and — when
    /// <paramref name="options"/> declares a host — additionally replaces the single-target observation
    /// surface (<see cref="ITransport"/>, <see cref="ILogStream"/>, <see cref="IMetricsSource"/>) with one
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
    /// remarks for the full scope line. <see cref="IServerDiscovery"/> is always re-bound too: to the
    /// composite fan-out directly when <see cref="SshDockerWiringOptions.Any"/> is <see langword="true"/>
    /// (unconditionally displacing local Docker discovery, exactly as before), or to
    /// <see cref="HostAwareServerDiscovery"/> otherwise, which defers to local Docker discovery until
    /// <see cref="IHostConnectionSource"/> reports a database-registered host and switches over dynamically
    /// from then on — see <see cref="HostAwareServerDiscovery"/>'s own remarks.
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
        // NOT itself register CompositeServerDiscovery under the IServerDiscovery service type — see below,
        // just past this block, for how (and unconditionally now, via HostAwareServerDiscovery when nothing
        // is statically declared) that swap actually happens without dropping local Docker discovery for a
        // plain local-docker-only install. BuildSshDockerTransport constructs a private ssh+docker transport
        // instance for the registry's own use — deliberately NOT the same instance as the (conditionally
        // registered) global ITransport below, so this registration never depends on whether that later block
        // ran.
        services.AddSingleton<HostConnectionRegistry>(sp => new HostConnectionRegistry(
            options,
            sp.GetRequiredService<IHostRepository>(),
            new LazyBuiltTransport(sp, BuildSshDockerTransport),
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

        // IServerDiscovery is always re-bound from here on, unlike the ITransport/TargetDescriptor/
        // IExecutionTarget/ILogStream/IMetricsSource surfaces further down (which stay untouched unless a
        // host is statically declared) — see this method's class-level remarks for why an unconditional swap
        // straight to CompositeServerDiscovery is not safe (it has no local Docker fallback of its own) and
        // why HostAwareServerDiscovery exists to cover the zero-config case instead. The prior registration
        // is captured BEFORE either branch below touches IServerDiscovery, so the zero-config branch can
        // still hand HostAwareServerDiscovery whatever local discovery AddServyxDocker already registered.
        var priorDiscoveryDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IServerDiscovery));
        var localDiscoveryFactory = priorDiscoveryDescriptor?.ImplementationFactory;

        if (options.Any)
        {
            // Statically declared: unconditionally displaces local Docker discovery for the process's whole
            // lifetime, exactly as this line always has, since before this fix existed.
            services.RemoveAll<IServerDiscovery>();
            services.AddSingleton<IServerDiscovery>(sp => sp.GetRequiredService<CompositeServerDiscovery>());
        }
        else
        {
            // Zero-config: defers to local Docker discovery until a host is registered through the UI, then
            // switches to the composite fan-out dynamically, without a process restart — see
            // HostAwareServerDiscovery's own remarks. Deliberately NOT RemoveAll'd: HostAwareServerDiscovery
            // needs the ORIGINAL registration (captured above) still available as its local fallback, and
            // this new registration is the LAST one added, so plain (non-enumerable) resolution still
            // returns it, exactly as a RemoveAll+Add pair would.
            services.AddSingleton<IServerDiscovery>(sp =>
            {
                var composite = sp.GetRequiredService<CompositeServerDiscovery>();
                if (localDiscoveryFactory is null)
                {
                    // Nothing registered a local discovery ahead of this call (AddServyxSshDocker used
                    // without AddServyxDocker first) — the composite is genuinely the only option.
                    return composite;
                }

                var local = (IServerDiscovery)localDiscoveryFactory(sp);
                return new HostAwareServerDiscovery(sp.GetRequiredService<IHostConnectionSource>(), composite, local);
            });
        }

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

    /// <summary>
    /// Defers invoking <paramref name="factory"/> — for <see cref="HostConnectionRegistry"/>'s own private
    /// transport, <see cref="BuildSshDockerTransport"/>, which calls
    /// <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/> for a real
    /// <see cref="SshTransport"/>, requiring <see cref="Servyx.Domain.Secrets.ISecretStore"/> and
    /// <see cref="IHostKeyVerifier"/> to be registered — until the first real <see cref="ProbeAsync"/> or
    /// <see cref="ConnectAsync"/> call.
    /// </summary>
    /// <remarks>
    /// <see cref="HostConnectionRegistry"/> is registered unconditionally (see this file's class remarks), and
    /// <see cref="HostAwareServerDiscovery"/> now always resolves it — merely to ask whether any host exists —
    /// on every install with no statically-declared host, not only ones that actually have one. Without this
    /// wrapper, that alone would require <see cref="Servyx.Domain.Secrets.ISecretStore"/>/<see cref="IHostKeyVerifier"/>
    /// to be registered on EVERY host composing <c>AddServyxSshDocker</c>, including ones that never register
    /// either — the MCP stdio host (<c>Servyx.Mcp.Stdio</c>) does not, because it authenticates its own
    /// transport rather than through the operator-password gate that registers secrets in
    /// <c>Servyx.Web</c>'s <c>Program.cs</c> (see <c>AddServyxOperatorAuthentication</c>). Deferred all the way
    /// to a real network call means those two services are only ever required at the point an install that
    /// actually has an ssh+docker host (configured or database-registered) tries to reach it — the same point
    /// it would already need them for that host to work at all.
    /// </remarks>
    private sealed class LazyBuiltTransport(IServiceProvider serviceProvider, Func<IServiceProvider, ITransport> factory) : ITransport
    {
        private readonly Lazy<ITransport> _inner = new(() => factory(serviceProvider));

        public string TransportId => _inner.Value.TransportId;

        public TransportCapabilities Capabilities => _inner.Value.Capabilities;

        public Task<TargetHealth> ProbeAsync(TargetDescriptor target, CancellationToken ct = default) =>
            _inner.Value.ProbeAsync(target, ct);

        public Task<IExecutionTarget> ConnectAsync(TargetDescriptor target, CancellationToken ct = default) =>
            _inner.Value.ConnectAsync(target, ct);
    }
}
